using Microsoft.Extensions.Configuration;

namespace Monergy.Platform;

public static class PhysicalPersistenceGuard
{
    public const string Provider = "POSTGRESQL_S3";

    private static readonly HashSet<string> AllowedZones =
        new(StringComparer.OrdinalIgnoreCase) { "LOCAL", "CI_EPHEMERAL", "CI/EPHEMERAL" };

    public static bool IsSelected(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var selected = configuration["Monergy:Persistence:Provider"];
        if (string.IsNullOrWhiteSpace(selected)) return false;
        if (!string.Equals(selected, Provider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unknown persistence provider '{selected}'.");
        }

        EnsureAllowed(configuration);
        if (ReferenceAdapterGuard.IsSelected(configuration))
        {
            throw new InvalidOperationException("Physical and reference persistence adapters cannot be selected together.");
        }

        return true;
    }

    public static void EnsureAllowed(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var zone = configuration["Monergy:ExecutionZone"] ?? string.Empty;
        if (!AllowedZones.Contains(zone))
        {
            throw new InvalidOperationException(
                "D09 physical persistence may run only in LOCAL or CI_EPHEMERAL.");
        }
    }

    public static string Require(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required physical persistence configuration '{key}' is unavailable.")
            : value;
    }
}
