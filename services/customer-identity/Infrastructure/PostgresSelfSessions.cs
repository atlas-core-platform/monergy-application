using Dapper;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

public sealed class PostgresSelfSessions(PostgresTenantSessions sessions) : ISelfSessionRepository
{
    public async Task<SelfSessionPage> ListAsync(TenantSessionContext current, string? after, CancellationToken ct)
    {
        await using var connection = await sessions.OpenAsync(current.TenantId, ct).ConfigureAwait(false);
        var cursor = after is null ? null : await connection.QuerySingleOrDefaultAsync<CursorRow>(new CommandDefinition("""
            SELECT established_at AS EstablishedAt FROM customer_identity.trusted_sessions
            WHERE actor_id=@Actor AND session_hash=@After;
            """, new { Actor = current.ActorId, After = after }, cancellationToken: ct)).ConfigureAwait(false);
        if (after is not null && cursor is null) throw new TenantAccessException("SESSION_CURSOR_INVALID", 400);
        var rows = (await connection.QueryAsync<SessionRow>(new CommandDefinition("""
            SELECT actor_id AS ActorId,session_hash AS SessionReference,established_at AS EstablishedAt,expires_at AS ExpiresAt,
                CASE WHEN revoked THEN 'Ended' WHEN expires_at<=clock_timestamp() THEN 'Expired'
                    WHEN subject_version<>@Version THEN 'AccessChanged' ELSE 'Active' END AS Status
            FROM customer_identity.trusted_sessions
            WHERE actor_id=@Actor AND (@After IS NULL OR (established_at,session_hash)<(@At,@After))
            ORDER BY established_at DESC,session_hash DESC LIMIT 101;
            """, new { Actor = current.ActorId, Version = current.SubjectVersion, After = after,
                At = cursor?.EstablishedAt ?? DateTime.UnixEpoch }, cancellationToken: ct)).ConfigureAwait(false)).ToArray();
        var reference = SelfSessionReferences.Hash(current.AuthenticationContextId);
        return new(current.TenantId, current.ActorId, rows.Take(100).Select(x => new SelfSessionEntry(x.ActorId,
            x.SessionReference, Utc(x.EstablishedAt), Utc(x.ExpiresAt), x.Status, x.SessionReference == reference)).ToArray(),
            rows.Length > 100 ? rows[99].SessionReference : null);
    }
    public async Task<SelfSessionReceipt> RevokeAsync(TenantSessionContext current, SelfSessionRevocation change, CancellationToken ct)
    {
        if (change.SessionReference == SelfSessionReferences.Hash(current.AuthenticationContextId))
            throw new TenantAccessException("USE_CURRENT_SESSION_SIGN_OUT", 409);
        await using var connection = await sessions.OpenAsync(current.TenantId, ct).ConfigureAwait(false);
        var safe = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT NOT has_table_privilege(current_user,'customer_identity.self_session_receipts','UPDATE,DELETE,TRUNCATE');
            """, cancellationToken: ct)).ConfigureAwait(false);
        if (!safe) throw new TenantAccessException("SESSION_BINDING_INVALID", 503);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        // Same tenant-local lock as trusted session creation and AM event consumption.
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(705001);", transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
        // Existing UPDATE(revoked) privilege permits locking this session row;
        // sign-out cannot invalidate it between this check and the local commit.
        var authorized = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT session_hash FROM customer_identity.trusted_sessions s
                WHERE session_hash=@Hash AND actor_id=@Actor AND subject_version=@Version
                    AND NOT revoked AND expires_at>clock_timestamp()
                    AND subject_version>=COALESCE((SELECT version FROM customer_identity.access_subject_versions WHERE actor_id=@Actor),0)
                FOR UPDATE OF s;
            """, new { Hash = SelfSessionReferences.Hash(current.AuthenticationContextId), Actor = current.ActorId,
                Version = current.SubjectVersion }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (authorized is null) throw new TenantAccessException("SESSION_NOT_CURRENT", 401);
        var operation = Guid.ParseExact(change.OperationId, "N");
        var prior = await connection.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition("""
            SELECT actor_id AS ActorId,session_reference AS SessionReference FROM customer_identity.self_session_receipts
            WHERE operation_id=@Operation;
            """, new { Operation = operation }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (prior is not null)
        {
            if (prior.ActorId != current.ActorId || prior.SessionReference != change.SessionReference)
                throw new TenantAccessException("SESSION_OPERATION_CONFLICT", 409);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new(change.OperationId, change.SessionReference, true);
        }
        // Exact actor predicate prevents a reference from another person granting authority.
        var changed = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE customer_identity.trusted_sessions SET revoked=true
            WHERE actor_id=@Actor AND session_hash=@Reference;
            """, new { Actor = current.ActorId, Reference = change.SessionReference }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (changed != 1) throw new TenantAccessException("SESSION_NOT_FOUND", 404);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO customer_identity.self_session_receipts(operation_id,actor_id,session_reference)
            VALUES (@Operation,@Actor,@Reference);
            """, new { Operation = operation, Actor = current.ActorId, Reference = change.SessionReference }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(change.OperationId, change.SessionReference, true);
    }
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private sealed class CursorRow { public DateTime EstablishedAt { get; set; } }
    private sealed class ReceiptRow { public string ActorId { get; set; } = ""; public string SessionReference { get; set; } = ""; }
    private sealed class SessionRow
    {
        public string ActorId { get; set; } = "";
        public string SessionReference { get; set; } = "";
        public DateTime EstablishedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string Status { get; set; } = "";
    }
}
