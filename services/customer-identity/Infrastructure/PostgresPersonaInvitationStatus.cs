using Dapper;
using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

// Read-only projection of the C&I-owned invitation ledger. This does not
// grant membership, issue credentials, or expose invitation bearer hashes.
public sealed record PersonaInvitationStatus(string ActorId, string Email, string State,
    DateTimeOffset IssuedAt, DateTimeOffset? ConsumedAt);
public sealed record PersonaInvitationStatusPage(string TenantId,
    IReadOnlyList<PersonaInvitationStatus> Items, string? NextCursor);

public sealed class PostgresPersonaInvitationStatus(PostgresTenantSessions connections)
{
    public async Task<PersonaInvitationStatusPage> ListAsync(string tenant, string? after, CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(tenant, 64);
        if (after is not null && (after.Length > 128 || after.Length == 0 || after.Any(char.IsControl)))
            throw new TenantAccessException("INVITATION_CURSOR_INVALID", 400);
        await using var db = await connections.OpenAsync(tenant, ct).ConfigureAwait(false);
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
                   WHEN l.issued_at<=clock_timestamp()-interval '5 minutes'
                      THEN 'Expired'
                   ELSE 'Pending' END AS State,
              l.issue_id AS IssueId
            FROM latest l JOIN customer_identity.tenant_principals p
              ON p.actor_id=l.actor_id
            LEFT JOIN customer_identity.persona_invitation_events consumed
              ON consumed.invitation_hash=l.invitation_hash AND consumed.action='CONSUMED'
            WHERE (@After IS NULL OR l.actor_id>@After)
            ORDER BY l.actor_id LIMIT 51;
            """, new { After = after }, cancellationToken: ct)).ConfigureAwait(false)).ToArray();
        // Actor ID is used as a stable page cursor; the owner limits disclosure.
        var items = rows.Take(50).Select(r =>
            new PersonaInvitationStatus(r.ActorId, r.Email, r.State, r.IssuedAt, r.ConsumedAt)).ToArray();
        return new(tenant, items, rows.Length > 50 ? rows[49].ActorId : null);
    }

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
