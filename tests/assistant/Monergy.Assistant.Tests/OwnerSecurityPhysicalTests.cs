using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Monergy.Platform;
using Monergy.Services.AiIntelligence.AssistantFeedback;
using Monergy.Services.Audit.PlatformAudit;
using Npgsql;
using Xunit;

namespace Monergy.Assistant.Tests;

[Collection("Assistant physical")]
[Trait("Category", "Physical")]
public sealed class OwnerSecurityPhysicalTests(PhysicalFixture fixture)
{
    [Theory]
    [InlineData("product")][InlineData("platform")][InlineData("ingest")][InlineData("review")][InlineData("identity")]
    [InlineData("evaluation")][InlineData("operator")][InlineData("ai-audit")][InlineData("am-audit")]
    public async Task EachWorkloadPurposeIsDeniedInEveryUnrelatedOwnerFamily(string purpose)
    {
        (string Url, string Header, string[] Allowed)[] families =
        [
            ("http://127.0.0.1:5119/internal/assistant-feedback/v1/topics", "X-Monergy-Assistant-Feedback", ["ingest"]),
            ("http://127.0.0.1:5119/internal/assistant-feedback/v1/review/status", "X-Monergy-Assistant-Feedback", ["review"]),
            ("http://127.0.0.1:5102/internal/platform-audit/v1/events", "X-Monergy-Platform-Audit", ["ai-audit", "am-audit"]),
            ("http://127.0.0.1:4173/internal/platform-identity/v1/validate", "X-Monergy-Platform-Identity", ["identity"]),
            ("http://127.0.0.1:5088/platform/v1/assistant-feedback/evaluate", "X-Monergy-AM-Platform", ["evaluation"]),
            ("http://127.0.0.1:5088/platform/v1/assistant-feedback/entitlements", "X-Monergy-AM-Platform-Operator", ["operator"]),
        ];
        foreach (var family in families.Where(value => !value.Allowed.Contains(purpose, StringComparer.Ordinal)))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, family.Url); request.Headers.Add(family.Header, fixture.Token(purpose));
            object body = family.Url.EndsWith("/evaluate", StringComparison.Ordinal)
                ? new PlatformContextValidation("opaque-test-context", "list", null)
                : family.Url.EndsWith("/entitlements", StringComparison.Ordinal)
                    ? new Monergy.AccessManagement.PlatformEntitlementCommand("1.0.0", Guid.NewGuid().ToString(), "A950", AssistantContract.Capability, "Grant", 1, "REVIEW_ASSIGNMENT")
                    : new { };
            request.Content = JsonContent.Create(body, options: AssistantContract.Json); using var response = await fixture.Client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"Purpose {purpose} at {family.Url} returned {(int)response.StatusCode}, expected 403.");
            Assert.DoesNotContain("question", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("platform.localhost", "/assistant-api/local/v1/status", 404)]
    [InlineData("127.0.0.1", "/platform-api/local/v1/assistant/status", 404)]
    [InlineData("unknown.localhost", "/platform-api/local/v1/assistant/status", 404)]
    [InlineData("platform.localhost", "/platform-api/local/v1/assistant/status", 401)]
    public async Task BrowserHostAndSessionBoundaryRejectsBeforeOwnerAccess(string host, string path, int expected)
    {
        using var request = fixture.BrowserRequest(HttpMethod.Get, path, host, false);
        using var response = await fixture.Client.SendAsync(request); Assert.Equal(expected, (int)response.StatusCode);
    }

    [Fact]
    public async Task PlatformHelpStatusSurvivesRuntimeOutageAndReviewerDenialWithoutLogout()
    {
        await fixture.GrantAsync(false);
        using var request = fixture.BrowserRequest(HttpMethod.Get, "/platform-api/local/v1/assistant/status");
        using var response = await fixture.Client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(status.GetProperty("available").GetBoolean()); Assert.False(status.GetProperty("reviewAllowed").GetBoolean());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        using var deniedRequest = fixture.BrowserRequest(HttpMethod.Get, "/platform-api/local/v1/assistant/review/topics");
        using var denied = await fixture.Client.SendAsync(deniedRequest); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.NotEmpty(await fixture.ContextAsync("list")); await fixture.GrantAsync();
    }

    [Theory]
    [InlineData("read")][InlineData("update")]
    public async Task RevokeWhileWaitingForOwnerTransactionPreventsReleaseOrCommit(string kind)
    {
        await fixture.GrantAsync(); var store = fixture.Store(); using var authority = fixture.Authority();
        var saved = await store.SubmitAsync(fixture.Submission(), default);
        await using var blocker = await fixture.OpenAsync("ai"); await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("SELECT pg_advisory_xact_lock(7101201)", transaction: transaction);
        var operation = Guid.NewGuid().ToString();
        Task pending = kind == "read"
            ? store.ReadAsync(new(await fixture.ContextAsync("read", saved.GapId), saved.GapId, null, 1, null, null, null, null), authority, default)
            : store.ChangeAsync(new(await fixture.ContextAsync("status", saved.GapId), saved.GapId, null, 1, null, null, operation,
                new("1.0.0", 1, "Under review", "Knowledge gap", false, null, null, null, false, null)), authority, default);
        var waiting = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            waiting = await blocker.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid=7101201 AND NOT granted AND database=(SELECT oid FROM pg_database WHERE datname=current_database()))", transaction: transaction);
            if (waiting) break; await Task.Delay(25);
        }
        Assert.True(waiting); await fixture.GrantAsync(false); await transaction.CommitAsync();
        var error = await Assert.ThrowsAsync<TenantBoundaryException>(async () => await pending); Assert.Equal(403, error.Status);
        Assert.Equal(1, await blocker.ExecuteScalarAsync<long>("SELECT revision FROM ai_feedback.topics WHERE gap_id=@id", new { id = Guid.Parse(saved.GapId) }));
        Assert.Equal(0, await blocker.ExecuteScalarAsync<long>("SELECT count(*) FROM ai_feedback.audit_outbox WHERE payload->>'operationId'=@operation", new { operation }));
        await fixture.GrantAsync();
    }

    [Fact]
    public async Task OutboxPermissionFailureRollsBackReviewStateAndIdempotency()
    {
        await fixture.GrantAsync(); var store = fixture.Store(); using var authority = fixture.Authority();
        var saved = await store.SubmitAsync(fixture.Submission(), default); var operation = Guid.NewGuid().ToString();
        var request = new FeedbackReviewRequest(await fixture.ContextAsync("status", saved.GapId), saved.GapId, null, 1, null, null, operation,
            new("1.0.0", 1, "Under review", "Knowledge gap", false, null, null, null, false, null));
        await using var owner = await fixture.OpenAsync("ai"); var role = new NpgsqlConnectionStringBuilder(fixture.Connection("ai")).Username!;
        await owner.ExecuteAsync($"REVOKE INSERT ON ai_feedback.audit_outbox FROM {role}");
        try { var error = await Assert.ThrowsAsync<PostgresException>(() => store.ChangeAsync(request, authority, default)); Assert.Equal("42501", error.SqlState); }
        finally { await owner.ExecuteAsync($"GRANT INSERT ON ai_feedback.audit_outbox TO {role}"); }
        Assert.Equal(1, await owner.ExecuteScalarAsync<long>("SELECT revision FROM ai_feedback.topics WHERE gap_id=@id", new { id = Guid.Parse(saved.GapId) }));
        Assert.Equal(0, await owner.ExecuteScalarAsync<long>("SELECT count(*) FROM ai_feedback.operations WHERE operation_key=@key", new { key = AssistantContract.Hash("review|A950|" + operation) }));
        await store.ChangeAsync(request, authority, default);
        Assert.Equal(2, await owner.ExecuteScalarAsync<long>("SELECT revision FROM ai_feedback.topics WHERE gap_id=@id", new { id = Guid.Parse(saved.GapId) }));
    }

    [Theory]
    [InlineData(null)][InlineData("not a connection string")]
    public async Task OptionalMalformedOrMissingStoresRemainUnavailableWithoutWorkerTermination(string? connection)
    {
        var settings = fixture.Modified(("Monergy:AssistantFeedback:ConnectionString", connection), ("Monergy:PlatformAudit:ConnectionString", connection));
        var store = fixture.Store(settings); Assert.False(await store.ReadyAsync(default));
        Assert.False(await new PlatformAuditStore(settings).ReadyAsync(default));
        using var worker = new FeedbackMaintenance(store, settings, NullLogger<FeedbackMaintenance>.Instance);
        using var client = new HttpClient(); await worker.RunCycleAsync(client, default);
        using var request = fixture.BrowserRequest(HttpMethod.Get, "/platform-api/local/v1/assistant/status");
        using var response = await fixture.Client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await fixture.Store().ReadyAsync(default));
    }

    [Fact]
    public async Task SyntheticApprovedPublishedReceiptResolvesOnlyBoundKnowledgeAndReadDoesNotRenewExpiry()
    {
        await fixture.GrantAsync(); using var catalogue = fixture.Catalogue();
        var resolution = new TopicResolution("SYNTHETIC-ISOLATED-APPROVAL", catalogue.KnowledgeRevision, "SYNTHETIC-ISOLATED-VERIFICATION");
        var path = Path.Combine(fixture.FixtureFolder, "synthetic-publication.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { contractVersion = "1.0.0", knowledgeRevision = catalogue.KnowledgeRevision,
            retrievalRevision = catalogue.RetrievalRevision, approvedChanges = new[] { resolution } }, AssistantContract.Json);
        await File.WriteAllBytesAsync(path, bytes);
        var store = fixture.Store(fixture.Modified(("Monergy:AssistantFeedback:PublishedResolutionsPath", path),
            ("Monergy:AssistantFeedback:PublishedResolutionsSha256", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)))));
        var saved = await store.SubmitAsync(fixture.Submission(), default); using var authority = fixture.Authority();
        await store.ChangeAsync(new(await fixture.ContextAsync("resolution", saved.GapId), saved.GapId, null, 1, null, null, Guid.NewGuid().ToString(),
            new("1.0.0", 1, "Resolved", "Knowledge gap", true, null, null, null, false, resolution)), authority, default);
        var request = new FeedbackReviewRequest(await fixture.ContextAsync("read", saved.GapId), saved.GapId, null, 1, null, null, null, null);
        var first = Assert.Single((await store.ReadAsync(request, authority, default)).Topics);
        var second = Assert.Single((await store.ReadAsync(request, authority, default)).Topics);
        Assert.Equal("Resolved", first.Status); Assert.Equal(first.CreatedAt.AddDays(180), first.ExpiresAt); Assert.Equal(first.ExpiresAt, second.ExpiresAt);
    }

    [Fact]
    public async Task KnownTopicCountsKeepExplicitSubmissionsSeparateFromAnonymousMatchedOutcomes()
    {
        await fixture.GrantAsync(); using var catalogue = fixture.Catalogue();
        var topic = catalogue.Scope("product").GetProperty("topicIds").EnumerateObject().First().Value.GetString()!;
        var store = fixture.Store(); var before = fixture.Submission(topic: topic); var first = await store.SubmitAsync(before, default);
        await store.SubmitAsync(fixture.Submission(topic: topic), default);
        var counter = new FeedbackCounter("1.0.0", "product", "ANSWERED", topic, catalogue.KnowledgeRevision, catalogue.RetrievalRevision);
        for (var index = 0; index < 3; index++) await store.CountAsync(counter, default);
        using var authority = fixture.Authority(); var item = Assert.Single((await store.ReadAsync(new(await fixture.ContextAsync("read", first.GapId), first.GapId, null, 1, null, null, null, null), authority, default)).Topics);
        Assert.Equal(2, item.TopicSubmissionCount); Assert.Equal(3, item.MatchedOutcomeCount);
        await using var connection = await fixture.OpenAsync("ai");
        var columns = await connection.QueryAsync<string>("SELECT column_name FROM information_schema.columns WHERE table_schema='ai_feedback' AND table_name='counters'");
        Assert.DoesNotContain(columns, column => column.Contains("question", StringComparison.Ordinal) || column.Contains("actor", StringComparison.Ordinal) || column.Contains("tenant", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FullPendingOutboxRollsBackMutationButKeepsBoundedRecoveryDiscoveryAvailable()
    {
        await fixture.GrantAsync(); var store = fixture.Store(); using var authority = fixture.Authority();
        var saved = await store.SubmitAsync(fixture.Submission(), default);
        await using var owner = await fixture.OpenAsync("ai"); var marker = Guid.NewGuid().ToString();
        var operation = Guid.NewGuid().ToString();
        // Synthetic capacity pressure exists only in this disposable test database. It is not accepted evidence.
        await owner.ExecuteAsync("INSERT INTO ai_feedback.audit_outbox(event_id,payload,attempt_count) SELECT gen_random_uuid(),jsonb_build_object('operationId',@marker,'action','SyntheticCapacityFixture'),16 FROM generate_series(1,GREATEST(0,10000-(SELECT count(*)::int FROM ai_feedback.audit_outbox WHERE delivered_at IS NULL)))", new { marker });
        try
        {
            var context = await fixture.ContextAsync("status", saved.GapId);
            var error = await Assert.ThrowsAsync<TenantBoundaryException>(() => store.ChangeAsync(new(context, saved.GapId, null, 1, null, null, operation,
                new("1.0.0", 1, "Under review", "Knowledge gap", false, null, null, null, false, null)), authority, default));
            Assert.Equal(503, error.Status);
            Assert.Equal(1, await owner.ExecuteScalarAsync<long>("SELECT revision FROM ai_feedback.topics WHERE gap_id=@id", new { id = Guid.Parse(saved.GapId) }));
            Assert.Equal(0, await owner.ExecuteScalarAsync<long>("SELECT count(*) FROM ai_feedback.operations WHERE operation_key=@key", new { key = AssistantContract.Hash("review|A950|" + operation) }));
            Assert.NotEmpty((await store.DiscoverAuditAsync(new(await fixture.ContextAsync("list"), null, null, 20, null), authority, default)).Events);
        }
        finally
        {
            // Remove only rows this test fabricated for capacity pressure, never genuine owner events.
            await owner.ExecuteAsync("DELETE FROM ai_feedback.audit_outbox WHERE payload->>'operationId'=@marker AND payload->>'action'='SyntheticCapacityFixture'", new { marker });
        }
    }

    [Fact]
    public async Task AccessManagementRecoveryEnvelopeReceivesMatchingImmutableAuditReceipt()
    {
        var envelope = new PlatformAuditEnvelope("1.0.0", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), AssistantContract.Realm,
            "am-platform", "OperatorWorkload", "local-reviewer-operator", AssistantContract.Capability, null, 1, 0, null,
            "ReviewerAuditRecovery", "Committed", "AuditRecovery", Guid.NewGuid().ToString(), "platform", DateTimeOffset.UtcNow);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:5102/internal/platform-audit/v1/events");
        request.Headers.Add("X-Monergy-Platform-Audit", fixture.Token("am-audit")); request.Content = JsonContent.Create(envelope, options: AssistantContract.Json);
        using var response = await fixture.Client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<PlatformAuditReceipt>(AssistantContract.Json);
        Assert.NotNull(receipt); Assert.Equal(envelope.EventId, receipt.EventId); Assert.Equal("Accepted", receipt.State);
    }
}
