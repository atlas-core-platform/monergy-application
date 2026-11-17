using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Monergy.Platform;

public sealed record GovernedProviderRuntimeConfiguration(
    string Code, string Scope, string? TenantId, bool Enabled, long Revision,
    string? Endpoint, string? ClientId, string? CallbackUri, string? Model,
    IReadOnlyDictionary<string, string> Options, string Secret);

public interface IGovernedProviderConfigurationReader
{
    Task<GovernedProviderRuntimeConfiguration?> ReadAsync(string provider, string tenant, CancellationToken ct);
}

internal sealed class LegacyProviderConfigurationReader : IGovernedProviderConfigurationReader
{
    public Task<GovernedProviderRuntimeConfiguration?> ReadAsync(string provider, string tenant, CancellationToken ct) =>
        Task.FromResult<GovernedProviderRuntimeConfiguration?>(null);
}

public sealed class GovernedProviderConfigurationClient(HttpClient client, string token)
    : IGovernedProviderConfigurationReader, IDisposable
{
    public async Task<GovernedProviderRuntimeConfiguration?> ReadAsync(string provider, string tenant, CancellationToken ct)
    {
        if (!Identifier(provider, 80) || !Identifier(tenant, 64))
            throw new InvalidOperationException("GOVERNED_PROVIDER_REQUEST_INVALID");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "internal/local/v1/governed-integrations/" + Uri.EscapeDataString(provider) + "?tenant=" + Uri.EscapeDataString(tenant));
        request.Headers.Add("X-Monergy-Provider-Configuration", token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("GOVERNED_PROVIDER_CONFIGURATION_UNAVAILABLE");
        if (response.Content.Headers.ContentLength is > 16_384)
            throw new InvalidOperationException("GOVERNED_PROVIDER_CONFIGURATION_INVALID");
        return await response.Content.ReadFromJsonAsync<GovernedProviderRuntimeConfiguration>(cancellationToken: ct)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("GOVERNED_PROVIDER_CONFIGURATION_INVALID");
    }

    private static bool Identifier(string value, int maximum) => value.Length is > 0 && value.Length <= maximum &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');
    public void Dispose() => client.Dispose();
}

public static class GovernedProviderConfigurationRegistration
{
    public static bool AddGovernedProviderConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Monergy:GovernedProviders");
        if (section["Enabled"] != "true")
        {
            services.AddSingleton<IGovernedProviderConfigurationReader, LegacyProviderConfigurationReader>();
            return false;
        }
        var url = section["Url"];
        var token = section["Token"];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var root) || !root.IsLoopback || root.Scheme != Uri.UriSchemeHttp ||
            string.IsNullOrWhiteSpace(token) || token.Length < 32)
            throw new InvalidOperationException("Governed provider configuration is invalid.");
        services.AddSingleton<IGovernedProviderConfigurationReader>(_ => new GovernedProviderConfigurationClient(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            { BaseAddress = root, Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 16_384 }, token));
        return true;
    }
}
