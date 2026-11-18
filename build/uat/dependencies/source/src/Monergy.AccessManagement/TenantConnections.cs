using Dapper;
using Npgsql;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

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
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> sources;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    private readonly DynamicTenantBindingRoute? dynamicRoute;
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(10) };

    public ReferenceTenantConnectionFactory(IReadOnlyDictionary<string, string> bindings,
        DynamicTenantBindingRoute? dynamicRoute = null)
    {
        sources = new ConcurrentDictionary<string, NpgsqlDataSource>(StringComparer.Ordinal);
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
            if (!sources.TryAdd(binding.Key, NpgsqlDataSource.Create(options.ConnectionString)))
                throw new InvalidOperationException("Duplicate tenant binding.");
        }
        this.dynamicRoute = dynamicRoute;
    }

    public bool IsConfigured => !sources.IsEmpty;

    public async ValueTask<NpgsqlConnection> OpenAsync(string tenantId, CancellationToken ct)
    {
        if (!sources.TryGetValue(tenantId, out var source))
            source = await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        NpgsqlConnection? connection = null;
        try
        {
            connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
            var matches = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT EXISTS (SELECT 1 FROM am.tenant_identity WHERE singleton AND tenant_id=@TenantId
                    AND initial_admin_actor_id IS NOT NULL AND schema_version=8)
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
        client.Dispose();
        foreach (var source in sources.Values)
            await source.DisposeAsync().ConfigureAwait(false);
        foreach (var gate in gates.Values) gate.Dispose();
    }

    private async Task<NpgsqlDataSource> ResolveAsync(string tenantId, CancellationToken ct)
    {
        if (dynamicRoute is null) throw new AccessStoreUnavailableException();
        var gate = gates.GetOrAdd(tenantId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (sources.TryGetValue(tenantId, out var existing)) return existing;
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(dynamicRoute.Endpoint,
                "internal/local/v1/tenant-bindings/access-management/" + Uri.EscapeDataString(tenantId)));
            request.Headers.Add("X-Monergy-Tenant-Binding", dynamicRoute.Token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new AccessStoreUnavailableException();
            var binding = await response.Content.ReadFromJsonAsync<TenantDatabaseBinding>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (binding is null || binding.TenantId != tenantId || binding.Service != "access-management" ||
                binding.Version < 1 || string.IsNullOrWhiteSpace(binding.ConnectionString))
                throw new AccessStoreUnavailableException();
            var options = new NpgsqlConnectionStringBuilder(binding.ConnectionString)
            {
                Pooling = true, MinPoolSize = 0, MaxPoolSize = 20, Timeout = 5, CommandTimeout = 15,
                IncludeErrorDetail = false, NoResetOnClose = false,
            };
            var created = NpgsqlDataSource.Create(options.ConnectionString);
            if (!sources.TryAdd(tenantId, created))
            {
                await created.DisposeAsync().ConfigureAwait(false);
                return sources[tenantId];
            }
            return created;
        }
        catch (AccessStoreUnavailableException) { throw; }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { throw new AccessStoreUnavailableException(failure); }
        finally { gate.Release(); }
    }

    private sealed record TenantDatabaseBinding(string TenantId, string Service, string ConnectionString, long Version);
}

public sealed record DynamicTenantBindingRoute(Uri Endpoint, string Token);

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
        var known = (tenantId is "T001" or "T002") &&
            (actorId is "A100" or "A200" or "A300" or "A400" or "A500" or "A600" or
                "A700" or "A800" or "A850" or "A900" or "A901");
        return ValueTask.FromResult(known
            ? new VerifiedTenantIdentity(tenantId, actorId, actorId.ToLowerInvariant() + "@example.test")
            : null);
    }
}

public sealed class HttpVerifiedTenantIdentityDirectory : IVerifiedTenantIdentityDirectory, IDisposable
{
    private readonly HttpClient client;
    private readonly ReferenceDeliveryRoute route;

    public HttpVerifiedTenantIdentityDirectory(HttpClient client, ReferenceDeliveryRoute route)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.route = route ?? throw new ArgumentNullException(nameof(route));
        ReferenceOwnerBoundary.Validate(route);
    }

    public async ValueTask<VerifiedTenantIdentity?> FindAsync(string tenantId, string actorId, CancellationToken ct)
    {
        AccessInput.Identifier(tenantId, 64);
        AccessInput.Identifier(actorId);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(route.Endpoint,
                "internal/v1/tenant-identities/" + Uri.EscapeDataString(actorId)));
            request.Headers.Add("X-Monergy-Tenant", tenantId);
            request.Headers.Add("X-Monergy-Owner-Token", route.Token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw new AccessStoreUnavailableException();
            var identity = await response.Content.ReadFromJsonAsync<VerifiedTenantIdentity>(cancellationToken: ct)
                .ConfigureAwait(false);
            return identity is { TenantId: var tenant, ActorId: var actor } && tenant == tenantId && actor == actorId
                ? identity : throw new AccessStoreUnavailableException();
        }
        catch (AccessStoreUnavailableException) { throw; }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { throw new AccessStoreUnavailableException(failure); }
    }

    public void Dispose() => client.Dispose();
}
