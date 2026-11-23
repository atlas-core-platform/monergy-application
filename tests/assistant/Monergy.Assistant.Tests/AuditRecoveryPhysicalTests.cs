using System.Text.Json;
using System.Net.Http.Json;
using Dapper;
using Monergy.Platform;
using Xunit;

namespace Monergy.Assistant.Tests;

[Collection("Assistant physical")]
[Trait("Category", "Physical")]
public sealed class AuditRecoveryPhysicalTests(PhysicalFixture fixture)
{
    [Fact]
    public async Task LostLegacyGapRecoveryResponseRemainsUnknownAndSameBrowserOperationDoesNotRepeatRecovery()
    {
        await fixture.GrantAsync(); var store = fixture.Store(); using var authority = fixture.Authority();
        var saved = await store.SubmitAsync(fixture.Submission(), default);
        await store.ChangeAsync(new(await fixture.ContextAsync("status", saved.GapId), saved.GapId, null, 1, null, null, Guid.NewGuid().ToString(),
            new("1.0.0", 1, "Under review", "Knowledge gap", false, null, null, null, false, null)), authority, default);
        await using var connection = await fixture.OpenAsync("ai");
        var id = await connection.QuerySingleAsync<Guid>("SELECT event_id FROM ai_feedback.audit_outbox WHERE payload->>'resourceId'=@gap", new { gap = saved.GapId });
        await connection.ExecuteAsync("UPDATE ai_feedback.audit_outbox SET attempt_count=16 WHERE event_id=@id", new { id });
        fixture.LoseNextOwnerResponsePath = "/internal/assistant-feedback/v1/review/delivery/retry"; fixture.LoseNextOwnerResponse = true;
        var operation = Guid.NewGuid().ToString();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = fixture.BrowserRequest(HttpMethod.Post, "/platform-api/local/v1/assistant/review/topics/" + saved.GapId + "/delivery/retry");
            request.Headers.Add("Idempotency-Key", operation); request.Content = JsonContent.Create(new { contractVersion = "1.0.0" });
            using var response = await fixture.Client.SendAsync(request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            if (attempt == 0)
            {
                Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Equal("UNKNOWN", body.RootElement.GetProperty("outcome").GetString()); Assert.False(body.RootElement.TryGetProperty("saved", out _));
            }
            else Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT recovery_count FROM ai_feedback.audit_outbox WHERE event_id=@id", new { id }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM ai_feedback.audit_outbox WHERE payload->>'operationId'=@operation", new { operation }));
    }
    [Fact]
    public async Task MismatchedAuditAcknowledgementRemainsPendingAndMatchingReplayDeduplicates()
    {
        await fixture.GrantAsync(); await DrainAsync(); var store = fixture.Store(); using var authority = fixture.Authority();
        await store.ReadAsync(new(await fixture.ContextAsync("list"), null, null, 1, null, null, null, null), authority, default);
        await using var connection = await fixture.OpenAsync("ai");
        var id = await connection.QuerySingleAsync<Guid>("SELECT event_id FROM ai_feedback.audit_outbox WHERE delivered_at IS NULL AND attempt_count=0 ORDER BY created_at DESC LIMIT 1");
        using (var client = new HttpClient(new CorruptReceipt())) await store.DeliverAsync(client, default);
        Assert.True(await connection.ExecuteScalarAsync<bool>("SELECT delivered_at IS NULL AND attempt_count=1 FROM ai_feedback.audit_outbox WHERE event_id=@id", new { id }));
        await connection.ExecuteAsync("UPDATE ai_feedback.audit_outbox SET next_attempt_at=clock_timestamp() WHERE event_id=@id", new { id });
        await DrainAsync();
        Assert.True(await connection.ExecuteScalarAsync<bool>("SELECT delivered_at IS NOT NULL FROM ai_feedback.audit_outbox WHERE event_id=@id", new { id }));
        await using var audit = await fixture.OpenAsync("audit");
        Assert.Equal(1, await audit.ExecuteScalarAsync<long>("SELECT count(*) FROM audit_platform.evidence WHERE event_id=@id", new { id }));
    }
    private sealed class CorruptReceipt() : DelegatingHandler(new SocketsHttpHandler { UseProxy = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var receipt = (await response.Content.ReadFromJsonAsync<PlatformAuditReceipt>(AssistantContract.Json, ct))!;
                response.Content.Dispose(); response.Content = JsonContent.Create(receipt with { EventId = Guid.NewGuid().ToString() }, options: AssistantContract.Json);
            }
            return response;
        }
    }
    [Fact]
    public async Task ListAndMutationEventsExhaustThenRecoverWithoutChangingEventIdentityOrBusinessState()
    {
        await fixture.GrantAsync(); var store = fixture.Store(); using var authority = fixture.Authority();
        await DrainAsync();
        var saved = await store.SubmitAsync(fixture.Submission(), default);
        await store.ReadAsync(new(await fixture.ContextAsync("list"), null, null, 20, null, null, null, null), authority, default);
        var operation = Guid.NewGuid().ToString();
        await store.ChangeAsync(new(await fixture.ContextAsync("status", saved.GapId), saved.GapId, null, 1, null, null, operation,
            new("1.0.0", 1, "Under review", "Knowledge gap", false, null, null, null, false, null)), authority, default);
        await using var connection = await fixture.OpenAsync("ai");
        var listId = await connection.QuerySingleAsync<Guid>("SELECT event_id FROM ai_feedback.audit_outbox WHERE payload->>'action'='FeedbackRead' AND payload->>'resourceId' IS NULL AND delivered_at IS NULL ORDER BY created_at DESC LIMIT 1");
        var mutationId = await connection.QuerySingleAsync<Guid>("SELECT event_id FROM ai_feedback.audit_outbox WHERE payload->>'operationId'=@operation", new { operation });
        var ids = new[] { listId, mutationId };
        using (var unavailable = new HttpClient(new RefusingAudit()))
            for (var attempt = 0; attempt < 16; attempt++)
            {
                await connection.ExecuteAsync("UPDATE ai_feedback.audit_outbox SET next_attempt_at=clock_timestamp() WHERE event_id=ANY(@ids)", new { ids });
                await store.DeliverAsync(unavailable, default);
            }
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.audit_outbox WHERE event_id=ANY(@ids) AND attempt_count=16 AND delivered_at IS NULL", new { ids }));
        var discovery = await store.DiscoverAuditAsync(new(await fixture.ContextAsync("list"), null, null, 20, null), authority, default);
        var discovered = discovery.Events.Select(item => item.EventId).ToHashSet(StringComparer.Ordinal);
        for (var pageNumber = 0; pageNumber < 50 && !ids.All(id => discovered.Contains(id.ToString())) && discovery.NextCursor is not null; pageNumber++)
        {
            discovery = await store.DiscoverAuditAsync(new(await fixture.ContextAsync("list"), null, discovery.NextCursor, 20, null), authority, default);
            Assert.InRange(discovery.Events.Count, 1, 20);
            foreach (var item in discovery.Events) Assert.True(discovered.Add(item.EventId));
        }
        Assert.All(ids, id => Assert.Contains(id.ToString(), discovered));
        var listStatus = await store.RecoverAuditAsync(new(await fixture.ContextAsync("status", listId.ToString()), listId.ToString(), null, 1, null), authority, false, default);
        Assert.Null(listStatus.Event.ResourceId); Assert.Equal("Paused", listStatus.Event.State);
        foreach (var eventId in ids)
        {
            var retryId = Guid.NewGuid().ToString();
            var request = new FeedbackAuditRequest(await fixture.ContextAsync("status", eventId.ToString()), eventId.ToString(), null, 1, retryId);
            var resumed = await store.RecoverAuditAsync(request, authority, true, default);
            var replay = await store.RecoverAuditAsync(request with { AuthenticationContext = await fixture.ContextAsync("status", eventId.ToString()) }, authority, true, default);
            Assert.Equal(eventId.ToString(), resumed.Event.EventId); Assert.Equal(resumed.Event.Recoveries, replay.Event.Recoveries); Assert.Equal(1, replay.Event.Recoveries);
        }
        var pendingBefore = await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.audit_outbox WHERE delivered_at IS NULL");
        await DrainAsync();
        Assert.True(pendingBefore > 0); Assert.True(await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.audit_outbox WHERE delivered_at IS NULL") < pendingBefore);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.audit_outbox WHERE event_id=ANY(@ids) AND delivered_at IS NOT NULL", new { ids }));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT revision::int FROM ai_feedback.topics WHERE gap_id=@id", new { id = Guid.Parse(saved.GapId) }));
        await using var audit = await fixture.OpenAsync("audit");
        Assert.Equal(2, await audit.ExecuteScalarAsync<int>("SELECT count(*)::int FROM audit_platform.evidence WHERE event_id=ANY(@ids)", new { ids }));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.audit_recovery_operations WHERE event_id=ANY(@ids)", new { ids }));
    }

    [Fact]
    public async Task RecoveryRequiresCurrentAuthorityAndCannotExceedThreeCycles()
    {
        await fixture.GrantAsync(); using var authority = fixture.Authority(); var store = fixture.Store();
        await store.ReadAsync(new(await fixture.ContextAsync("list"), null, null, 1, null, null, null, null), authority, default);
        await using var connection = await fixture.OpenAsync("ai");
        var id = await connection.QuerySingleAsync<Guid>("SELECT event_id FROM ai_feedback.audit_outbox WHERE payload->>'resourceId' IS NULL ORDER BY created_at DESC LIMIT 1");
        await connection.ExecuteAsync("UPDATE ai_feedback.audit_outbox SET attempt_count=16,recovery_count=3 WHERE event_id=@id", new { id });
        var request = new FeedbackAuditRequest(await fixture.ContextAsync("status", id.ToString()), id.ToString(), null, 1, Guid.NewGuid().ToString());
        var exhausted = await Assert.ThrowsAsync<TenantBoundaryException>(() => store.RecoverAuditAsync(request, authority, true, default)); Assert.Equal(409, exhausted.Status);
        await fixture.GrantAsync(false);
        var denied = await Assert.ThrowsAsync<TenantBoundaryException>(() => store.RecoverAuditAsync(request, authority, true, default)); Assert.Equal(403, denied.Status);
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT recovery_count FROM ai_feedback.audit_outbox WHERE event_id=@id", new { id }));
        await fixture.GrantAsync();
        // This synthetic exhausted fixture is retained as paused evidence; later tests isolate their pending IDs.
    }

    [Fact]
    public async Task RestrictedReadWithUnavailableOutboxReturnsNoContent()
    {
        await fixture.GrantAsync(); var saved = await fixture.Store().SubmitAsync(fixture.Submission(), default);
        var runtime = new Npgsql.NpgsqlConnectionStringBuilder(fixture.Connection("ai")).Username!;
        await using var owner = await fixture.OpenAsync("ai");
        var context = await fixture.ContextAsync("read", saved.GapId);
        await owner.ExecuteAsync($"REVOKE INSERT ON ai_feedback.audit_outbox FROM {runtime}");
        try
        {
            using var authority = fixture.Authority();
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Store().ReadAsync(new(context, saved.GapId, null, 1, null, null, null, null), authority, default));
            Assert.Equal(Npgsql.PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        finally { await owner.ExecuteAsync($"GRANT INSERT ON ai_feedback.audit_outbox TO {runtime}"); }
    }

    private async Task DrainAsync()
    {
        await using var connection = await fixture.OpenAsync("ai");
        for (var batch = 0; batch < 100; batch++)
        {
            var pending = await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM ai_feedback.audit_outbox WHERE delivered_at IS NULL AND attempt_count<16");
            if (pending == 0) return;
            await connection.ExecuteAsync("UPDATE ai_feedback.audit_outbox SET next_attempt_at=clock_timestamp() WHERE delivered_at IS NULL AND attempt_count<16");
            await fixture.Store().DeliverAsync(fixture.Client, default);
        }
        throw new InvalidOperationException("Disposable audit backlog did not drain.");
    }
    private sealed class RefusingAudit : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("Synthetic audit outage."); }
}
