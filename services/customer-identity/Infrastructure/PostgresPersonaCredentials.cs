using Dapper;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Npgsql;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

// All rows live inside the tenant's C&I owner database. A supplied email can
// never pick an actor ID; only pre-existing canonical C&I principals qualify.
public sealed class PostgresPersonaCredentials(PostgresTenantSessions connections) : IPersonaCredentialRepository
{
    public async Task QueueAsync(string tenant, RegistrationCode challenge, CancellationToken ct)
    {
        await using var db = await connections.OpenAsync(tenant, ct).ConfigureAwait(false);
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO customer_identity.registration_challenges AS c
            (purpose,email,challenge_hash,code_hash,protected_code,requested_at,expires_at,window_at,requests)
            SELECT @Purpose,@Email,@ChallengeHash,@CodeHash,@ProtectedCode,@RequestedAt,
                @RequestedAt+interval '5 minutes',@RequestedAt,1
            WHERE EXISTS (
                SELECT 1 FROM customer_identity.tenant_principals p
                WHERE p.normalized_email=@Email
                  AND NOT EXISTS (SELECT 1 FROM customer_identity.registered_credentials r WHERE r.actor_id=p.actor_id)
                  AND (
                    (@Purpose='persona-activation' AND NOT EXISTS (
                        SELECT 1 FROM customer_identity.persona_credentials pc WHERE pc.actor_id=p.actor_id))
                    OR (@Purpose='persona-recovery' AND EXISTS (
                        SELECT 1 FROM customer_identity.persona_credentials pc WHERE pc.actor_id=p.actor_id))
                  )
            )
            ON CONFLICT(purpose,email) DO UPDATE
            SET challenge_hash=EXCLUDED.challenge_hash,code_hash=EXCLUDED.code_hash,
                protected_code=EXCLUDED.protected_code,
                requested_at=EXCLUDED.requested_at,expires_at=EXCLUDED.expires_at,
                window_at=CASE WHEN c.window_at<=@RequestedAt-interval '1 hour' THEN @RequestedAt ELSE c.window_at END,
                requests=CASE WHEN c.window_at<=@RequestedAt-interval '1 hour' THEN 1 ELSE c.requests+1 END,
                attempts=0,consumed=false,delivered=false,delivery_attempt=0,lease_until=NULL
            WHERE c.requested_at<=@RequestedAt-interval '1 minute'
              AND (c.window_at<=@RequestedAt-interval '1 hour' OR c.requests<5);
            """, challenge, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> QueueTesterAsync(
        string tenant, RegistrationCode challenge, CancellationToken ct)
    {
        await using var db = await connections.OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);

        // A retry of the same protected request reproduces the same challenge
        // hash. Reuse the still-live issuance without incrementing rate-limit
        // counters or appending another event.
        var replay = await db.QuerySingleOrDefaultAsync<TesterQueue>(new CommandDefinition("""
            SELECT c.expires_at AS ExpiresAt
            FROM customer_identity.registration_challenges c
            WHERE c.purpose='persona-activation' AND c.email=@Email
              AND c.challenge_hash=@ChallengeHash
              AND c.delivered AND NOT c.consumed
              AND c.expires_at>clock_timestamp()
              AND EXISTS(
                SELECT 1 FROM customer_identity.persona_invitation_events issued
                WHERE issued.invitation_hash=c.challenge_hash AND issued.action='ISSUED'
              )
              AND NOT EXISTS(
                SELECT 1 FROM customer_identity.persona_invitation_events terminal
                WHERE terminal.invitation_hash=c.challenge_hash
                  AND terminal.action IN ('CONSUMED','CANCELLED','EXPIRED')
              )
            FOR UPDATE OF c;
            """, challenge, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (replay is not null)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return replay.ExpiresAt;
        }

        // A deterministic ticket may never be resurrected after reaching
        // historical/terminal state. Intentional renewal requires a new
        // client request ID and therefore a new secret-derived ticket.
        var historical = await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(
              SELECT 1 FROM customer_identity.persona_invitation_events
              WHERE invitation_hash=@ChallengeHash
            );
            """, challenge, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (historical)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return null;
        }

