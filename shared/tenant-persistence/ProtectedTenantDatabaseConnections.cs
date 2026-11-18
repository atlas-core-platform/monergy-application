using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using Npgsql;

namespace Monergy.Platform;

/// <summary>
/// Resolves a tenant database from static operator configuration first and,
/// only in LOCAL Development, from the protected provisioning owner. The
/// browser and business APIs never receive the resolved connection string.
/// </summary>
public sealed class ProtectedTenantDatabaseConnections : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> sources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> tenantGates = new(StringComparer.Ordinal);
    private readonly HttpClient client;
    private readonly Uri? owner;
    private readonly string? token;
    private readonly string service;

    public ProtectedTenantDatabaseConnections(
        IConfiguration configuration,
        string service,
        string staticBindingsSection)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Identifier(service, 64);
        this.service = service;
        foreach (var binding in configuration.GetSection(staticBindingsSection).GetChildren())
        {
            Identifier(binding.Key, 64);
            if (string.IsNullOrWhiteSpace(binding.Value))
                throw new InvalidOperationException("TENANT_BINDING_INVALID");
            sources.TryAdd(binding.Key, Create(binding.Value));
        }

        var section = configuration.GetSection("Monergy:DynamicTenantBindings");
        if (section.GetValue<bool>("Enabled"))
        {
            if (configuration["Monergy:ExecutionZone"] is not ("LOCAL" or "CI_EPHEMERAL"))
                throw new InvalidOperationException("DYNAMIC_TENANT_BINDINGS_LOCAL_ONLY");
            var endpoint = section["Url"];
            token = section["Tokens:" + service];
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out owner) ||
                owner.Scheme != Uri.UriSchemeHttp || owner.Host is not ("127.0.0.1" or "localhost") ||
                string.IsNullOrWhiteSpace(token) || token.Length > 256)
                throw new InvalidOperationException("DYNAMIC_TENANT_BINDING_CONFIGURATION_INVALID");
        }
        client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        if (sources.IsEmpty && owner is null)
            throw new InvalidOperationException("Tenant database bindings are required.");
    }

    public IReadOnlyCollection<string> ConfiguredTenants => sources.Keys.ToArray();

    public async ValueTask<NpgsqlConnection> OpenAsync(string tenantId, CancellationToken ct)
    {
        Identifier(tenantId, 64);
        var source = sources.GetValueOrDefault(tenantId) ?? await ResolveAsync(tenantId, ct).ConfigureAwait(false);
        return await source.OpenConnectionAsync(ct).ConfigureAwait(false);
    }

    private async Task<NpgsqlDataSource> ResolveAsync(string tenantId, CancellationToken ct)
    {
        if (owner is null || token is null)
            throw new TenantBoundaryException("TENANT_UNAVAILABLE", 503);
        var gate = tenantGates.GetOrAdd(tenantId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (sources.TryGetValue(tenantId, out var existing)) return existing;
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(owner, "internal/local/v1/tenant-bindings/" + Uri.EscapeDataString(service) + "/" +
                    Uri.EscapeDataString(tenantId)));
            request.Headers.Add("X-Monergy-Tenant-Binding", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                throw new TenantBoundaryException("TENANT_UNAVAILABLE", 503);
            if (!response.IsSuccessStatusCode)
                throw new TenantBoundaryException("TENANT_BINDING_OWNER_UNAVAILABLE", 503);
            var binding = await response.Content.ReadFromJsonAsync<TenantDatabaseBinding>(cancellationToken: ct)
                .ConfigureAwait(false) ?? throw new TenantBoundaryException("TENANT_BINDING_INVALID", 503);
            if (binding.TenantId != tenantId || binding.Service != service || binding.Version < 1 ||
                string.IsNullOrWhiteSpace(binding.ConnectionString))
                throw new TenantBoundaryException("TENANT_BINDING_INVALID", 503);
            var created = Create(binding.ConnectionString);
            if (!sources.TryAdd(tenantId, created))
            {
                await created.DisposeAsync().ConfigureAwait(false);
                return sources[tenantId];
            }
            return created;
        }
        catch (TenantBoundaryException) { throw; }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { throw new TenantBoundaryException("TENANT_BINDING_OWNER_UNAVAILABLE", 503); }
        finally { gate.Release(); }
    }

    private static NpgsqlDataSource Create(string connectionString)
    {
        var options = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 20,
            Timeout = 5,
            CommandTimeout = 15,
            IncludeErrorDetail = false,
            NoResetOnClose = false,
        };
        if (string.IsNullOrWhiteSpace(options.Database) || string.IsNullOrWhiteSpace(options.Username) ||
            string.IsNullOrWhiteSpace(options.Password))
            throw new InvalidOperationException("TENANT_BINDING_INVALID");
        return NpgsqlDataSource.Create(options.ConnectionString);
    }

    private static void Identifier(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
            throw new InvalidOperationException("TENANT_BINDING_IDENTIFIER_INVALID");
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        foreach (var source in sources.Values) await source.DisposeAsync().ConfigureAwait(false);
        foreach (var gate in tenantGates.Values) gate.Dispose();
    }

    private sealed record TenantDatabaseBinding(string TenantId, string Service, string ConnectionString, long Version);
}
