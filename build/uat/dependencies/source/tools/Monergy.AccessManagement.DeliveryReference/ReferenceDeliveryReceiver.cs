using System.Text.Json;
using Dapper;
using Monergy.AccessManagement;
using Npgsql;

namespace Monergy.AccessManagement.DeliveryReference;

public sealed record ReferenceSessionSeed(string ActorId, string AuthenticationContextId, long SubjectVersion);
public sealed record ReferenceSessionState(string ActorId, string AuthenticationContextId, long SubjectVersion, bool Revoked);
public sealed record ReferenceReceiverState(long InboxCount, long AuditCount, long PolicyVersion, IReadOnlyList<ReferenceSessionState> Sessions);

// Verification-only owner stand-ins in an independent database. No Monergy
// customer/session/Audit authority is read, changed, or claimed by this fixture.
public sealed class ReferenceDeliveryReceiver : IAsyncDisposable
{
    private readonly NpgsqlDataSource source;

    public ReferenceDeliveryReceiver(string connectionString)
    {
        var options = new NpgsqlConnectionStringBuilder(connectionString)
        {
            IncludeErrorDetail = false,
            NoResetOnClose = false,
            CommandTimeout = 15,
            Timeout = 5,
            MaxPoolSize = 20
        };
        source = NpgsqlDataSource.Create(options.ConnectionString);
    }

    public async Task<AccessEventReceipt> ConsumeAsync(string trustedTenant, string destination, AccessEventEnvelope message, CancellationToken ct)
    {
        RequireTenant(trustedTenant);
        AccessEventProtocol.Validate(message, trustedTenant, destination);
        var hash = AccessEventProtocol.Hash(message);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(connection, transaction, trustedTenant, destination, ct).ConfigureAwait(false);
        var prior = await connection.QuerySingleOrDefaultAsync<string>(Command("""
            SELECT receipt::text FROM delivery_reference.inbox
            WHERE destination=@Destination AND tenant_id=@TenantId AND event_id=@EventId;
            """, new { Destination = destination, message.TenantId, message.EventId }, transaction, ct)).ConfigureAwait(false);
        if (prior is not null)
        {
            var receipt = JsonSerializer.Deserialize<AccessEventReceipt>(prior, AccessEventProtocol.Json)!;
            if (receipt.EventHash != hash) throw new EventDeliveryException("EVENT_ID_CONTENT_CONFLICT", true);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return receipt with { Disposition = "Duplicate" };
        }
        if (destination == "authorization")
        {
            await connection.ExecuteAsync(Command("""
                INSERT INTO delivery_reference.policy_versions(tenant_id,version) VALUES (@TenantId,@PolicyVersion)
                ON CONFLICT(tenant_id) DO UPDATE SET version=GREATEST(delivery_reference.policy_versions.version,EXCLUDED.version);
                """, message, transaction, ct)).ConfigureAwait(false);
            if (message.SubjectActorId is not null)
                await connection.ExecuteAsync(Command("""
                    INSERT INTO delivery_reference.subject_versions(tenant_id,actor_id,version) VALUES (@TenantId,@SubjectActorId,@SubjectVersion)
                    ON CONFLICT(tenant_id,actor_id) DO UPDATE SET version=GREATEST(delivery_reference.subject_versions.version,EXCLUDED.version);
                    """, message, transaction, ct)).ConfigureAwait(false);
        }
        else if (destination == "sessions")
        {
            await connection.ExecuteAsync(Command("""
                INSERT INTO delivery_reference.session_versions(tenant_id,actor_id,version) VALUES (@TenantId,@SubjectActorId,@SubjectVersion)
                ON CONFLICT(tenant_id,actor_id) DO UPDATE SET version=GREATEST(delivery_reference.session_versions.version,EXCLUDED.version);
                UPDATE delivery_reference.sessions SET revoked=true
                WHERE tenant_id=@TenantId AND actor_id=@SubjectActorId AND subject_version<@SubjectVersion;
                """, message, transaction, ct)).ConfigureAwait(false);
        }
        else
        {
            await connection.ExecuteAsync(Command("""
                INSERT INTO delivery_reference.audit_observations(tenant_id,event_id,event_hash,envelope)
                VALUES (@TenantId,@EventId,@Hash,CAST(@Envelope AS jsonb));
                """, new { message.TenantId, message.EventId, Hash = hash, Envelope = JsonSerializer.Serialize(message, AccessEventProtocol.Json) },
                transaction, ct)).ConfigureAwait(false);
        }
        var applied = new AccessEventReceipt(destination, message.TenantId, message.EventId, message.ContractId, message.EventVersion,
            hash, "reference-" + destination + "-" + message.EventId.ToString("N"), "Applied");
        await connection.ExecuteAsync(Command("""
            INSERT INTO delivery_reference.inbox(destination,tenant_id,event_id,event_hash,receipt)
            VALUES (@Destination,@TenantId,@EventId,@Hash,CAST(@Receipt AS jsonb));
            """, new
        {
            Destination = destination,
            message.TenantId,
            message.EventId,
            Hash = hash,
            Receipt = JsonSerializer.Serialize(applied, AccessEventProtocol.Json)
        }, transaction, ct)).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return applied;
    }

