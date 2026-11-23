using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Monergy.Platform;
using Monergy.Services.AiIntelligence.AssistantFeedback;
using Monergy.Services.Audit.PlatformAudit;
using Monergy.UatGateway;
using Npgsql;
using Xunit;

namespace Monergy.Assistant.Tests;

[Collection("Assistant physical")]
[Trait("Category", "Physical")]
public sealed class FeedbackPhysicalTests(PhysicalFixture fixture)
{
    private static TopicChange Change(long revision = 1, string status = "Under review", string? duplicate = null, bool approved = false, string? topic = null,
        bool delete = false, TopicResolution? resolution = null) => new("1.0.0", revision, status, "Knowledge gap", approved, topic, null, duplicate, delete, resolution);
    private async Task<FeedbackSaved> UpdateAsync(string gap, TopicChange change, string? operation = null)
    {
        var op = change.PrivacyDelete ? "privacy-delete" : change.Resolution is not null ? "resolution" : "status";
        using var authority = fixture.Authority();
        return await fixture.Store().ChangeAsync(new(await fixture.ContextAsync(op, Guid.Parse(gap).ToString()), gap, null, 1, null, null,
            operation ?? Guid.NewGuid().ToString(), change), authority, default);
    }
    private async Task<HelpTopic> TopicAsync(string gap)
    {
        await using var connection = await fixture.OpenAsync("ai");
        return JsonSerializer.Deserialize<HelpTopic>(await connection.QuerySingleAsync<string>("SELECT payload::text FROM ai_feedback.topics WHERE gap_id=@id", new { id = Guid.Parse(gap) }), AssistantContract.Json)!;
    }
    private static string DuplicateSavedReceipt(string field, string gap, string auditState)
    {
        var receipt = JsonSerializer.Serialize(new FeedbackSaved(true, gap, auditState), AssistantContract.Json);
        var contradiction = field switch
        {
            "saved" => "\"saved\":false,",
            "gapId" => "\"gapId\":\"11111111-1111-4111-8111-111111111111\",",
            "auditState" => "\"auditState\":\"Rejected\",",
            _ => throw new ArgumentException("Unexpected receipt mutation.", nameof(field)),
        };
        // The final occurrence is otherwise a plausible successful receipt, so
        // last-property-wins deserialization cannot establish a safe outcome.
        return "{" + contradiction + receipt[1..];
    }

