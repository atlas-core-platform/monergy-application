using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Monergy.Platform;
using Npgsql;

namespace Monergy.Services.Consent;

public sealed class PostgresCustomerConsent : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, NpgsqlDataSource> sources = new(StringComparer.Ordinal);

    public PostgresCustomerConsent(IConfiguration configuration)
    {
        TenantAccessIntegration.EnsureAllowed(configuration);
        foreach (var entry in configuration.GetSection("Monergy:AccessIntegration:ConsentDatabases").GetChildren())
        {
            if (!TenantAccessClient.Identifier(entry.Key, 64)) throw new InvalidOperationException("Invalid Consent tenant binding.");
            var options = new NpgsqlConnectionStringBuilder(entry.Value ?? "")
            { IncludeErrorDetail = false, NoResetOnClose = false, Timeout = 5, CommandTimeout = 15, MaxPoolSize = 20 };
            sources.Add(entry.Key, NpgsqlDataSource.Create(options.ConnectionString));
        }
        if (sources.Count == 0) throw new InvalidOperationException("Consent database bindings are required.");
    }

    public async Task<object> ListAsync(string tenant, string customer, CancellationToken ct)
    {
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var version = await RevisionAsync(connection, transaction, false, ct).ConfigureAwait(false);
        var items = (await connection.QueryAsync<GrantRow>(Command("""
            SELECT grant_id AS Id,customer_id AS CustomerId,actor_id AS ActorId,purpose AS Purpose,
                capability_ids AS CapabilityIds,expires_at AS ExpiresAt,revoked_at AS RevokedAt
            FROM consent.grants WHERE customer_id=@Customer ORDER BY granted_at DESC LIMIT 100;
            """, new { Customer = customer }, transaction, ct)).ConfigureAwait(false)).Select(row =>
                new ConsentGrant(row.Id, row.CustomerId, row.ActorId, row.Purpose, row.CapabilityIds,
                    Utc(row.ExpiresAt), row.RevokedAt is { } revoked ? Utc(revoked) : null)).ToArray();
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new { version, items };
    }

    public async Task<ConsentReceipt> GrantAsync(string tenant, CustomerOwnerContext owner, string ownerActor,
        ConsentGrantWrite request, CancellationToken ct)
    {
        request = CustomerConsentPolicy.Validate(request, null);
        RequireOwner(tenant, owner, ownerActor, request.CustomerId);
        if (request.ActorId == ownerActor) throw new TenantBoundaryException("SELF_CONSENT_NOT_REQUIRED", 409);
        var hash = Hash(new { operation = "Granted", ownerActor, ownerVersion = owner.Version, request });
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var version = await RevisionAsync(connection, transaction, true, ct).ConfigureAwait(false);
        var replay = await ReplayAsync(connection, transaction, request.RequestId, hash, ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        _ = CustomerConsentPolicy.Validate(request, DateTimeOffset.UtcNow);
        if (version != request.ExpectedVersion) throw new TenantBoundaryException("CONSENT_VERSION_CONFLICT", 409);
        var existing = await connection.ExecuteScalarAsync<bool>(Command("""
            SELECT EXISTS(SELECT 1 FROM consent.grants WHERE customer_id=@CustomerId AND actor_id=@ActorId AND purpose=@Purpose AND revoked_at IS NULL);
            """, request, transaction, ct)).ConfigureAwait(false);
        if (existing) throw new TenantBoundaryException("REVOKE_EXISTING_CONSENT_FIRST", 409);
        var id = Guid.NewGuid().ToString("N");
        await connection.ExecuteAsync(Command("""
            INSERT INTO consent.grants(grant_id,customer_id,actor_id,purpose,capability_ids,owner_actor_id,owner_version,expires_at)
            VALUES(@Id,@CustomerId,@ActorId,@Purpose,@Capabilities,@OwnerActor,@OwnerVersion,@ExpiresAt);
            """, new
        {
            Id = id,
            request.CustomerId,
            request.ActorId,
            request.Purpose,
            Capabilities = request.CapabilityIds,
            OwnerActor = ownerActor,
            OwnerVersion = owner.Version,
            request.ExpiresAt
        }, transaction, ct)).ConfigureAwait(false);
        var receipt = await CommitAsync(connection, transaction, request.RequestId, hash, ownerActor, request.CustomerId,
            request.ActorId, id, "Granted", version + 1, ct).ConfigureAwait(false);
        return receipt;
    }

    public async Task<ConsentReceipt> RevokeAsync(string tenant, CustomerOwnerContext owner, string ownerActor, string grantId,
        ConsentRevocation request, CancellationToken ct)
    {
        CustomerConsentPolicy.ValidateRevision(request.RequestId, request.ExpectedVersion);
        if (!TenantAccessClient.Identifier(grantId, 128)) throw new TenantBoundaryException("INVALID_CONSENT", 400);
        RequireOwner(tenant, owner, ownerActor, owner.CustomerId);
        var hash = Hash(new { operation = "Revoked", ownerActor, customerId = owner.CustomerId, grantId, request });
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var version = await RevisionAsync(connection, transaction, true, ct).ConfigureAwait(false);
        var replay = await ReplayAsync(connection, transaction, request.RequestId, hash, ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        if (version != request.ExpectedVersion) throw new TenantBoundaryException("CONSENT_VERSION_CONFLICT", 409);
        var actor = await connection.QuerySingleOrDefaultAsync<string>(Command("""
            UPDATE consent.grants SET revoked_at=clock_timestamp() WHERE grant_id=@GrantId AND customer_id=@CustomerId AND revoked_at IS NULL
            RETURNING actor_id;
            """, new { GrantId = grantId, owner.CustomerId }, transaction, ct)).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("CONSENT_NOT_CURRENT", 409);
        return await CommitAsync(connection, transaction, request.RequestId, hash, ownerActor, owner.CustomerId,
            actor, grantId, "Revoked", version + 1, ct).ConfigureAwait(false);
    }

    public async Task<ConsentDecision> EvaluateAsync(string trustedTenant, CustomerOwnerContext owner, ConsentEvaluation request, CancellationToken ct)
    {
        if (request.TenantId != trustedTenant || !TenantAccessClient.Identifier(request.ActorId, 128) ||
            !TenantAccessClient.Identifier(request.CustomerId, 128) || request.Purpose != CustomerConsentPolicy.Purpose ||
            !CustomerConsentPolicy.Capabilities.Contains(request.CapabilityId)) throw new TenantBoundaryException("INVALID_CONSENT_EVALUATION", 400);
        if (owner.TenantId != trustedTenant || owner.CustomerId != request.CustomerId || !owner.Active || owner.Version < 1)
            throw new TenantBoundaryException("CUSTOMER_UNAVAILABLE", 503);
        await using var connection = await OpenAsync(trustedTenant, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var version = await RevisionAsync(connection, transaction, false, ct).ConfigureAwait(false);
        var row = await connection.QuerySingleAsync<DecisionRow>(Command("""
            SELECT clock_timestamp() AS EvaluatedAt,
                (SELECT expires_at FROM consent.grants WHERE customer_id=@CustomerId AND actor_id=@ActorId AND purpose=@Purpose
                    AND @CapabilityId=ANY(capability_ids) AND owner_actor_id=@OwnerActor AND owner_version=@OwnerVersion
                    AND revoked_at IS NULL AND expires_at>clock_timestamp()) AS ExpiresAt;
            """, new
        {
            request.CustomerId,
            request.ActorId,
            request.Purpose,
            request.CapabilityId,
            OwnerActor = owner.OwnerActorId,
            OwnerVersion = owner.Version
        }, transaction, ct)).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(trustedTenant, request.ActorId, request.CustomerId, request.CapabilityId, request.Purpose,
            row.ExpiresAt is not null, row.ExpiresAt is null ? "ConsentRequired" : "Granted", version, Utc(row.EvaluatedAt),
            row.ExpiresAt is { } expiry ? Utc(expiry) : null);
    }

    public static void RequireOwner(string tenant, CustomerOwnerContext owner, string actor, string customer)
    {
        if (owner.TenantId != tenant || owner.CustomerId != customer || !owner.Active || owner.Version < 1 || owner.OwnerActorId != actor)
            throw new TenantBoundaryException("CUSTOMER_OWNER_REQUIRED", 403);
    }

    private async Task<NpgsqlConnection> OpenAsync(string tenant, CancellationToken ct)
    {
        if (!sources.TryGetValue(tenant, out var source)) throw new TenantBoundaryException("CONSENT_TENANT_UNAVAILABLE", 503);
        var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var valid = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM consent.tenant_identity WHERE singleton AND tenant_id=@Tenant AND schema_version=1)
                    AND NOT has_schema_privilege(current_user,'consent','CREATE') AND NOT has_schema_privilege(current_user,'public','CREATE')
                    AND NOT has_database_privilege(current_user,current_database(),'CREATE')
                    AND NOT has_table_privilege(current_user,'consent.tenant_identity','INSERT,UPDATE,DELETE,TRUNCATE')
                    AND NOT has_table_privilege(current_user,'consent.audit','UPDATE,DELETE,TRUNCATE')
                    AND NOT has_table_privilege(current_user,'consent.receipts','UPDATE,DELETE,TRUNCATE')
                    AND NOT has_table_privilege(current_user,'consent.outbox','UPDATE,DELETE,TRUNCATE')
                    AND NOT has_column_privilege(current_user,'consent.grants','expires_at','UPDATE')
                    AND NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication));
                """, new { Tenant = tenant }, cancellationToken: ct)).ConfigureAwait(false);
            if (!valid) throw new TenantBoundaryException("CONSENT_BINDING_INVALID", 503);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<bool> ReadyAsync(CancellationToken ct)
    {
        try { foreach (var tenant in sources.Keys) { await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false); } return true; }
        catch (Exception error) when (error is NpgsqlException or TenantBoundaryException) { return false; }
    }

    private static Task<long> RevisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, bool write, CancellationToken ct) =>
        connection.ExecuteScalarAsync<long>(Command(write ? "SELECT version FROM consent.state WHERE singleton FOR UPDATE;" :
            "SELECT version FROM consent.state WHERE singleton FOR SHARE;", new { }, transaction, ct));
    private static async Task<ConsentReceipt?> ReplayAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string id, string hash, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ReplayRow>(Command(
            "SELECT content_hash AS Hash,receipt::text AS Receipt FROM consent.receipts WHERE request_id=@Id;", new { Id = id }, transaction, ct)).ConfigureAwait(false);
        if (row is null) return null;
        if (row.Hash != hash) throw new TenantBoundaryException("CONSENT_IDEMPOTENCY_CONFLICT", 409);
        return JsonSerializer.Deserialize<ConsentReceipt>(row.Receipt, Json) ?? throw new TenantBoundaryException("CONSENT_RECEIPT_INVALID", 503);
    }
    private static async Task<ConsentReceipt> CommitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string requestId, string hash,
        string ownerActor, string customer, string actor, string id, string operation, long version, CancellationToken ct)
    {
        var evidence = "consent-" + Guid.NewGuid().ToString("N");
        var receipt = new ConsentReceipt(id, customer, actor, operation, version, evidence);
        await connection.ExecuteAsync(Command("""
            UPDATE consent.state SET version=@Version WHERE singleton;
            INSERT INTO consent.receipts(request_id,content_hash,receipt) VALUES(@RequestId,@Hash,CAST(@Receipt AS jsonb));
            INSERT INTO consent.audit(evidence_id,actor_id,customer_id,grant_id,operation,version) VALUES(@Evidence,@OwnerActor,@Customer,@Id,@Operation,@Version);
            INSERT INTO consent.outbox(event_id,payload) VALUES(@Evidence,CAST(@Receipt AS jsonb));
            """, new
        {
            Version = version,
            RequestId = requestId,
            Hash = hash,
            Receipt = JsonSerializer.Serialize(receipt, Json),
            Evidence = evidence,
            OwnerActor = ownerActor,
            Customer = customer,
            Id = id,
            Operation = operation
        }, transaction, ct)).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return receipt;
    }
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json))));
    private static CommandDefinition Command(string sql, object values, NpgsqlTransaction transaction, CancellationToken ct) => new(sql, values, transaction, cancellationToken: ct);
    public async ValueTask DisposeAsync() { foreach (var source in sources.Values) await source.DisposeAsync().ConfigureAwait(false); }
    private sealed record ReplayRow(string Hash, string Receipt);
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private sealed record DecisionRow(DateTime EvaluatedAt, DateTime? ExpiresAt);
    private sealed record GrantRow(string Id, string CustomerId, string ActorId, string Purpose, string[] CapabilityIds, DateTime ExpiresAt, DateTime? RevokedAt);
}
