using Dapper;
using Monergy.Platform;

namespace Monergy.Services.AiIntelligence.AssistantFeedback;

public sealed partial class FeedbackStore
{
    public async Task<FeedbackAuditPage> DiscoverAuditAsync(FeedbackAuditRequest request, FeedbackAuthority authority, CancellationToken ct)
    {
        if (request.EventId is not null || request.OperationId is not null || request.Limit is < 1 or > 20)
            throw new TenantBoundaryException("FEEDBACK_AUDIT_QUERY_INVALID", 400);
        var decision = await authority.RequireAsync(request.AuthenticationContext, "list", null, ct).ConfigureAwait(false);
        DateTimeOffset? time = null; Guid? id = null;
        if (request.Cursor is not null)
        {
            var parts = request.Cursor.Split('|');
            if (parts.Length != 2 || !DateTimeOffset.TryParseExact(parts[0], "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsedTime) || !Guid.TryParseExact(parts[1], "D", out var parsedId))
                throw new TenantBoundaryException("FEEDBACK_CURSOR_INVALID", 400);
            time = parsedTime; id = parsedId;
        }
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(connection, transaction, ct).ConfigureAwait(false);
        var events = (await connection.QueryAsync<AuditRow>(Sql(AuditProjection + " WHERE (@time::timestamptz IS NULL OR (created_at,event_id)>(@time,@id::uuid)) ORDER BY created_at,event_id LIMIT @limit",
            new { time, id, limit = request.Limit + 1 }, transaction, ct)).ConfigureAwait(false)).Select(Project).ToArray();
        decision = await authority.ReconfirmAsync(decision, request.AuthenticationContext, ct).ConfigureAwait(false);
        await OutboxAsync(connection, transaction, Audit(decision, Guid.NewGuid().ToString(), "FeedbackRead", "ReleasePrepared", "DeliveryDiscovery", null, null), ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        await authority.ReconfirmAsync(decision, request.AuthenticationContext, ct).ConfigureAwait(false);
        var page = events.Take(request.Limit).ToArray();
        return new(page, events.Length > request.Limit ? page[^1].CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "|" + page[^1].EventId : null);
    }

    public async Task<FeedbackAuditResult> RecoverAuditAsync(FeedbackAuditRequest request, FeedbackAuthority authority, bool retry, CancellationToken ct)
    {
        if (request.EventId is null || !AssistantContract.OpaqueId(request.EventId) || request.Cursor is not null ||
            retry && (request.OperationId is null || !AssistantContract.OpaqueId(request.OperationId)) || !retry && request.OperationId is not null)
            throw new TenantBoundaryException("FEEDBACK_AUDIT_REQUEST_INVALID", 400);
        var eventId = Guid.Parse(request.EventId);
        var decision = await authority.RequireAsync(request.AuthenticationContext, "status", eventId.ToString(), ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(connection, transaction, ct).ConfigureAwait(false);
        var stored = await connection.QuerySingleOrDefaultAsync<AuditRow>(Sql(AuditProjection + " WHERE event_id=@eventId FOR UPDATE", new { eventId }, transaction, ct)).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("FEEDBACK_AUDIT_EVENT_NOT_FOUND", 404);
        var item = Project(stored);
        decision = await authority.ReconfirmAsync(decision, request.AuthenticationContext, ct).ConfigureAwait(false);
        string? auditState = null;
        if (retry)
        {
            var key = AssistantContract.Hash("audit-recovery|" + decision.PrincipalId + "|" + Guid.Parse(request.OperationId!).ToString());
            var digest = AssistantContract.Hash(eventId.ToString());
            var prior = await connection.QuerySingleOrDefaultAsync<RecoveryOperation>(Sql("SELECT request_hash AS Hash,recovery_event_id AS EventId,expires_at<=clock_timestamp() AS Expired FROM ai_feedback.audit_recovery_operations WHERE operation_key=@key", new { key }, transaction, ct)).ConfigureAwait(false);
            if (prior is not null)
            {
                if (prior.Hash != digest || prior.Expired) throw new TenantBoundaryException("FEEDBACK_AUDIT_OPERATION_CONFLICT", 409);
                auditState = await connection.ExecuteScalarAsync<bool>(Sql("SELECT delivered_at IS NOT NULL FROM ai_feedback.audit_outbox WHERE event_id=@id", new { id = prior.EventId }, transaction, ct)).ConfigureAwait(false) ? "Accepted" : "Pending";
            }
            else
            {
                if (item.State != "Paused" || item.Recoveries >= 3) throw new TenantBoundaryException("FEEDBACK_AUDIT_RECOVERY_UNAVAILABLE", 409);
                await connection.ExecuteAsync(Sql("UPDATE ai_feedback.audit_outbox SET attempt_count=0,recovery_count=recovery_count+1,next_attempt_at=clock_timestamp() WHERE event_id=@eventId", new { eventId }, transaction, ct)).ConfigureAwait(false);
                var audit = Audit(decision, Guid.Parse(request.OperationId!).ToString(), "FeedbackStatus", "Committed", "AuditRecovery", null, null);
                await OutboxAsync(connection, transaction, audit, ct).ConfigureAwait(false);
                await connection.ExecuteAsync(Sql("INSERT INTO ai_feedback.audit_recovery_operations(operation_key,request_hash,event_id,recovery_event_id) VALUES(@key,@digest,@eventId,@recovery)", new { key, digest, eventId, recovery = Guid.Parse(audit.EventId) }, transaction, ct)).ConfigureAwait(false);
                item = item with { Attempts = 0, Recoveries = item.Recoveries + 1, State = "Pending" };
                auditState = "Pending";
            }
        }
        else await OutboxAsync(connection, transaction, Audit(decision, Guid.NewGuid().ToString(), "FeedbackRead", "ReleasePrepared", "DeliveryDiscovery", null, null), ct).ConfigureAwait(false);
        await authority.ReconfirmAsync(decision, request.AuthenticationContext, ct).ConfigureAwait(false);
        if (retry) await CommitMutationAsync(transaction, ct).ConfigureAwait(false);
        else await transaction.CommitAsync(ct).ConfigureAwait(false);
        if (!retry) await authority.ReconfirmAsync(decision, request.AuthenticationContext, ct).ConfigureAwait(false);
        return new(item, retry ? true : null, auditState);
    }

    private const string AuditProjection = """
        SELECT event_id::text AS EventId,payload->>'operationId' AS OperationId,payload->>'resourceId' AS ResourceId,
         payload->>'action' AS Action,CASE WHEN delivered_at IS NOT NULL THEN 'Accepted' WHEN attempt_count>=16 THEN 'Paused' ELSE 'Pending' END AS State,
         attempt_count AS Attempts,recovery_count AS Recoveries,created_at AS CreatedAt FROM ai_feedback.audit_outbox
        """;
    private sealed record RecoveryOperation(string Hash, Guid EventId, bool Expired);
    private sealed record AuditRow(string EventId, string OperationId, string? ResourceId, string Action, string State,
        int Attempts, int Recoveries, DateTime CreatedAt);
    private static FeedbackAuditEvent Project(AuditRow row) => new(row.EventId, row.OperationId, row.ResourceId,
        row.Action, row.State, row.Attempts, row.Recoveries, new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)));
}