    [Fact]
    public async Task ThreeOwnerMigrationsAndRuntimeLeastPrivilegesArePhysical()
    {
        foreach (var service in new[] { "ai", "audit", "am" })
        {
            await using var runtime = await fixture.OpenAsync(service, false);
            var schema = service switch { "ai" => "ai_feedback", "audit" => "audit_platform", _ => "am_platform" };
            Assert.True(await runtime.ExecuteScalarAsync<bool>("SELECT NOT has_schema_privilege(current_user,@schema,'CREATE')", new { schema }));
            Assert.False(await runtime.ExecuteScalarAsync<bool>("SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls FROM pg_roles WHERE rolname=current_user"));
            await using var owner = await fixture.OpenAsync(service);
            Assert.True(await owner.ExecuteScalarAsync<long>("SELECT count(*) FROM public.assistant_test_migrations") >= 1);
        }
        await using var audit = await fixture.OpenAsync("audit", false);
        foreach (var action in new[] { "UPDATE", "DELETE", "TRUNCATE" })
            Assert.False(await audit.ExecuteScalarAsync<bool>("SELECT has_table_privilege(current_user,'audit_platform.evidence',@action)", new { action }));
        var forbidden = await Assert.ThrowsAsync<PostgresException>(() => audit.ExecuteAsync("DELETE FROM audit_platform.evidence"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, forbidden.SqlState);
        await using var ai = await fixture.OpenAsync("ai", false);
        Assert.False(await ai.ExecuteScalarAsync<bool>("SELECT has_column_privilege(current_user,'ai_feedback.audit_outbox','payload','UPDATE')"));
        Assert.False(await ai.ExecuteScalarAsync<bool>("SELECT has_table_privilege(current_user,'ai_feedback.audit_outbox','DELETE')"));
        await Assert.ThrowsAsync<PostgresException>(() => ai.ExecuteAsync("CREATE TABLE ai_feedback.forbidden(x int)"));
        Assert.False(await ai.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.schemata WHERE schema_name IN ('am_platform','audit_platform'))"));
    }

    [Fact]
    public async Task SubmissionReplayIsOneDurableTopicAndDoesNotRenewExpiry()
    {
        var submission = fixture.Submission(); var store = fixture.Store();
        var first = await store.SubmitAsync(submission, default); var topic = await TopicAsync(first.GapId);
        var replay = await store.SubmitAsync(submission, default);
        Assert.Equal(first, replay); Assert.Equal(topic.ExpiresAt, (await TopicAsync(first.GapId)).ExpiresAt);
        await using var connection = await fixture.OpenAsync("ai");
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.topics WHERE gap_id=@id", new { id = Guid.Parse(first.GapId) }));
        var conflict = await Assert.ThrowsAsync<TenantBoundaryException>(() => store.SubmitAsync(submission with { GeneralQuestion = "Another general question about the product?" }, default));
        Assert.Equal(409, conflict.Status);
    }

    [Fact]
    public async Task ConcurrentSameOperationProducesExactlyOneTopic()
    {
        var submission = fixture.Submission(); var store = fixture.Store();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => store.SubmitAsync(submission, default)));
        Assert.Single(results.Select(result => result.GapId).Distinct(StringComparer.Ordinal));
        await using var connection = await fixture.OpenAsync("ai");
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.operations WHERE operation_key=@key", new { key = submission.IdempotencyKey }));
    }

    [Theory]
    [InlineData("D")][InlineData("N")][InlineData("UPPER")]
    public async Task SelfDuplicateUuidVariantsAreRejected(string format)
    {
        await fixture.GrantAsync();
        var saved = await fixture.Store().SubmitAsync(fixture.Submission(), default); var id = Guid.Parse(saved.GapId);
        var duplicate = format == "UPPER" ? id.ToString().ToUpperInvariant() : id.ToString(format);
        var denied = await Assert.ThrowsAsync<TenantBoundaryException>(() => UpdateAsync(saved.GapId, Change(status: "Duplicate", duplicate: duplicate)));
        Assert.Equal("HELP_TOPIC_DUPLICATE_INVALID", denied.Code); Assert.Equal(1, (await TopicAsync(saved.GapId)).Revision);
    }

    [Fact]
    public async Task DifferentDuplicateReferenceIsNormalizedAndAuditHasNoQuestionText()
    {
        await fixture.GrantAsync(); var store = fixture.Store();
        var target = await store.SubmitAsync(fixture.Submission(), default); var source = await store.SubmitAsync(fixture.Submission(), default);
        await UpdateAsync(source.GapId, Change(status: "Duplicate", duplicate: Guid.Parse(target.GapId).ToString("N").ToUpperInvariant()));
        var updated = await TopicAsync(source.GapId); Assert.Equal(target.GapId, updated.DuplicateOf); Assert.Equal(2, updated.Revision);
        await using var connection = await fixture.OpenAsync("ai");
        var evidence = await connection.QuerySingleAsync<string>("SELECT payload::text FROM ai_feedback.audit_outbox WHERE payload->>'resourceId'=@id AND payload->>'action'='FeedbackDuplicate'", new { id = source.GapId });
        Assert.DoesNotContain(updated.GeneralQuestion, evidence, StringComparison.Ordinal); Assert.DoesNotContain("authenticationContext", evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-token")][InlineData("short-token")][InlineData("invalid-url")][InlineData("unreachable")]
    public async Task RetentionWorksDespiteBrokenAuditTransportConfiguration(string failure)
    {
        var settings = failure switch
        {
            "missing-token" => fixture.Modified(("Monergy:AssistantFeedback:AuditToken", null)),
            "short-token" => fixture.Modified(("Monergy:AssistantFeedback:AuditToken", "short")),
            "invalid-url" => fixture.Modified(("Monergy:AssistantFeedback:AuditUrl", "not-a-url")),
            _ => fixture.Configuration,
        };
        var store = fixture.Store(settings);
        var expired = await store.SubmitAsync(fixture.Submission(), default);
        var approvedExpired = await store.SubmitAsync(fixture.Submission(), default);
        var retained = await store.SubmitAsync(fixture.Submission(), default);
        await using (var connection = await fixture.OpenAsync("ai"))
        {
            await connection.ExecuteAsync("UPDATE ai_feedback.topics SET expires_at=clock_timestamp()-interval '1 day',created_at=clock_timestamp()-interval '31 days' WHERE gap_id=@id; UPDATE ai_feedback.operations SET expires_at=clock_timestamp()-interval '1 day' WHERE gap_id=@id", new { id = Guid.Parse(expired.GapId) });
            await connection.ExecuteAsync("UPDATE ai_feedback.topics SET expires_at=clock_timestamp()-interval '1 day',created_at=clock_timestamp()-interval '181 days',payload=jsonb_set(payload,'{approvedGeneralTopic}','true') WHERE gap_id=@id", new { id = Guid.Parse(approvedExpired.GapId) });
            await connection.ExecuteAsync("INSERT INTO ai_feedback.counters(day,source_audience,category,intent,knowledge_revision,retrieval_revision,count) VALUES((clock_timestamp() AT TIME ZONE 'UTC')::date-90,'product','UNKNOWN','UNCLASSIFIED',@k,@r,1) ON CONFLICT DO NOTHING", new { k = new string('a', 64), r = new string('b', 64) });
        }
        await fixture.GrantAsync(); using var authority = fixture.Authority();
        await fixture.Store().ReadAsync(new(await fixture.ContextAsync("list"), null, null, 1, null, null, null, null), authority, default);
        using var transport = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        using var worker = new FeedbackMaintenance(store, settings, NullLogger<FeedbackMaintenance>.Instance);
        fixture.AuditTransportUnavailable = failure == "unreachable";
        try { await worker.RunCycleAsync(transport, default); }
        finally { fixture.AuditTransportUnavailable = false; }
        await using var read = await fixture.OpenAsync("ai");
        Assert.Equal(0, await read.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.topics WHERE gap_id=ANY(@ids)", new { ids = new[] { Guid.Parse(expired.GapId), Guid.Parse(approvedExpired.GapId) } }));
        Assert.Equal(0, await read.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.operations WHERE gap_id=@id", new { id = Guid.Parse(expired.GapId) }));
        Assert.Equal(0, await read.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.counters WHERE day<=(clock_timestamp() AT TIME ZONE 'UTC')::date-90"));
        Assert.Equal(1, (await TopicAsync(retained.GapId)).Revision);
        Assert.True(await store.ReadyAsync(default));
    }

    [Fact]
    public async Task LostPostCommitResponseIsUnknownAndSameOperationRecoversOneTopic()
    {
        var submission = fixture.Submission(); fixture.LoseNextOwnerResponse = true;
        using var gateway = new AssistantFeedbackClient(fixture.Configuration);
        await Assert.ThrowsAsync<AssistantOutcomeUnknownException>(() => gateway.SendAsync<FeedbackSaved>("topics", submission, false, default));
        await using var connection = await fixture.OpenAsync("ai");
        var gap = await connection.QuerySingleAsync<Guid>("SELECT gap_id FROM ai_feedback.operations WHERE operation_key=@key", new { key = submission.IdempotencyKey });
        var before = await TopicAsync(gap.ToString());
        var confirmed = await gateway.SendAsync<FeedbackSaved>("topics", submission, false, default);
        Assert.True(confirmed.Saved); Assert.Equal(gap.ToString(), confirmed.GapId);
        Assert.Equal(before.ExpiresAt, (await TopicAsync(confirmed.GapId)).ExpiresAt);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.topics WHERE gap_id=@gap", new { gap }));
    }

    [Theory]
    [InlineData(503, "{\"outcome\":17}")]
    [InlineData(503, "{\"error\":\"FEEDBACK_UNAVAILABLE\",\"outcome\":\"UNKNOWN\",\"outcome\":\"REJECTED\",\"saved\":false}")]
    [InlineData(400, "{\"error\":\"FEEDBACK_UNAVAILABLE\",\"outcome\":\"UNKNOWN\",\"outcome\":\"REJECTED\",\"saved\":false}")]
    [InlineData(503, "{\"error\":\"FEEDBACK_UNAVAILABLE\",\"outcome\":\"REJECTED\",\"saved\":true}")]
    [InlineData(503, "{\"outcome\":\"REJECTED\"}")]
    [InlineData(503, "{\"error\":\"FEEDBACK_UNAVAILABLE\",\"outcome\":\"REJECTED\",\"saved\":false,\"unexpected\":true}")]
    [InlineData(503, "[]")]
    [InlineData(503, "{")]
    public async Task MalformedPostCommitFailureCannotClaimRejectionAndRetryRecoversOneTopic(int status, string body)
    {
        var submission = fixture.Submission(); fixture.NextOwnerFailure = (status, body);
        using var gateway = new AssistantFeedbackClient(fixture.Configuration);
        await Assert.ThrowsAsync<AssistantOutcomeUnknownException>(() => gateway.SendAsync<FeedbackSaved>("topics", submission, false, default));
        await using var connection = await fixture.OpenAsync("ai");
        var gap = await connection.QuerySingleAsync<Guid>("SELECT gap_id FROM ai_feedback.operations WHERE operation_key=@key", new { key = submission.IdempotencyKey });
        var original = await TopicAsync(gap.ToString());
        var replay = await gateway.SendAsync<FeedbackSaved>("topics", submission, false, default);
        Assert.True(replay.Saved); Assert.Equal(gap.ToString(), replay.GapId);
        Assert.Equal(original.ExpiresAt, (await TopicAsync(replay.GapId)).ExpiresAt);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.topics WHERE gap_id=@gap", new { gap }));
    }

    [Theory]
    [InlineData("saved")][InlineData("gapId")][InlineData("auditState")]
    public async Task DuplicateSuccessReceiptIsUnknownAndSameSubmissionRecoversOneTopic(string field)
    {
        var submission = fixture.Submission();
        fixture.NextOwnerFailure = (200, DuplicateSavedReceipt(field, "22222222-2222-4222-8222-222222222222", "NotApplicable"));
        using var gateway = new AssistantFeedbackClient(fixture.Configuration);
        await Assert.ThrowsAsync<AssistantOutcomeUnknownException>(() => gateway.SendAsync<FeedbackSaved>("topics", submission, false, default));
        await using var connection = await fixture.OpenAsync("ai");
        var gap = await connection.QuerySingleAsync<Guid>("SELECT gap_id FROM ai_feedback.operations WHERE operation_key=@key", new { key = submission.IdempotencyKey });
        var original = await TopicAsync(gap.ToString());
        var replay = await gateway.SendAsync<FeedbackSaved>("topics", submission, false, default);
        Assert.True(replay.Saved); Assert.Equal(gap.ToString(), replay.GapId); Assert.Equal("NotApplicable", replay.AuditState);
        var current = await TopicAsync(replay.GapId);
        Assert.Equal(1, current.Revision); Assert.Equal(original.CreatedAt, current.CreatedAt); Assert.Equal(original.ExpiresAt, current.ExpiresAt);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.topics WHERE gap_id=@gap", new { gap }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.operations WHERE operation_key=@key", new { key = submission.IdempotencyKey }));
    }

    [Theory]
    [InlineData("saved")][InlineData("gapId")][InlineData("auditState")]
    public async Task DuplicateReviewSuccessReceiptIsUnknownAndRetryDoesNotRepeatMutation(string field)
    {
        await fixture.GrantAsync();
        var saved = await fixture.Store().SubmitAsync(fixture.Submission(), default);
        var original = await TopicAsync(saved.GapId); var operation = Guid.NewGuid().ToString();
        var request = new FeedbackReviewRequest(await fixture.ContextAsync("status", saved.GapId), saved.GapId, null, 1, null, null, operation, Change());
        fixture.LoseNextOwnerResponsePath = "/internal/assistant-feedback/v1/review/change";
        fixture.NextOwnerFailure = (200, DuplicateSavedReceipt(field, saved.GapId, "Pending"));
        using var gateway = new AssistantFeedbackClient(fixture.Configuration);
        await Assert.ThrowsAsync<AssistantOutcomeUnknownException>(() => gateway.SendAsync<FeedbackSaved>("review/change", request, true, default));
        var committed = await TopicAsync(saved.GapId);
        Assert.Equal(2, committed.Revision); Assert.Equal(original.ExpiresAt, committed.ExpiresAt);
        var replay = await gateway.SendAsync<FeedbackSaved>("review/change",
            request with { AuthenticationContext = await fixture.ContextAsync("status", saved.GapId) }, true, default);
        Assert.True(replay.Saved); Assert.Equal(saved.GapId, replay.GapId); Assert.Equal("Pending", replay.AuditState);
        var current = await TopicAsync(saved.GapId);
        Assert.Equal(committed.Revision, current.Revision); Assert.Equal(original.CreatedAt, current.CreatedAt); Assert.Equal(original.ExpiresAt, current.ExpiresAt);
        await using var connection = await fixture.OpenAsync("ai");
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.topics WHERE gap_id=@gap", new { gap = Guid.Parse(saved.GapId) }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.audit_outbox WHERE payload->>'operationId'=@operation", new { operation }));
    }

    [Fact]
    public async Task DuplicateNestedSuccessFieldCannotReleaseAnAmbiguousReviewPage()
    {
        await fixture.GrantAsync();
        var saved = await fixture.Store().SubmitAsync(fixture.Submission(), default);
        var original = await TopicAsync(saved.GapId);
        var page = JsonSerializer.Serialize(new FeedbackPage([original], null), AssistantContract.Json);
        var ambiguous = page.Replace("\"status\":", "\"status\":\"Closed\",\"status\":", StringComparison.Ordinal);
        Assert.NotEqual(page, ambiguous);
        fixture.LoseNextOwnerResponsePath = "/internal/assistant-feedback/v1/review/topics";
        fixture.NextOwnerFailure = (200, ambiguous);
        using var gateway = new AssistantFeedbackClient(fixture.Configuration);
        await using var connection = await fixture.OpenAsync("ai");
        var releases = await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.audit_outbox WHERE payload->>'action'='FeedbackRead'");
        var request = new FeedbackReviewRequest(await fixture.ContextAsync("read", saved.GapId), saved.GapId, null, 1, null, null, null, null);
        await Assert.ThrowsAsync<JsonException>(() => gateway.SendAsync<FeedbackPage>("review/topics", request, true, default));
        Assert.Equal(releases + 1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.audit_outbox WHERE payload->>'action'='FeedbackRead'"));
        var recovered = await gateway.SendAsync<FeedbackPage>("review/topics",
            request with { AuthenticationContext = await fixture.ContextAsync("read", saved.GapId) }, true, default);
        var current = Assert.Single(recovered.Topics, value => value.GapId == saved.GapId);
        Assert.Equal(original.Status, current.Status); Assert.Equal(original.Revision, current.Revision);
        Assert.Equal(original.CreatedAt, current.CreatedAt); Assert.Equal(original.ExpiresAt, current.ExpiresAt);
    }

    [Fact]
    public async Task CompleteOwnerRejectionRemainsDefiniteAndDoesNotPersist()
    {
        var submission = fixture.Submission(text: "Password=synthetic-private-example-value");
        using var gateway = new AssistantFeedbackClient(fixture.Configuration);
        var failure = await Assert.ThrowsAsync<TenantBoundaryException>(() => gateway.SendAsync<FeedbackSaved>("topics", submission, false, default));
        Assert.Equal(400, failure.Status);
        await using var connection = await fixture.OpenAsync("ai");
        Assert.False(await connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ai_feedback.operations WHERE operation_key=@key)", new { key = submission.IdempotencyKey }));
    }

    [Fact]
    public async Task DefiniteSensitiveInputRejectionDoesNotPersist()
    {
        var submission = fixture.Submission(text: "Password=synthetic-private-example-value");
        using var response = await fixture.PostAsync("topics", submission);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var failure = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("REJECTED", failure.RootElement.GetProperty("outcome").GetString()); Assert.False(failure.RootElement.GetProperty("saved").GetBoolean());
        await using var connection = await fixture.OpenAsync("ai");
        Assert.False(await connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ai_feedback.operations WHERE operation_key=@key)", new { key = submission.IdempotencyKey }));
    }

    [Fact]
    public async Task InterruptedCommitAcknowledgementIsUnknownAndRetryFindsDurableSubmission()
    {
        await using var proxy = new CommitAcknowledgementProxy(fixture.Connection("ai"));
        var store = fixture.Store(fixture.Modified(("Monergy:AssistantFeedback:ConnectionString", proxy.ConnectionString)));
        var submission = fixture.Submission();
        await Assert.ThrowsAsync<AssistantOutcomeUnknownException>(() => store.SubmitAsync(submission, default));
        Assert.True(await proxy.CommitDropped.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var recovered = await fixture.Store().SubmitAsync(submission, default); Assert.True(recovered.Saved);
        await using var connection = await fixture.OpenAsync("ai");
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.operations WHERE operation_key=@key", new { key = submission.IdempotencyKey }));
    }

    [Fact]
    public async Task InterruptedReviewCommitAcknowledgementDoesNotClaimRollbackOrRepeatMutation()
    {
        await fixture.GrantAsync(); var saved = await fixture.Store().SubmitAsync(fixture.Submission(), default);
        await using var proxy = new CommitAcknowledgementProxy(fixture.Connection("ai"));
        var store = fixture.Store(fixture.Modified(("Monergy:AssistantFeedback:ConnectionString", proxy.ConnectionString)));
        using var authority = fixture.Authority(); var operation = Guid.NewGuid().ToString();
        var request = new FeedbackReviewRequest(await fixture.ContextAsync("status", saved.GapId), saved.GapId, null, 1, null, null, operation, Change());
        await Assert.ThrowsAsync<AssistantOutcomeUnknownException>(() => store.ChangeAsync(request, authority, default));
        Assert.True(await proxy.CommitDropped.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var recovered = await fixture.Store().ChangeAsync(request with { AuthenticationContext = await fixture.ContextAsync("status", saved.GapId) }, authority, default);
        Assert.True(recovered.Saved); Assert.Equal(2, (await TopicAsync(saved.GapId)).Revision);
        await using var connection = await fixture.OpenAsync("ai");
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.audit_outbox WHERE payload->>'operationId'=@operation", new { operation }));
    }

    [Fact]
    public async Task ReviewRequiresIndependentCurrentGrantAndRevocationNeedsNoLogout()
    {
        await fixture.GrantAsync(false); using var authority = fixture.Authority();
        var context = await fixture.ContextAsync("list");
        var denied = await Assert.ThrowsAsync<TenantBoundaryException>(() => authority.RequireAsync(context, "list", null, default)); Assert.Equal(403, denied.Status);
        await fixture.GrantAsync(); var allowed = await authority.RequireAsync(context, "list", null, default); Assert.True(allowed.Allowed);
        await fixture.GrantAsync(false);
        await Assert.ThrowsAsync<TenantBoundaryException>(() => authority.RequireAsync(context, "list", null, default));
        await fixture.GrantAsync();
    }

    [Fact]
    public async Task ConcurrentReviewAndReplayProduceOneMutationAndOneAudit()
    {
        await fixture.GrantAsync(); var saved = await fixture.Store().SubmitAsync(fixture.Submission(), default); var operation = Guid.NewGuid().ToString();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => UpdateAsync(saved.GapId, Change(), operation)));
        Assert.All(results, item => Assert.True(item.Saved)); Assert.Equal(2, (await TopicAsync(saved.GapId)).Revision);
        await using var connection = await fixture.OpenAsync("ai");
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.audit_outbox WHERE payload->>'operationId'=@operation", new { operation }));
        var conflict = await Assert.ThrowsAsync<TenantBoundaryException>(() => UpdateAsync(saved.GapId, Change(status: "Awaiting decision"), operation)); Assert.Equal(409, conflict.Status);
    }

    [Fact]
    public async Task PlatformCanonicalTopicCannotBeAssignedToProductButNovelTopicRemainsAProposal()
    {
        await fixture.GrantAsync(); var saved = await fixture.Store().SubmitAsync(fixture.Submission(), default);
        var denied = await Assert.ThrowsAsync<TenantBoundaryException>(() => UpdateAsync(saved.GapId, Change(topic: "persona.platform-administrator")));
        Assert.Equal("HELP_TOPIC_AUDIENCE_MISMATCH", denied.Code);
        await UpdateAsync(saved.GapId, Change(topic: "proposed.future-help-topic"));
        using var authority = fixture.Authority();
        var page = await fixture.Store().ReadAsync(new(await fixture.ContextAsync("read", saved.GapId), saved.GapId, null, 1, null, null, null, null), authority, default);
        var topic = Assert.Single(page.Topics); Assert.Equal("product", topic.SourceAudience); Assert.Null(topic.TopicSubmissionCount); Assert.Null(topic.MatchedOutcomeCount);
    }

    [Fact]
    public async Task UnapprovedResolutionIsNotAcceptedAndPrivacyDeletionRemovesText()
    {
        await fixture.GrantAsync(); var saved = await fixture.Store().SubmitAsync(fixture.Submission(), default);
        await UpdateAsync(saved.GapId, Change(status: "Resolved", approved: true, resolution: new("OPEN-PR-1", new string('a', 64), "ASSERTED")));
        var topic = await TopicAsync(saved.GapId); Assert.Equal("Ready for verification", topic.Status); Assert.Equal(topic.CreatedAt.AddDays(180), topic.ExpiresAt);
        await UpdateAsync(saved.GapId, Change(revision: 2, delete: true));
        var deleted = await TopicAsync(saved.GapId); Assert.Empty(deleted.GeneralQuestion);
        using var authority = fixture.Authority();
        var page = await fixture.Store().ReadAsync(new(await fixture.ContextAsync("read", saved.GapId), saved.GapId, null, 1, null, null, null, null), authority, default);
        Assert.Empty(page.Topics);
    }

    [Fact]
    public async Task AuditIsAppendOnlyIdempotentAndRejectsConflictingEventReuse()
    {
        var store = new PlatformAuditStore(fixture.Configuration);
        var envelope = new PlatformAuditEnvelope("1.0.0", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), AssistantContract.Realm,
            "ai-feedback", "PlatformPrincipal", "A950", AssistantContract.Capability, Guid.NewGuid().ToString(), 1, 1, 1,
            "FeedbackStatus", "Committed", "ReviewApplied", Guid.NewGuid().ToString(), "product", DateTimeOffset.UtcNow);
        var receipt = await store.AppendAsync(envelope, default); var replay = await store.AppendAsync(envelope, default); Assert.Equal(receipt, replay);
        var conflict = await Assert.ThrowsAsync<TenantBoundaryException>(() => store.AppendAsync(envelope with { ReasonCode = "PrivacyDeletion" }, default)); Assert.Equal(409, conflict.Status);
        await using var connection = await fixture.OpenAsync("audit");
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM audit_platform.evidence WHERE event_id=@id", new { id = Guid.Parse(envelope.EventId) }));
    }
}
