using Dapper;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Npgsql;
using System.Security.Cryptography;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

public sealed class PostgresCustomerPan(PostgresTenantSessions sessions, CustomerPanProtection protection) : ICustomerPanRepository
{
    public async Task<PanState?> ReadAsync(string tenant, string actor, string customer, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(connection, tx, customer, ct).ConfigureAwait(false);
        await OwnerAsync(connection, tx, actor, customer, ct).ConfigureAwait(false);
        var row = await StateAsync(connection, tx, customer, ct).ConfigureAwait(false);
        if (row is not null && row.ActorId != actor) throw new TenantAccessException("PAN_CUSTOMER_CHANGED", 409);
        await AuditAsync(connection, tx, actor, customer, row?.Version ?? 0, "READ", now, ct).ConfigureAwait(false);
        await OwnerAsync(connection, tx, actor, customer, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return row is null ? null : new(row.ActorId, row.Version, row.KeyId, row.Ciphertext, row.VerificationReference,
            new DateTimeOffset(DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc)));
    }
    public async Task<CustomerPanReceipt> SaveAsync(string tenant, string actor, string customer, CustomerPanChange change, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(connection, tx, customer, ct).ConfigureAwait(false);
        await OwnerAsync(connection, tx, actor, customer, ct).ConfigureAwait(false);
        var operation = Guid.ParseExact(change.OperationId, "N");
        var prior = await connection.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition("""
            SELECT actor_id AS ActorId,customer_id AS CustomerId,fingerprint_key_id AS KeyId,fingerprint AS Fingerprint,
                pan_version AS Version,verification_reference AS VerificationReference
            FROM customer_identity.customer_pan_receipts WHERE operation_id=@Operation;
            """, new { Operation = operation }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (prior is not null)
        {
            if (prior.ActorId != actor || prior.CustomerId != customer ||
                prior.Fingerprint != protection.Fingerprint(prior.KeyId, tenant, actor, customer, change))
                throw new TenantAccessException("PAN_OPERATION_CONFLICT", 409);
            await AuditAsync(connection, tx, actor, customer, prior.Version, "REPLAY", now, ct).ConfigureAwait(false);
            await OwnerAsync(connection, tx, actor, customer, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new(change.OperationId, prior.Version, prior.VerificationReference);
        }
        var current = await StateAsync(connection, tx, customer, ct).ConfigureAwait(false);
        if (current is not null && current.ActorId != actor) throw new TenantAccessException("PAN_CUSTOMER_CHANGED", 409);
        if ((current?.Version ?? 0) != change.ExpectedVersion) throw new TenantAccessException("PAN_VERSION_CONFLICT", 409);
        var version = checked(change.ExpectedVersion + 1);
        var reference = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var encrypted = change.Pan is null ? null : protection.Protect(tenant, actor, customer, version, reference, change.Pan);
        var fingerprint = protection.Fingerprint(protection.CurrentKeyId, tenant, actor, customer, change);
        // Only ciphertext and keyed operation fingerprints cross into SQL. The
        // request DTO and the plaintext identifier are never SQL parameters.
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO customer_identity.customer_pan(customer_id,actor_id,key_id,protected_pan,version,verification_reference,updated_at)
            VALUES (@Customer,@Actor,@KeyId,@Encrypted,@Version,@Reference,@Now)
            ON CONFLICT(customer_id) DO UPDATE SET key_id=EXCLUDED.key_id,protected_pan=EXCLUDED.protected_pan,
                version=EXCLUDED.version,verification_reference=EXCLUDED.verification_reference,updated_at=EXCLUDED.updated_at;
            INSERT INTO customer_identity.customer_pan_receipts(operation_id,customer_id,actor_id,fingerprint_key_id,fingerprint,pan_version,verification_reference,recorded_at)
            VALUES (@Operation,@Customer,@Actor,@FingerprintKeyId,@Fingerprint,@Version,@Reference,@Now);
            """, new { Customer = customer, Actor = actor, KeyId = encrypted is null ? null : protection.CurrentKeyId,
                Encrypted = encrypted, Version = version, Reference = reference, Now = now, Operation = operation,
                FingerprintKeyId = protection.CurrentKeyId, Fingerprint = fingerprint }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await AuditAsync(connection, tx, actor, customer, version, encrypted is null ? "REMOVED" : "RECORDED", now, ct).ConfigureAwait(false);
        await OwnerAsync(connection, tx, actor, customer, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new(change.OperationId, version, reference);
    }
    private static Task<int> LockAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string customer, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Customer,56));", new { Customer = customer }, tx, cancellationToken: ct));
    private static async Task OwnerAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string actor, string customer, CancellationToken ct)
    {
        var owns = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM customer_identity.current_customers WHERE customer_id=@Customer AND owner_actor_id=@Actor AND active)
                AND (SELECT count(*) FROM customer_identity.current_customers WHERE owner_actor_id=@Actor AND active)=1;
            """, new { Customer = customer, Actor = actor }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (!owns) throw new TenantAccessException("OWNED_CUSTOMER_REQUIRED", 403);
    }
    private static Task<StateRow?> StateAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string customer, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition("""
            SELECT actor_id AS ActorId,key_id AS KeyId,protected_pan AS Ciphertext,version AS Version,
                verification_reference AS VerificationReference,updated_at AS UpdatedAt
            FROM customer_identity.customer_pan WHERE customer_id=@Customer;
            """, new { Customer = customer }, tx, cancellationToken: ct));
    private static Task<int> AuditAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string actor, string customer, long version, string outcome, DateTimeOffset now, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO customer_identity.customer_pan_audit(event_id,customer_id,actor_id,pan_version,outcome,recorded_at)
            VALUES (@Event,@Customer,@Actor,@Version,@Outcome,@Now);
            """, new { Event = Guid.NewGuid(), Customer = customer, Actor = actor, Version = version, Outcome = outcome, Now = now }, tx, cancellationToken: ct));
    private async Task<NpgsqlConnection> OpenAsync(string tenant, CancellationToken ct)
    {
        var connection = await sessions.OpenAsync(tenant, ct).ConfigureAwait(false);
        try
        {
            var safe = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT NOT has_table_privilege(current_user,'customer_identity.customer_pan','DELETE,TRUNCATE')
                    AND NOT has_table_privilege(current_user,'customer_identity.customer_pan_receipts','UPDATE,DELETE,TRUNCATE')
                    AND NOT has_table_privilege(current_user,'customer_identity.customer_pan_audit','UPDATE,DELETE,TRUNCATE');
                """, cancellationToken: ct)).ConfigureAwait(false);
            if (!safe) throw new TenantAccessException("PAN_BINDING_INVALID", 503);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private sealed class StateRow
    {
        public string ActorId { get; set; } = "";
        public string? KeyId { get; set; }
        public byte[]? Ciphertext { get; set; }
        public long Version { get; set; }
        public string VerificationReference { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
    }
    private sealed class ReceiptRow
    {
        public string ActorId { get; set; } = "";
        public string CustomerId { get; set; } = "";
        public string KeyId { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public long Version { get; set; }
        public string VerificationReference { get; set; } = "";
    }
}
