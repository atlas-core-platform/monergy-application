using Dapper;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Npgsql;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

public sealed class PostgresCustomerEmailAccess(PostgresTenantSessions sessions) : ICustomerEmailAccessRepository
{
    public async Task QueueAsync(string tenant, EmailChallengeWrite challenge, CancellationToken ct)
    {
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var customer = await connection.QuerySingleOrDefaultAsync<EmailCustomer>(new CommandDefinition("""
            INSERT INTO customer_identity.email_access_challenges AS current
                (normalized_email,challenge_hash,code_hash,protected_code,actor_id,customer_id,customer_version,requested_at,expires_at,request_window,requests)
            SELECT p.normalized_email,@ChallengeHash,@CodeHash,@ProtectedCode,p.actor_id,
                max(c.customer_id),max(c.version),@Now,@Now+interval '5 minutes',@Now,1
            FROM customer_identity.tenant_principals p
            JOIN customer_identity.current_customers c ON c.owner_actor_id=p.actor_id AND c.active
            WHERE p.normalized_email=@Email AND NOT EXISTS(SELECT 1 FROM customer_identity.registered_credentials r WHERE r.actor_id=p.actor_id)
            GROUP BY p.normalized_email,p.actor_id HAVING count(*)=1
            ON CONFLICT(normalized_email) DO UPDATE SET
                challenge_hash=EXCLUDED.challenge_hash,code_hash=EXCLUDED.code_hash,protected_code=EXCLUDED.protected_code,
                actor_id=EXCLUDED.actor_id,customer_id=EXCLUDED.customer_id,customer_version=EXCLUDED.customer_version,
                requested_at=EXCLUDED.requested_at,expires_at=EXCLUDED.expires_at,attempts=0,consumed=false,delivered=false,
                delivery_attempt=0,lease_until=NULL,
                request_window=CASE WHEN current.request_window<=@Now-interval '1 hour' THEN @Now ELSE current.request_window END,
                requests=CASE WHEN current.request_window<=@Now-interval '1 hour' THEN 1 ELSE current.requests+1 END
            WHERE current.requested_at<=@Now-interval '60 seconds'
                AND (current.request_window<=@Now-interval '1 hour' OR current.requests<5)
            RETURNING actor_id AS ActorId,customer_id AS CustomerId,customer_version AS CustomerVersion;
            """, challenge, transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (customer is not null)
            await EventAsync(connection, transaction, challenge.ChallengeHash, customer, "REQUESTED", challenge.Now, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<EmailCustomer?> ConsumeAsync(string tenant, string challengeHash, string codeHash, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        // One UPDATE locks and consumes the challenge across requests/processes.
        // Failed attempts commit too; no pre-check/read race or plaintext code.
        var result = await connection.QuerySingleOrDefaultAsync<ConsumedRow>(new CommandDefinition("""
            UPDATE customer_identity.email_access_challenges SET attempts=attempts+1,
                consumed=(code_hash=@CodeHash),
                protected_code=CASE WHEN code_hash=@CodeHash OR attempts+1>=5 THEN NULL ELSE protected_code END
            WHERE challenge_hash=@ChallengeHash AND NOT consumed AND delivered AND expires_at>@Now AND attempts<5
              AND NOT EXISTS(SELECT 1 FROM customer_identity.registered_credentials r WHERE r.actor_id=customer_identity.email_access_challenges.actor_id)
            RETURNING actor_id AS ActorId,customer_id AS CustomerId,customer_version AS CustomerVersion,
                code_hash=@CodeHash AS Verified;
            """, new { ChallengeHash = challengeHash, CodeHash = codeHash, Now = now }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        EmailCustomer? customer = result is null ? null : new(result.ActorId, result.CustomerId, result.CustomerVersion);
        if (customer is not null)
            await EventAsync(connection, transaction, challengeHash, customer, result!.Verified ? "CODE_VERIFIED" : "CODE_REJECTED", now, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result?.Verified == true ? customer : null;
    }

    public async Task<bool> IsCurrentAsync(string tenant, EmailCustomer customer, CancellationToken ct)
    {
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM customer_identity.current_customers WHERE customer_id=@CustomerId
                AND owner_actor_id=@ActorId AND version=@CustomerVersion AND active)
                AND (SELECT count(*) FROM customer_identity.current_customers WHERE owner_actor_id=@ActorId AND active)=1
                AND NOT EXISTS(SELECT 1 FROM customer_identity.registered_credentials WHERE actor_id=@ActorId);
            """, customer, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task RecordSessionAsync(string tenant, string challengeHash, EmailCustomer customer, bool issued, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await EventAsync(connection, transaction, challengeHash, customer, issued ? "SESSION_ISSUED" : "SESSION_DENIED", now, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<PendingCustomerEmail?> LeaseAsync(string tenant, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE customer_identity.email_access_challenges SET protected_code=NULL
            WHERE protected_code IS NOT NULL AND (expires_at<=@Now OR consumed OR attempts>=5);
            """, new { Now = now }, cancellationToken: ct)).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<PendingCustomerEmail>(new CommandDefinition("""
            UPDATE customer_identity.email_access_challenges SET lease_until=@Now+interval '20 seconds',delivery_attempt=delivery_attempt+1
            WHERE challenge_hash=(SELECT challenge_hash FROM customer_identity.email_access_challenges
                WHERE protected_code IS NOT NULL AND NOT consumed AND NOT delivered AND expires_at>@Now
                    AND delivery_attempt<3 AND (lease_until IS NULL OR lease_until<=@Now)
                ORDER BY requested_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING challenge_hash AS ChallengeHash,normalized_email AS Email,protected_code AS ProtectedCode,delivery_attempt AS DeliveryAttempt;
            """, new { Now = now }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task CompleteDeliveryAsync(string tenant, string challengeHash, int deliveryAttempt, bool sent, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var customer = await connection.QuerySingleOrDefaultAsync<EmailCustomer>(new CommandDefinition("""
            UPDATE customer_identity.email_access_challenges SET delivered=@Sent,
                protected_code=CASE WHEN @Sent OR delivery_attempt>=3 OR expires_at<=@Now THEN NULL ELSE protected_code END
            WHERE challenge_hash=@ChallengeHash AND delivery_attempt=@DeliveryAttempt AND NOT delivered AND NOT consumed
            RETURNING actor_id AS ActorId,customer_id AS CustomerId,customer_version AS CustomerVersion;
            """, new { ChallengeHash = challengeHash, DeliveryAttempt = deliveryAttempt, Sent = sent, Now = now }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (customer is not null)
            await EventAsync(connection, transaction, challengeHash, customer, sent ? "DELIVERED" : "DELIVERY_FAILED", now, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<NpgsqlConnection> OpenAsync(string tenant, CancellationToken ct)
    {
        var connection = await sessions.OpenAsync(tenant, ct).ConfigureAwait(false);
        try
        {
            var safe = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT NOT has_table_privilege(current_user,'customer_identity.email_access_events','UPDATE,DELETE,TRUNCATE')
                    AND NOT has_table_privilege(current_user,'customer_identity.email_access_challenges','DELETE,TRUNCATE');
                """, cancellationToken: ct)).ConfigureAwait(false);
            if (!safe) throw new TenantAccessException("EMAIL_ACCESS_BINDING_INVALID", 503);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static Task<int> EventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string hash,
        EmailCustomer customer, string outcome, DateTimeOffset now, CancellationToken ct) => connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO customer_identity.email_access_events(event_id,challenge_hash,actor_id,customer_id,outcome,recorded_at)
            VALUES (@EventId,@Hash,@ActorId,@CustomerId,@Outcome,@Now);
            """, new { EventId = Guid.NewGuid(), Hash = hash, customer.ActorId, customer.CustomerId, Outcome = outcome, Now = now }, transaction, cancellationToken: ct));

    private sealed class ConsumedRow
    {
        public string ActorId { get; set; } = "";
        public string CustomerId { get; set; } = "";
        public long CustomerVersion { get; set; }
        public bool Verified { get; set; }
    }
}
