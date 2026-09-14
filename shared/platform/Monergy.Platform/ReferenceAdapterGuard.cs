using Microsoft.Extensions.Configuration;

namespace Monergy.Platform;

public static class ReferenceAdapterGuard
{
    private static readonly HashSet<string> AllowedZones =
        new(StringComparer.OrdinalIgnoreCase) { "LOCAL", "CI_EPHEMERAL", "CI/EPHEMERAL" };

    public static bool IsSelected(IConfiguration configuration) =>
        configuration.GetValue<bool>("Monergy:ReferenceAdapters");

    public static void EnsureAllowed(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var zone = configuration["Monergy:ExecutionZone"] ?? string.Empty;
        if (!AllowedZones.Contains(zone))
        {
            throw new InvalidOperationException(
                "Reference adapters may run only in LOCAL or CI_EPHEMERAL and cannot be activated in a persistent environment.");
        }
    }
}
