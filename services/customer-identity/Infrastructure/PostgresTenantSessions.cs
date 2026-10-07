using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;
using Npgsql;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

public sealed class PostgresTenantSessions : ITenantSessionRepository, IAsyncDisposable
{
    private readonly Dictionary<string, NpgsqlDataSource> sources = new(StringComparer.Ordinal);

    public PostgresTenantSessions(IConfiguration configuration)
    {
        TenantAccessIntegration.EnsureAllowed(configuration);
        foreach (var entry in configuration.GetSection("Monergy:AccessIntegration:CustomerIdentityDatabases").GetChildren())
        {
            TenantAccessProtocol.Identifier(entry.Key, 64);
            var options = new NpgsqlConnectionStringBuilder(entry.Value ?? "")
            {
                IncludeErrorDetail = false,
                NoResetOnClose = false,
                Timeout = 5,
                CommandTimeout = 15,
                MaxPoolSize = 20,
            };
            sources.Add(entry.Key, NpgsqlDataSource.Create(options.ConnectionString));
        }
        if (sources.Count == 0) throw new InvalidOperationException("Tenant C&I bindings are required.");
    }

    public async Task<TenantSessionContext> CreateAsync(string tenantId, string actorId, string sessionId, long subjectVersion, CancellationToken cancellationToken)
    {
        TenantAccessProtocol.Identifier(actorId);
        TenantAccessProtocol.Identifier(sessionId, 160);
        if (subjectVersion < 1) throw new TenantAccessException("INVALID_SUBJECT_VERSION");
        await using var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var minimum = await connection.ExecuteScalarAsync<long>(Command("""
            SELECT COALESCE((SELECT version FROM customer_identity.access_subject_versions WHERE actor_id=@ActorId),0);
            """, new { ActorId = actorId }, transaction, cancellationToken)).ConfigureAwait(false);
        if (subjectVersion < minimum) throw new TenantAccessException("MEMBERSHIP_CHANGED");
        var expires = await connection.ExecuteScalarAsync<DateTime>(Command("""
            INSERT INTO customer_identity.trusted_sessions(session_hash,actor_id,subject_version)
            VALUES (@Hash,@ActorId,@SubjectVersion) RETURNING expires_at;
            """, new { Hash = SessionHash(sessionId), ActorId = actorId, SubjectVersion = subjectVersion }, transaction, cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(tenantId, actorId, sessionId, subjectVersion, Utc(expires));
    }

    public async Task<TenantSessionContext?> ReadCurrentAsync(string tenantId, string sessionId, CancellationToken cancellationToken)
    {
        TenantAccessProtocol.Identifier(sessionId, 160);
        await using var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition("""
            SELECT s.actor_id AS ActorId,s.subject_version AS SubjectVersion,s.expires_at AS ExpiresAt
            FROM customer_identity.trusted_sessions s
            LEFT JOIN customer_identity.access_subject_versions v ON v.actor_id=s.actor_id
            WHERE s.session_hash=@Hash AND NOT s.revoked AND s.expires_at>clock_timestamp()
                AND s.subject_version>=COALESCE(v.version,0);
            """, new { Hash = SessionHash(sessionId) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : new(tenantId, row.ActorId, sessionId, row.SubjectVersion, Utc(row.ExpiresAt));
    }

    public async Task RevokeAsync(string tenantId, string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE customer_identity.trusted_sessions SET revoked=true WHERE session_hash=@Hash;
            """, new { Hash = SessionHash(sessionId) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<AccessManagementReceipt> ConsumeAsync(string trustedTenant, string destination, AccessManagementEvent message, CancellationToken cancellationToken)
    {
        if (destination is not ("authorization" or "sessions")) throw new TenantAccessException("INVALID_DESTINATION");
        TenantAccessProtocol.Validate(message, trustedTenant, destination);
        var hash = TenantAccessProtocol.Hash(message);
        await using var connection = await OpenAsync(trustedTenant, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var prior = await connection.QuerySingleOrDefaultAsync<string>(Command("""
            SELECT receipt::text FROM customer_identity.access_event_inbox WHERE destination=@Destination AND event_id=@EventId;
            """, new { Destination = destination, message.EventId }, transaction, cancellationToken)).ConfigureAwait(false);
        if (prior is not null)
        {
            var receipt = JsonSerializer.Deserialize<AccessManagementReceipt>(prior, TenantAccessProtocol.Json)!;
            if (receipt.EventHash != hash) throw new TenantAccessException("EVENT_ID_CONTENT_CONFLICT");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return receipt with { Disposition = "Duplicate" };
        }
        await connection.ExecuteAsync(Command("""
            UPDATE customer_identity.access_policy_version SET version=GREATEST(version,@PolicyVersion) WHERE singleton;
            """, message, transaction, cancellationToken)).ConfigureAwait(false);
        if (message.SubjectActorId is not null)
        {
            await connection.ExecuteAsync(Command("""
                INSERT INTO customer_identity.access_subject_versions(actor_id,version) VALUES (@SubjectActorId,@SubjectVersion)
                ON CONFLICT(actor_id) DO UPDATE SET version=GREATEST(customer_identity.access_subject_versions.version,EXCLUDED.version);
                """, message, transaction, cancellationToken)).ConfigureAwait(false);
            if (destination == "sessions")
                await connection.ExecuteAsync(Command("""
                    UPDATE customer_identity.trusted_sessions SET revoked=true WHERE actor_id=@SubjectActorId AND subject_version<@SubjectVersion;
                    """, message, transaction, cancellationToken)).ConfigureAwait(false);
        }
        var applied = new AccessManagementReceipt(destination, trustedTenant, message.EventId, message.ContractId, message.EventVersion,
            hash, "ci-" + destination + "-" + message.EventId.ToString("N"), "Applied");
        var parameters = new
        {
            Destination = destination,
            message.EventId,
            Hash = hash,
            Receipt = JsonSerializer.Serialize(applied, TenantAccessProtocol.Json),
        };
        await connection.ExecuteAsync(Command("""
            INSERT INTO customer_identity.access_event_inbox(destination,event_id,event_hash,receipt)
            VALUES (@Destination,@EventId,@Hash,CAST(@Receipt AS jsonb));
            """, parameters, transaction, cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return applied;
    }

    public async Task<bool> ReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var tenant in sources.Keys)
            {
                await using var connection = await OpenAsync(tenant, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
        catch (Exception exception) when (exception is NpgsqlException or TenantAccessException) { return false; }
    }

    internal async Task<NpgsqlConnection> OpenAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (!sources.TryGetValue(tenantId, out var source)) throw new TenantAccessException("TENANT_UNAVAILABLE", 503);
        var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var valid = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM customer_identity.tenant_identity WHERE singleton AND tenant_id=@TenantId AND schema_version=1)
                    AND NOT has_schema_privilege(current_user,'customer_identity','CREATE')
                    AND NOT has_schema_privilege(current_user,'public','CREATE')
                    AND NOT has_database_privilege(current_user,current_database(),'CREATE')
                    AND NOT has_table_privilege(current_user,'customer_identity.tenant_identity','INSERT,UPDATE,DELETE,TRUNCATE')
                    AND NOT has_table_privilege(current_user,'customer_identity.access_event_inbox','UPDATE,DELETE,TRUNCATE')
                    AND NOT has_column_privilege(current_user,'customer_identity.trusted_sessions','subject_version','UPDATE')
                    AND NOT has_column_privilege(current_user,'customer_identity.trusted_sessions','expires_at','UPDATE')
                    AND NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication));
                """, new { TenantId = tenantId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (!valid) throw new TenantAccessException("TENANT_BINDING_INVALID", 503);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string SessionHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static Task<int> LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command("SELECT pg_advisory_xact_lock(705001);", new { }, transaction, cancellationToken));
    private static CommandDefinition Command(string sql, object parameters, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        new(sql, parameters, transaction, cancellationToken: cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var source in sources.Values) await source.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class SessionRow
    {
        public string ActorId { get; set; } = "";
        public long SubjectVersion { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
