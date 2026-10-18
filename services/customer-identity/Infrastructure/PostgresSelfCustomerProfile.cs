using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Npgsql;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

public sealed class PostgresSelfCustomerProfile(PostgresTenantSessions sessions) : ISelfCustomerProfileRepository
{
    public async Task<bool> OwnsAsync(string tenant, string actor, string customer, CancellationToken ct)
    {
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM customer_identity.customers WHERE customer_id=@Customer AND owner_actor_id=@Actor AND active)
                AND (SELECT count(*) FROM customer_identity.customers WHERE owner_actor_id=@Actor AND active)=1;
            """, new { Customer = customer, Actor = actor }, cancellationToken: ct)).ConfigureAwait(false);
    }
    public async Task<SelfCustomerProfile?> ReadAsync(string tenant, string actor, CancellationToken ct)
    {
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<ProfileRow>(new CommandDefinition("""
            SELECT c.customer_id AS CustomerId,coalesce(p.display_name,c.display_name) AS DisplayName,
                i.normalized_email AS Email,p.mobile AS Mobile,p.date_of_birth AS DateOfBirth,coalesce(p.version,0) AS Version,p.updated_at AS UpdatedAt,
                (SELECT max(recorded_at) FROM customer_identity.email_access_events
                    WHERE actor_id=@Actor AND customer_id=c.customer_id AND outcome='CODE_VERIFIED') AS EmailVerifiedAt
            FROM customer_identity.customers c
            JOIN customer_identity.tenant_principals i ON i.actor_id=c.owner_actor_id
            LEFT JOIN customer_identity.customer_profiles p ON p.customer_id=c.customer_id
            WHERE c.owner_actor_id=@Actor AND c.active ORDER BY c.customer_id LIMIT 2;
            """, new { Actor = actor }, cancellationToken: ct)).ConfigureAwait(false)).ToArray();
        if (rows.Length > 1) throw new TenantAccessException("CUSTOMER_CONTEXT_AMBIGUOUS", 409);
        if (rows.Length == 0) return null;
        var row = rows[0];
        return new(tenant, actor, row.CustomerId, row.DisplayName, row.Email, row.Mobile, row.Version, Utc(row.UpdatedAt), Utc(row.EmailVerifiedAt))
        { DateOfBirth = row.DateOfBirth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
    }

    public async Task<SelfCustomerProfileReceipt> SaveAsync(string tenant, string actor, string customer, SelfCustomerProfileChange change, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        // Serialize edits to this profile without granting UPDATE on the
        // ownership table merely to obtain a SELECT FOR UPDATE row lock.
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@Customer, 44));",
            new { Customer = customer }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        var customers = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT customer_id FROM customer_identity.customers WHERE owner_actor_id=@Actor AND active
            ORDER BY customer_id LIMIT 2;
            """, new { Actor = actor }, transaction, cancellationToken: ct)).ConfigureAwait(false)).ToArray();
        if (customers.Length != 1) throw new TenantAccessException("OWNED_CUSTOMER_REQUIRED", 403);
        if (customers[0] != customer) throw new TenantAccessException("PROFILE_CUSTOMER_CHANGED", 409);
        var operation = Guid.ParseExact(change.OperationId, "N");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            change.HasDateOfBirth
                ? (object)new { Customer = customer, Actor = actor, change.ExpectedVersion, change.DisplayName, change.Mobile, change.DateOfBirth }
                : new { Customer = customer, Actor = actor, change.ExpectedVersion, change.DisplayName, change.Mobile }))));
        var prior = await connection.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition("""
            SELECT customer_id AS CustomerId,actor_id AS ActorId,fingerprint AS Fingerprint,profile_version AS Version
            FROM customer_identity.customer_profile_receipts WHERE operation_id=@Operation;
            """, new { Operation = operation }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (prior is not null)
        {
            if (prior.CustomerId != customer || prior.ActorId != actor || prior.Fingerprint != fingerprint)
                throw new TenantAccessException("PROFILE_OPERATION_CONFLICT", 409);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new(change.OperationId, prior.Version);
        }
        var currentVersion = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition("""
            SELECT version FROM customer_identity.customer_profiles WHERE customer_id=@Customer;
            """, new { Customer = customer }, transaction, cancellationToken: ct)).ConfigureAwait(false) ?? 0;
        if (currentVersion != change.ExpectedVersion) throw new TenantAccessException("PROFILE_VERSION_CONFLICT", 409);
        var version = checked(currentVersion + 1);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO customer_identity.customer_profiles(customer_id,display_name,mobile,date_of_birth,version,updated_at)
            VALUES (@Customer,@DisplayName,@Mobile,CAST(@DateOfBirth AS date),@Version,@Now)
            ON CONFLICT(customer_id) DO UPDATE SET display_name=EXCLUDED.display_name,mobile=EXCLUDED.mobile,
                date_of_birth=CASE WHEN @HasDateOfBirth THEN EXCLUDED.date_of_birth ELSE customer_identity.customer_profiles.date_of_birth END,
                version=EXCLUDED.version,updated_at=EXCLUDED.updated_at;
            INSERT INTO customer_identity.customer_profile_receipts(operation_id,customer_id,actor_id,fingerprint,profile_version,recorded_at)
            VALUES (@Operation,@Customer,@Actor,@Fingerprint,@Version,@Now);
            """, new { Customer = customer, change.DisplayName, change.Mobile, change.DateOfBirth, change.HasDateOfBirth, Version = version, Now = now,
                Operation = operation, Actor = actor, Fingerprint = fingerprint }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        var stillOwned = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM customer_identity.customers WHERE customer_id=@Customer AND owner_actor_id=@Actor AND active)
                AND (SELECT count(*) FROM customer_identity.customers WHERE owner_actor_id=@Actor AND active)=1;
            """, new { Customer = customer, Actor = actor }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (!stillOwned) throw new TenantAccessException("PROFILE_CUSTOMER_CHANGED", 409);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(change.OperationId, version);
    }

    private async Task<NpgsqlConnection> OpenAsync(string tenant, CancellationToken ct)
    {
        var connection = await sessions.OpenAsync(tenant, ct).ConfigureAwait(false);
        try
        {
            var safe = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT NOT has_table_privilege(current_user,'customer_identity.customer_profile_receipts','UPDATE,DELETE,TRUNCATE')
                    AND NOT has_table_privilege(current_user,'customer_identity.customer_profiles','DELETE,TRUNCATE');
                """, cancellationToken: ct)).ConfigureAwait(false);
            if (!safe) throw new TenantAccessException("PROFILE_BINDING_INVALID", 503);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private static DateTimeOffset? Utc(DateTime? value) => value.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)) : null;
    private sealed class ProfileRow
    {
        public string CustomerId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public string? Mobile { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public long Version { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? EmailVerifiedAt { get; set; }
    }
    private sealed class ReceiptRow
    {
        public string CustomerId { get; set; } = "";
        public string ActorId { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public long Version { get; set; }
    }
}