        // Serialize renewals against the previous challenge, so successful
        // replacement cannot leave an earlier welcome ticket usable.
        var previous = await db.QuerySingleOrDefaultAsync<PreviousInvitation>(new CommandDefinition("""
            SELECT c.challenge_hash AS ChallengeHash,
                   c.expires_at<=clock_timestamp() AS Expired
            FROM customer_identity.registration_challenges c
            WHERE c.purpose='persona-activation' AND c.email=@Email
              AND c.delivered AND NOT c.consumed
              AND EXISTS(
                SELECT 1 FROM customer_identity.persona_invitation_events e
                WHERE e.invitation_hash=c.challenge_hash AND e.action='ISSUED'
              )
            FOR UPDATE OF c;
            """, new { challenge.Email }, tx, cancellationToken: ct)).ConfigureAwait(false);

        var written = await db.QuerySingleOrDefaultAsync<TesterQueue>(new CommandDefinition("""
            INSERT INTO customer_identity.registration_challenges AS c
            (purpose,email,challenge_hash,code_hash,protected_code,requested_at,expires_at,
             window_at,requests,delivered)
            SELECT @Purpose,@Email,@ChallengeHash,@CodeHash,NULL,@RequestedAt,
                @RequestedAt+interval '5 minutes',@RequestedAt,1,true
            WHERE @Purpose='persona-activation' AND EXISTS (
                SELECT 1 FROM customer_identity.tenant_principals p
                WHERE p.normalized_email=@Email
                  AND NOT EXISTS (SELECT 1 FROM customer_identity.registered_credentials r WHERE r.actor_id=p.actor_id)
                  AND NOT EXISTS (SELECT 1 FROM customer_identity.persona_credentials pc WHERE pc.actor_id=p.actor_id)
            )
            ON CONFLICT(purpose,email) DO UPDATE
            SET challenge_hash=EXCLUDED.challenge_hash,code_hash=EXCLUDED.code_hash,
                protected_code=NULL,requested_at=EXCLUDED.requested_at,
                expires_at=EXCLUDED.expires_at,
                window_at=CASE WHEN c.window_at<=@RequestedAt-interval '1 hour' THEN @RequestedAt ELSE c.window_at END,
                requests=CASE WHEN c.window_at<=@RequestedAt-interval '1 hour' THEN 1 ELSE c.requests+1 END,
                attempts=0,consumed=false,delivered=true,delivery_attempt=0,lease_until=NULL
            WHERE c.requested_at<=@RequestedAt-interval '1 minute'
              AND (c.window_at<=@RequestedAt-interval '1 hour' OR c.requests<5)
            RETURNING c.expires_at AS ExpiresAt;
            """, challenge, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);

        if (written is null)
        {
            // Concurrent duplicate requests can both miss the first read. The
            // conflict wait completes after the winner commits; re-read the
            // exact active hash and treat it as the same accepted request.
            replay = await db.QuerySingleOrDefaultAsync<TesterQueue>(new CommandDefinition("""
                SELECT c.expires_at AS ExpiresAt
                FROM customer_identity.registration_challenges c
                WHERE c.purpose='persona-activation' AND c.email=@Email
                  AND c.challenge_hash=@ChallengeHash
                  AND c.delivered AND NOT c.consumed
                  AND c.expires_at>clock_timestamp()
                  AND EXISTS(
                    SELECT 1 FROM customer_identity.persona_invitation_events issued
                    WHERE issued.invitation_hash=c.challenge_hash AND issued.action='ISSUED'
                  )
                  AND NOT EXISTS(
                    SELECT 1 FROM customer_identity.persona_invitation_events terminal
                    WHERE terminal.invitation_hash=c.challenge_hash
                      AND terminal.action IN ('CONSUMED','CANCELLED','EXPIRED')
                  );
                """, challenge, tx, cancellationToken: ct)).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return replay?.ExpiresAt;
        }

        if (previous is not null && previous.ChallengeHash != challenge.ChallengeHash)
        {
            // A successful renewal supersedes the former ticket. If its
            // five-minute lifetime already elapsed, preserve EXPIRED rather
            // than mislabelling the terminal event as administrator/user
            // cancellation.
            var action = previous.Expired ? "EXPIRED" : "CANCELLED";
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO customer_identity.persona_invitation_events
                  (event_id,actor_id,invitation_hash,action)
                SELECT @Event,e.actor_id,@Previous,@Action
                  FROM customer_identity.persona_invitation_events e
                 WHERE e.invitation_hash=@Previous AND e.action='ISSUED'
                   AND NOT EXISTS(
                     SELECT 1 FROM customer_identity.persona_invitation_events terminal
                     WHERE terminal.invitation_hash=@Previous
                       AND terminal.action IN ('CONSUMED','CANCELLED','EXPIRED')
                   )
                ON CONFLICT(invitation_hash,action) DO NOTHING;
                """, new { Event = Guid.NewGuid(), Previous = previous.ChallengeHash, Action = action },
                tx, cancellationToken: ct)).ConfigureAwait(false);
        }

        // Recorded only after the challenge was actually accepted. No OTP,
        // bearer link, recipient email or unhashed secret is retained here.
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO customer_identity.persona_invitation_events
              (event_id,actor_id,invitation_hash,action)
            SELECT @Event,p.actor_id,@Hash,'ISSUED'
              FROM customer_identity.tenant_principals p
             WHERE p.normalized_email=@Email;
            """, new { Event = Guid.NewGuid(), Hash = challenge.ChallengeHash, challenge.Email },
            transaction: tx, cancellationToken: ct)).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return written.ExpiresAt;
    }

    private static async Task<string?> ConsumeAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        string purpose, string hash, string verifier, CancellationToken ct)
    {
        // Do not roll back incorrect attempts; failed attempts are durable.
        var row = await db.QuerySingleOrDefaultAsync<Challenge>(new CommandDefinition("""
            UPDATE customer_identity.registration_challenges SET attempts=attempts+1,
              consumed=(code_hash=@Verifier),protected_code=NULL
            WHERE purpose=@Purpose AND challenge_hash=@Hash AND delivered AND NOT consumed
              AND attempts<5 AND expires_at>clock_timestamp()
            RETURNING email AS Email,code_hash=@Verifier AS Valid;
            """, new { Purpose = purpose, Hash = hash, Verifier = verifier },
            tx, cancellationToken: ct)).ConfigureAwait(false);
        return row?.Valid == true ? row.Email : null;
    }

    public async Task<string?> ActivateAsync(string tenant, string hash, string verifier, string passwordHash, CancellationToken ct)
    {
        await using var db = await connections.OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        var email = await ConsumeAsync(db, tx, "persona-activation", hash, verifier, ct).ConfigureAwait(false);
        string? actor = null;
        if (email is not null)
            actor = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
                INSERT INTO customer_identity.persona_credentials(actor_id,password_hash)
                SELECT p.actor_id,@PasswordHash FROM customer_identity.tenant_principals p
                WHERE p.normalized_email=@Email
                  AND NOT EXISTS(SELECT 1 FROM customer_identity.registered_credentials r WHERE r.actor_id=p.actor_id)
                ON CONFLICT(actor_id) DO NOTHING RETURNING actor_id;
                """, new { Email = email, PasswordHash = passwordHash }, tx, cancellationToken: ct))
                .ConfigureAwait(false);
        if (actor is not null)
        {
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO customer_identity.registration_events(event_id,action,actor_id)
                VALUES(@Id,'PERSONA_ACTIVATED',@Actor);
                """, new { Id = Guid.NewGuid(), Actor = actor }, tx, cancellationToken: ct)).ConfigureAwait(false);
            // Existing production email activation may have no tester invitation;
            // ledger writes are conditional, and share the credential transaction.
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO customer_identity.persona_invitation_events
                  (event_id,actor_id,invitation_hash,action)
                SELECT @Event,@Actor,@Hash,'CONSUMED'
                WHERE EXISTS (
                  SELECT 1 FROM customer_identity.persona_invitation_events
                  WHERE invitation_hash=@Hash AND actor_id=@Actor AND action='ISSUED'
                )
                ON CONFLICT(invitation_hash,action) DO NOTHING;
                """, new { Event = Guid.NewGuid(), Actor = actor, Hash = hash },
                tx, cancellationToken: ct)).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return actor;
    }

    public async Task<PersonaCredentialIdentity?> ReadAsync(string tenant, string email, CancellationToken ct)
    {
        await using var db = await connections.OpenAsync(tenant, ct).ConfigureAwait(false);
        return await db.QuerySingleOrDefaultAsync<PersonaCredentialIdentity>(new CommandDefinition("""
            SELECT @Tenant AS TenantId,p.normalized_email AS Email,pc.actor_id AS ActorId,
                pc.password_hash AS PasswordHash,pc.credential_version AS CredentialVersion
            FROM customer_identity.persona_credentials pc
            JOIN customer_identity.tenant_principals p ON p.actor_id=pc.actor_id
            WHERE p.normalized_email=@Email;
            """, new { Tenant = tenant, Email = email }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> RecordAttemptAsync(PersonaCredentialIdentity identity, bool valid, CancellationToken ct)
    {
        await using var db = await connections.OpenAsync(identity.TenantId, ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        var modified = await db.ExecuteAsync(new CommandDefinition("""
            UPDATE customer_identity.persona_credentials SET
                failed_attempts=CASE WHEN @Valid THEN 0
                    WHEN locked_until<=clock_timestamp() THEN 1 ELSE failed_attempts+1 END,
                locked_until=CASE WHEN @Valid THEN NULL
                    WHEN failed_attempts>=4 AND (locked_until IS NULL OR locked_until>clock_timestamp())
                    THEN clock_timestamp()+interval '15 minutes' ELSE NULL END
            WHERE actor_id=@ActorId AND credential_version=@CredentialVersion
                AND (locked_until IS NULL OR locked_until<=clock_timestamp());
            """, new { identity.ActorId, identity.CredentialVersion, Valid = valid },
            tx, cancellationToken: ct)).ConfigureAwait(false);
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO customer_identity.registration_events(event_id,action,actor_id)
            VALUES(@Id,@Action,@Actor);
            """, new { Id = Guid.NewGuid(), Actor = identity.ActorId,
                Action = valid && modified == 1 ? "PERSONA_PASSWORD_VERIFIED" : "PERSONA_PASSWORD_REJECTED" },
            tx, cancellationToken: ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return modified == 1;
    }

    public async Task<bool> IsCurrentAsync(PersonaCredentialIdentity identity, CancellationToken ct)
    {
        await using var db = await connections.OpenAsync(identity.TenantId, ct).ConfigureAwait(false);
        return await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM customer_identity.persona_credentials
            WHERE actor_id=@ActorId AND credential_version=@CredentialVersion);
            """, identity, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> RecoverAsync(string tenant, string hash, string verifier, string newHash, CancellationToken ct)
    {
        await using var db = await connections.OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        var email = await ConsumeAsync(db, tx, "persona-recovery", hash, verifier, ct).ConfigureAwait(false);
        string? actor = null;
        if (email is not null)
            actor = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
                UPDATE customer_identity.persona_credentials pc
                SET password_hash=@Hash,credential_version=credential_version+1,
                    failed_attempts=0,locked_until=NULL
                FROM customer_identity.tenant_principals p
                WHERE p.actor_id=pc.actor_id AND p.normalized_email=@Email
                RETURNING pc.actor_id;
                """, new { Hash = newHash, Email = email }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (actor is not null)
        {
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE customer_identity.trusted_sessions SET revoked=true WHERE actor_id=@Actor;
                INSERT INTO customer_identity.registration_events(event_id,action,actor_id)
                VALUES(@Id,'PERSONA_PASSWORD_RECOVERED',@Actor);
                """, new { Actor = actor, Id = Guid.NewGuid() }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return actor is not null;
    }
    private sealed record TesterQueue(DateTimeOffset ExpiresAt);
    private sealed record PreviousInvitation(string ChallengeHash, bool Expired);
    private sealed record Challenge(string Email, bool Valid);
}
