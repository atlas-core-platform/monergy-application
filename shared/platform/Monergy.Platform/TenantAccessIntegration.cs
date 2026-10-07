using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Monergy.Platform;

public static class TenantAccessIntegration
{
    public static bool IsSelected(IConfiguration configuration) => configuration["Monergy:AccessIntegration:Enabled"] == "true";

    public static void EnsureAllowed(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!IsSelected(configuration) || configuration["Monergy:ExecutionZone"] is not ("LOCAL" or "CI_EPHEMERAL") ||
            ReferenceAdapterGuard.IsSelected(configuration) || !string.IsNullOrEmpty(configuration["Monergy:Persistence:Provider"]))
            throw new InvalidOperationException("Tenant owner integration requires an exclusive LOCAL/CI composition.");
    }

    public static string Required(IConfiguration configuration, string key) =>
        configuration[key] ?? throw new InvalidOperationException("Required tenant integration configuration is missing.");

    public static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 256 || token.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Invalid tenant integration workload token.");
    }

    public static bool TokenMatches(string actual, string expected) =>
        actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));

    public static Uri LoopbackRoot(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback || endpoint.Scheme != Uri.UriSchemeHttp ||
            endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Tenant integration requires a loopback HTTP root.");
        return endpoint;
    }
}