    public async Task SeedSessionAsync(string tenantId, ReferenceSessionSeed seed, CancellationToken ct)
    {
        RequireTenant(tenantId);
        AccessInput.Identifier(seed.ActorId);
        AccessInput.Identifier(seed.AuthenticationContextId);
        AccessInput.Require(seed.SubjectVersion > 0, "INVALID_SUBJECT_VERSION");
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(connection, transaction, tenantId, "sessions", ct).ConfigureAwait(false);
        var minimum = await connection.ExecuteScalarAsync<long>(Command("""
            SELECT COALESCE((SELECT version FROM delivery_reference.session_versions WHERE tenant_id=@TenantId AND actor_id=@ActorId),0);
            """, new { TenantId = tenantId, seed.ActorId }, transaction, ct)).ConfigureAwait(false);
        AccessInput.Require(seed.SubjectVersion >= minimum, "STALE_SESSION_REFERENCE", 409);
        var inserted = await connection.ExecuteAsync(Command("""
            INSERT INTO delivery_reference.sessions(tenant_id,authentication_context_id,actor_id,subject_version)
            VALUES (@TenantId,@AuthenticationContextId,@ActorId,@SubjectVersion) ON CONFLICT DO NOTHING;
            """, new { TenantId = tenantId, seed.AuthenticationContextId, seed.ActorId, seed.SubjectVersion }, transaction, ct)).ConfigureAwait(false);
        AccessInput.Require(inserted == 1, "SESSION_REFERENCE_ALREADY_EXISTS", 409);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<ReferenceReceiverState> StateAsync(string tenantId, CancellationToken ct)
    {
        RequireTenant(tenantId);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var parameters = new { TenantId = tenantId };
        var inbox = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM delivery_reference.inbox WHERE tenant_id=@TenantId;", parameters, cancellationToken: ct)).ConfigureAwait(false);
        var audit = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM delivery_reference.audit_observations WHERE tenant_id=@TenantId;", parameters, cancellationToken: ct)).ConfigureAwait(false);
        var version = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE((SELECT version FROM delivery_reference.policy_versions WHERE tenant_id=@TenantId),0);", parameters, cancellationToken: ct)).ConfigureAwait(false);
        var sessions = (await connection.QueryAsync<ReferenceSessionState>(new CommandDefinition("""
            SELECT actor_id AS ActorId,authentication_context_id AS AuthenticationContextId,subject_version AS SubjectVersion,revoked AS Revoked
            FROM delivery_reference.sessions WHERE tenant_id=@TenantId ORDER BY authentication_context_id LIMIT 100;
            """, parameters, cancellationToken: ct)).ConfigureAwait(false)).ToArray();
        return new(inbox, audit, version, sessions);
    }

    public async Task<bool> ReadyAsync(CancellationToken ct)
    {
        try { await using var connection = await OpenAsync(ct).ConfigureAwait(false); return true; }
        catch (NpgsqlException) { return false; }
        catch (AccessStoreUnavailableException) { return false; }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var valid = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS (SELECT 1 FROM delivery_reference.identity WHERE singleton AND kind='AM04_DELIVERY_FIXTURE')
                    AND NOT has_schema_privilege(current_user,'delivery_reference','CREATE')
                    AND NOT has_schema_privilege(current_user,'public','CREATE')
                    AND NOT has_table_privilege(current_user,'delivery_reference.identity','INSERT,UPDATE,DELETE')
                    AND NOT has_table_privilege(current_user,'delivery_reference.inbox','UPDATE,DELETE')
                    AND NOT has_table_privilege(current_user,'delivery_reference.audit_observations','UPDATE,DELETE')
                    AND NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=current_user AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication));
                """, cancellationToken: ct)).ConfigureAwait(false);
            if (!valid) throw new AccessStoreUnavailableException();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static Task<int> LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId, string destination, CancellationToken ct) =>
        connection.ExecuteAsync(Command("SELECT pg_advisory_xact_lock(hashtextextended(@Key,0));",
            new { Key = JsonSerializer.Serialize(new[] { tenantId, destination }) }, transaction, ct));
    private static CommandDefinition Command(string sql, object parameters, NpgsqlTransaction transaction, CancellationToken ct) =>
        new(sql, parameters, transaction, cancellationToken: ct);
    private static void RequireTenant(string tenantId) => AccessInput.Require(tenantId is "T001" or "T002", "UNKNOWN_REFERENCE_TENANT", 403);
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
