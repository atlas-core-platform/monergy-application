using Dapper;
using Npgsql;

namespace Monergy.AccessManagement;

public sealed class AccessStoreUnavailableException : Exception
{
    public AccessStoreUnavailableException() : base("The tenant access store is unavailable.") { }
    public AccessStoreUnavailableException(Exception inner) : base("The tenant access store is unavailable.", inner) { }
}

public interface ITenantConnectionFactory
{
    bool IsConfigured { get; }
    ValueTask<NpgsqlConnection> OpenAsync(string tenantId, CancellationToken ct);
    ValueTask<bool> CheckReadyAsync(CancellationToken ct);
}

// Only the Development composition root creates this adapter. Tenant/database
// bindings are operator configuration, never values supplied by an HTTP caller.
public sealed class ReferenceTenantConnectionFactory : ITenantConnectionFactory, IAsyncDisposable
{
    private readonly Dictionary<string, NpgsqlDataSource> sources;

    public ReferenceTenantConnectionFactory(IReadOnlyDictionary<string, string> bindings)
    {
        sources = new Dictionary<string, NpgsqlDataSource>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            AccessInput.Identifier(binding.Key, 64);
            var options = new NpgsqlConnectionStringBuilder(binding.Value)
            {
                Pooling = true,
                MinPoolSize = 0,
                MaxPoolSize = 20,
                Timeout = 5,
                CommandTimeout = 15,
                IncludeErrorDetail = false,
                NoResetOnClose = false
            };
            sources.Add(binding.Key, NpgsqlDataSource.Create(options.ConnectionString));
        }
    }

    public bool IsConfigured => sources.Count > 0;

    public async ValueTask<NpgsqlConnection> OpenAsync(string tenantId, CancellationToken ct)
    {
        if (!sources.TryGetValue(tenantId, out var source))
            throw new AccessStoreUnavailableException();
        NpgsqlConnection? connection = null;
        try
        {
            connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
            var matches = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT EXISTS (SELECT 1 FROM am.tenant_identity WHERE singleton AND tenant_id=@TenantId
                    AND initial_admin_actor_id IS NOT NULL AND schema_version=6)
                    AND NOT has_schema_privilege(current_user,'am','CREATE')
                    AND NOT has_schema_privilege(current_user,'public','CREATE')
                    AND NOT has_database_privilege(current_user,current_database(),'CREATE')
                    AND NOT has_table_privilege(current_user,'am.tenant_identity','INSERT,UPDATE,DELETE')
                    AND NOT has_table_privilege(current_user,'am.policy_state','DELETE')
                    AND NOT has_table_privilege(current_user,'am.outbox','UPDATE,DELETE')
                    AND NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=current_user
                        AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication));
                """,
                new { TenantId = tenantId }, cancellationToken: ct)).ConfigureAwait(false);
            if (!matches) throw new AccessStoreUnavailableException();
            return connection;
        }
        catch (Exception exception)
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            if (exception is NpgsqlException)
                throw new AccessStoreUnavailableException(exception);
            throw;
        }
    }

    public async ValueTask<bool> CheckReadyAsync(CancellationToken ct)
    {
        if (!IsConfigured) return false;
        try
        {
            foreach (var tenantId in sources.Keys)
            {
                await using var connection = await OpenAsync(tenantId, ct).ConfigureAwait(false);
                _ = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT policy_version FROM am.policy_state WHERE singleton;",
                    cancellationToken: ct)).ConfigureAwait(false);
            }
            return true;
        }
        catch (AccessStoreUnavailableException) { return false; }
        catch (NpgsqlException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in sources.Values)
            await source.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed record VerifiedTenantIdentity(string TenantId, string ActorId, string NormalizedEmail);

public interface IVerifiedTenantIdentityDirectory
{
    ValueTask<VerifiedTenantIdentity?> FindAsync(string tenantId, string actorId, CancellationToken ct);
}

public sealed class ReferenceTenantIdentityDirectory : IVerifiedTenantIdentityDirectory
{
    public ValueTask<VerifiedTenantIdentity?> FindAsync(string tenantId, string actorId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var known = (tenantId is "T001" or "T002") && (actorId is "A100" or "A200" or "A300" or "A900" or "A901");
        return ValueTask.FromResult(known
            ? new VerifiedTenantIdentity(tenantId, actorId, actorId.ToLowerInvariant() + "@example.test")
            : null);
    }
}
