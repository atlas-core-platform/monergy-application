using Dapper;
using Monergy.Contracts;
using Npgsql;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

// Projection of the C&I-owned invitation ledger. Expiry is materialized as an
// immutable terminal event before status is returned. This does not grant
// membership, issue credentials, or expose invitation bearer hashes.
public sealed record PersonaInvitationStatus(string ActorId, string Email, string State,
    DateTimeOffset IssuedAt, DateTimeOffset? ConsumedAt);
public sealed record PersonaInvitationCancelResult(string ActorId, string State);
public sealed record PersonaInvitationStatusPage(string TenantId,
    IReadOnlyList<PersonaInvitationStatus> Items, string? NextCursor);

public interface IPersonaInvitationStatus
{
    Task<PersonaInvitationStatusPage> ListAsync(string tenant, string? after, CancellationToken ct);
    Task<PersonaInvitationCancelResult> CancelAsync(string tenant, string actorId, CancellationToken ct);
}

public sealed class PostgresPersonaInvitationStatus(PostgresTenantSessions connections) :
    IPersonaInvitationStatus
{
    public async Task<PersonaInvitationStatusPage> ListAsync(string tenant, string? after, CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(tenant, 64);
        if (after is not null && (after.Length > 128 || after.Length == 0 || after.Any(char.IsControl)))
            throw new TenantAccessException("INVITATION_CURSOR_INVALID", 400);
        await using var db = await connections.OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        await MaterializeExpiredAsync(db, tx, null, ct).ConfigureAwait(false);
        // Latest issuance per actor is the current invitation attempt. Previous
        // attempts remain in the immutable ledger. Do not return the hash.
        var rows = (await db.QueryAsync<Row>(new CommandDefinition("""
            WITH latest AS (
              SELECT DISTINCT ON (e.actor_id)
                e.actor_id,e.invitation_hash,e.occurred_at AS issued_at,
                e.event_id AS issue_id
              FROM customer_identity.persona_invitation_events e
              WHERE e.action='ISSUED'
              ORDER BY e.actor_id,e.occurred_at DESC,e.event_id DESC
            )
            SELECT l.actor_id AS ActorId,p.normalized_email AS Email,
              l.issued_at AS IssuedAt,
              consumed.occurred_at AS ConsumedAt,
              CASE WHEN consumed.event_id IS NOT NULL THEN 'Completed'
                   WHEN cancelled.event_id IS NOT NULL THEN 'Cancelled'
                   WHEN expired.event_id IS NOT NULL THEN 'Expired'
                   WHEN l.issued_at<=clock_timestamp()-interval '5 minutes'
                      THEN 'Expired'
                   ELSE 'Pending' END AS State,
              l.issue_id AS IssueId
            FROM latest l JOIN customer_identity.tenant_principals p
              ON p.actor_id=l.actor_id
            LEFT JOIN customer_identity.persona_invitation_events consumed
              ON consumed.invitation_hash=l.invitation_hash AND consumed.action='CONSUMED'
            LEFT JOIN customer_identity.persona_invitation_events cancelled
              ON cancelled.invitation_hash=l.invitation_hash AND cancelled.action='CANCELLED'
            LEFT JOIN customer_identity.persona_invitation_events expired
              ON expired.invitation_hash=l.invitation_hash AND expired.action='EXPIRED'
            WHERE (@After IS NULL OR l.actor_id>@After)
            ORDER BY l.actor_id LIMIT 51;
            """, new { After = after }, tx, cancellationToken: ct)).ConfigureAwait(false)).ToArray();
        // Actor ID is used as a stable page cursor; the owner limits disclosure.
        var items = rows.Take(50).Select(r =>
            new PersonaInvitationStatus(r.ActorId, r.Email, r.State, r.IssuedAt, r.ConsumedAt)).ToArray();
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new(tenant, items, rows.Length > 50 ? rows[49].ActorId : null);
    }

    // The challenge row is locked through UPDATE. A racing activation must
    // commit before cancellation (already consumed) or after (ticket denied).
    public async Task<PersonaInvitationCancelResult> CancelAsync(
        string tenant, string actorId, CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(tenant, 64);
        TenantAccessProtocol.Identifier(actorId, 128);
        await using var db = await connections.OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        await MaterializeExpiredAsync(db, tx, actorId, ct).ConfigureAwait(false);
        var hash = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            UPDATE customer_identity.registration_challenges c
            SET consumed=true,protected_code=NULL
            FROM customer_identity.tenant_principals p
            WHERE p.actor_id=@Actor AND p.normalized_email=c.email
              AND c.purpose='persona-activation' AND NOT c.consumed
              AND c.expires_at>clock_timestamp()
              AND EXISTS(
                SELECT 1 FROM customer_identity.persona_invitation_events e
                WHERE e.actor_id=p.actor_id AND e.invitation_hash=c.challenge_hash
                  AND e.action='ISSUED'
              )
              AND NOT EXISTS(
                SELECT 1 FROM customer_identity.persona_credentials pc
                WHERE pc.actor_id=p.actor_id
              )
            RETURNING c.challenge_hash;
            """, new { Actor = actorId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (hash is not null)
        {
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO customer_identity.persona_invitation_events
                    (event_id,actor_id,invitation_hash,action)
                VALUES (@Event,@Actor,@Hash,'CANCELLED')
                ON CONFLICT (invitation_hash,action) DO NOTHING;
                """, new { Event = Guid.NewGuid(), Actor = actorId, Hash = hash },
                tx, cancellationToken: ct)).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        // Repeated cancellation is a safe no-op, not proof of changed credentials.
        return new(actorId, hash is null ? "NotCancelled" : "Cancelled");
    }

    private static async Task MaterializeExpiredAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, string? actorId, CancellationToken ct)
    {
        // Lock currently expired, still-unconsumed challenges before writing the
        // terminal ledger event. A racing activation/cancellation either commits
        // first (and is re-evaluated out) or waits until expiry owns the row.
        var candidates = (await db.QueryAsync<ExpiredInvitation>(new CommandDefinition("""
            SELECT p.actor_id AS ActorId,c.challenge_hash AS InvitationHash
            FROM customer_identity.registration_challenges c
            JOIN customer_identity.tenant_principals p
              ON p.normalized_email=c.email
            WHERE c.purpose='persona-activation'
              AND c.delivered AND NOT c.consumed
              AND c.expires_at<=clock_timestamp()
              AND (@Actor IS NULL OR p.actor_id=@Actor)
              AND EXISTS(
                SELECT 1 FROM customer_identity.persona_invitation_events issued
                WHERE issued.actor_id=p.actor_id
                  AND issued.invitation_hash=c.challenge_hash
                  AND issued.action='ISSUED'
              )
            FOR UPDATE OF c;
            """, new { Actor = actorId }, tx, cancellationToken: ct))
            .ConfigureAwait(false)).ToArray();

        foreach (var candidate in candidates)
        {
            // Each INSERT gets a fresh READ COMMITTED snapshot after any row-lock
            // wait. Do not create EXPIRED when activation/cancellation already
            // committed a terminal event.
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO customer_identity.persona_invitation_events
                  (event_id,actor_id,invitation_hash,action)
                SELECT @Event,@Actor,@Hash,'EXPIRED'
                WHERE NOT EXISTS(
                    SELECT 1 FROM customer_identity.persona_invitation_events terminal
                    WHERE terminal.invitation_hash=@Hash
                      AND terminal.action IN ('CONSUMED','CANCELLED','EXPIRED')
                )
                ON CONFLICT(invitation_hash,action) DO NOTHING;
                """, new {
                    Event = Guid.NewGuid(),
                    Actor = candidate.ActorId,
                    Hash = candidate.InvitationHash
                }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }
    }

    private sealed record ExpiredInvitation(string ActorId, string InvitationHash);

    private sealed class Row
    {
        public string ActorId { get; set; } = "";
        public string Email { get; set; } = "";
        public string State { get; set; } = "";
        public DateTimeOffset IssuedAt { get; set; }
        public DateTimeOffset? ConsumedAt { get; set; }
        public Guid IssueId { get; set; }
    }
}
