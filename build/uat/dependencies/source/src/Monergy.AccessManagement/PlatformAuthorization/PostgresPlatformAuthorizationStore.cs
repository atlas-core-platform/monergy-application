using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Monergy.AccessManagement;

public sealed class PostgresPlatformAuthorizationStore(string connectionString, TimeProvider time)
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        try { await connection.OpenAsync(ct).ConfigureAwait(false); return connection; }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<bool> ReadyAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM am_platform.policy WHERE singleton AND schema_version=1);", cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<PlatformEntitlementStatus> StatusAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        return await connection.QuerySingleAsync<PlatformEntitlementStatus>(new CommandDefinition("""
            SELECT revision AS PolicyRevision,
                (SELECT count(*)::integer FROM am_platform.audit_delivery WHERE state <> 'Accepted') AS PendingAudit
            FROM am_platform.policy WHERE singleton;
            """, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<PlatformEntitlementResult> ApplyAsync(PlatformEntitlementCommand command,
        VerifiedPlatformPrincipal principal, CancellationToken ct)
    {
        var operation = Guid.Parse(command.OperationId);
        var fingerprint = Hash(JsonSerializer.Serialize(command, PlatformReviewerContract.Json));
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var policy = await LockPolicyAsync(connection, transaction, ct).ConfigureAwait(false);
        var prior = await connection.QuerySingleOrDefaultAsync<CommandRow>(new CommandDefinition(
            "SELECT request_hash AS RequestHash, result_json::text AS ResultJson FROM am_platform.command_results WHERE operation_id=@operation;",
            new { operation }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (prior is not null)
        {
            PlatformReviewerContract.Require(prior.RequestHash == fingerprint, "PLATFORM_OPERATION_CONFLICT", 409);
            return JsonSerializer.Deserialize<PlatformEntitlementResult>(prior.ResultJson, PlatformReviewerContract.Json)!;
        }
        PlatformReviewerContract.Require(policy == command.ExpectedPolicyRevision, "PLATFORM_POLICY_REVISION_CONFLICT", 409);
        await CheckCapacityAsync(connection, transaction, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var previous = await GetGrantAsync(connection, transaction, principal.PrincipalId, ct).ConfigureAwait(false);
        var grant = command.Action == "Grant";
        var changed = previous is null || previous.Granted != grant;
        var revision = changed ? policy + 1 : policy;
        var grantRevision = changed ? (previous?.Revision ?? 0) + 1 : previous!.Revision;
        var entitlementId = previous?.EntitlementId ?? Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO am_platform.principal_references (principal_id,realm,credential_version,verified_at)
            VALUES (@PrincipalId,@Realm,@CredentialVersion,@now)
            ON CONFLICT (principal_id) DO UPDATE SET credential_version=EXCLUDED.credential_version, verified_at=EXCLUDED.verified_at;
            """, new { principal.PrincipalId, principal.Realm, principal.CredentialVersion, now }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (changed)
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO am_platform.reviewer_grants (entitlement_id,principal_id,capability,granted,revision,changed_at)
                VALUES (@entitlementId,@PrincipalId,@capability,@grant,@grantRevision,@now)
                ON CONFLICT (principal_id) DO UPDATE SET granted=EXCLUDED.granted,revision=EXCLUDED.revision,changed_at=EXCLUDED.changed_at;
                UPDATE am_platform.policy SET revision=@revision WHERE singleton;
                """, new { entitlementId, principal.PrincipalId, capability = PlatformReviewerContract.Capability, grant, grantRevision, now, revision },
                transaction, cancellationToken: ct)).ConfigureAwait(false);
        var result = new PlatformEntitlementResult(command.OperationId, entitlementId.ToString("D"), grant, revision, grantRevision, "Pending");
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO am_platform.command_results (operation_id,request_hash,result_json,recorded_at)
            VALUES (@operation,@fingerprint,CAST(@json AS jsonb),@now);
            """, new { operation, fingerprint, json = JsonSerializer.Serialize(result, PlatformReviewerContract.Json), now },
            transaction, cancellationToken: ct)).ConfigureAwait(false);
        await EnqueueAsync(connection, transaction, new PlatformAuditEnvelope("1.0.0", Guid.NewGuid().ToString("D"),
            command.OperationId, PlatformReviewerContract.Realm, "am-platform", "OperatorWorkload", "local-reviewer-operator",
            PlatformReviewerContract.Capability, null, revision, grantRevision, null,
            grant ? "ReviewerGrant" : "ReviewerRevoke", "Committed", command.ReasonCode, entitlementId.ToString("D"), "platform", now), ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    public async Task<PlatformReviewDecision> EvaluateAsync(PlatformReviewEvaluationRequest request,
        VerifiedPlatformContext? identity, string identityFailure, IPlatformIdentityClient verifier, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var policy = await LockPolicyAsync(connection, transaction, ct).ConfigureAwait(false);
        await CheckCapacityAsync(connection, transaction, ct).ConfigureAwait(false);
        // Re-resolve immediately before the durable decision; no positive authorization cache.
        if (identity is not null)
        {
            try
            {
                var current = await verifier.ResolveAsync(request, ct).ConfigureAwait(false);
                if (current is null || current.PrincipalId != identity.PrincipalId || current.CredentialVersion != identity.CredentialVersion)
                { identity = null; identityFailure = "IdentityInvalid"; }
                else identity = current;
            }
            catch (PlatformAuthorizationException) { identity = null; identityFailure = "IdentityUnavailable"; }
        }
        var grant = identity is null ? null : await GetGrantAsync(connection, transaction, identity.PrincipalId, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var allowed = identity is not null && identity.ExpiresAt > now && grant is { Granted: true };
        var reason = identity is null ? identityFailure : identity.ExpiresAt <= now ? "ContextExpired" :
            grant is null ? "EntitlementMissing" : !grant.Granted ? "EntitlementRevoked" : "Authorized";
        var decisionId = Guid.NewGuid().ToString("D");
        var expiry = allowed && identity!.ExpiresAt < now.AddSeconds(15) ? identity.ExpiresAt : now.AddSeconds(15);
        var decision = new PlatformReviewDecision(decisionId, allowed, identity?.PrincipalId, PlatformReviewerContract.Realm,
            identity?.CredentialVersion ?? 0, PlatformReviewerContract.Capability, request.Operation, request.ResourceId, policy, grant?.Revision ?? 0,
            now, expiry, reason, "Pending");
        await EnqueueAsync(connection, transaction, new PlatformAuditEnvelope("1.0.0", Guid.NewGuid().ToString("D"), decisionId,
            PlatformReviewerContract.Realm, "am-platform", identity is null ? "OperatorWorkload" : "PlatformPrincipal",
            identity?.PrincipalId ?? "assistant-feedback-review-evaluator", PlatformReviewerContract.Capability, decisionId,
            policy, grant?.Revision ?? 0, null, "ReviewerAuthorization", allowed ? "Allow" : "Deny", reason,
            request.ResourceId, "platform", now), ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return decision;
    }

    public async Task<PlatformAuditDeliveryStatus[]> DeliveryAsync(Guid operationId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<DeliveryStatusRow>(new CommandDefinition("""
            SELECT o.operation_id::text AS OperationId,o.event_id::text AS EventId,d.state AS State,d.attempts AS Attempts,
                d.receipt_id AS ReceiptId,d.recorded_at AS RecordedAt
            FROM am_platform.audit_outbox o JOIN am_platform.audit_delivery d USING(event_id)
            WHERE o.operation_id=@operationId ORDER BY o.occurred_at,o.event_id LIMIT 100;
            """, new { operationId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(row => new PlatformAuditDeliveryStatus(row.OperationId, row.EventId, row.State, row.Attempts,
            row.ReceiptId, row.RecordedAt is { } recorded ? new DateTimeOffset(DateTime.SpecifyKind(recorded, DateTimeKind.Utc)) : null)).ToArray();
    }

    public async Task<PlatformDelivery?> ClaimAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var lease = Guid.NewGuid();
        return await connection.QuerySingleOrDefaultAsync<PlatformDelivery>(new CommandDefinition("""
            WITH picked AS (
                SELECT event_id FROM am_platform.audit_delivery
                WHERE attempts < 8 AND ((state IN ('Pending','Retry') AND next_attempt_at<=@now)
                    OR (state='Delivering' AND lease_until<=@now))
                ORDER BY next_attempt_at,event_id FOR UPDATE SKIP LOCKED LIMIT 1
            ), claimed AS (
                UPDATE am_platform.audit_delivery SET state='Delivering',attempts=attempts+1,lease_id=@lease,
                    lease_until=@leaseUntil FROM picked WHERE audit_delivery.event_id=picked.event_id
                RETURNING audit_delivery.event_id,audit_delivery.attempts
            )
            SELECT o.event_id AS EventId,o.payload_json::text AS Payload,c.attempts AS Attempts,@lease AS LeaseId
            FROM claimed c JOIN am_platform.audit_outbox o USING(event_id);
            """, new { now, lease, leaseUntil = now.AddSeconds(30) }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task CompleteDeliveryAsync(PlatformDelivery delivery, PlatformAuditReceipt? receipt, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE am_platform.audit_delivery SET state=@state,next_attempt_at=@next,lease_until=NULL,lease_id=NULL,
                receipt_id=@receiptId,recorded_at=@recordedAt
            WHERE event_id=@EventId AND state='Delivering' AND lease_id=@LeaseId;
            """, new { delivery.EventId, delivery.LeaseId,
                state = receipt is not null ? "Accepted" : delivery.Attempts >= PlatformReviewerContract.MaximumDeliveryAttempts ? "Paused" : "Retry",
                next = now.AddSeconds(Math.Min(300, 1 << delivery.Attempts)), receiptId = receipt?.ReceiptId, recordedAt = receipt?.RecordedAt },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> ResumeDeliveryAsync(Guid eventId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE am_platform.audit_delivery SET state='Retry',attempts=0,next_attempt_at=@now,lease_id=NULL,lease_until=NULL
            WHERE event_id=@eventId AND (state='Paused' OR (state='Delivering' AND attempts=8 AND lease_until<=@now));
            """, new { eventId, now = time.GetUtcNow() }, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    private static Task<long> LockPolicyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) =>
        connection.QuerySingleAsync<long>(new CommandDefinition("SELECT revision FROM am_platform.policy WHERE singleton FOR UPDATE;",
            transaction: transaction, cancellationToken: ct));

    private static Task<GrantRow?> GetGrantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string principalId, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<GrantRow>(new CommandDefinition("""
            SELECT entitlement_id AS EntitlementId,granted AS Granted,revision AS Revision
            FROM am_platform.reviewer_grants WHERE principal_id=@principalId;
            """, new { principalId }, transaction, cancellationToken: ct));

    private static async Task CheckCapacityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var pending = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM am_platform.audit_delivery WHERE state <> 'Accepted';", transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
        PlatformReviewerContract.Require(pending < PlatformReviewerContract.MaximumPendingAudit, "PLATFORM_AUDIT_OUTBOX_FULL", 503);
    }

    private static async Task EnqueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PlatformAuditEnvelope envelope, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(envelope, PlatformReviewerContract.Json);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO am_platform.audit_outbox (event_id,operation_id,payload_json,payload_hash,occurred_at)
            VALUES (@eventId,@operationId,CAST(@json AS jsonb),@hash,@now);
            INSERT INTO am_platform.audit_delivery (event_id,state,attempts,next_attempt_at) VALUES (@eventId,'Pending',0,@now);
            """, new { eventId = Guid.Parse(envelope.EventId), operationId = Guid.Parse(envelope.OperationId), json,
                hash = Hash(json), now = envelope.OccurredAt }, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record CommandRow(string RequestHash, string ResultJson);
    private sealed record GrantRow(Guid EntitlementId, bool Granted, long Revision);
    private sealed record DeliveryStatusRow(string OperationId, string EventId, string State, int Attempts, string? ReceiptId, DateTime? RecordedAt);
}

public sealed record PlatformDelivery(Guid EventId, string Payload, int Attempts, Guid LeaseId);
