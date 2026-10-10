using Monergy.Platform.ControlPlane;

namespace Monergy.DatabaseMigrator;

internal static partial class LocalUatBootstrap
{
    public static async Task<int> VerifyStorageAsync(string folder)
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Development")
        {
            Console.Error.WriteLine("Local UAT storage verification requires Development.");
            return 1;
        }
        try
        {
            await ProbeOnboardingStorageAsync(folder).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Local UAT storage verification failed: {error.GetType().Name}. The configured container user requires write access to the onboarding volume. Retain existing profiles and data.");
            return 1;
        }
    }

    private static async Task ProbeOnboardingStorageAsync(string folder)
    {
        // Exercise the real registry operations in uniquely named temporary files.
        // Never change permissions, reset a volume, or touch registry.json/setup.json.
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException();
        if (!OperatingSystem.IsWindows())
            Console.WriteLine($"Local UAT onboarding storage mode: {Convert.ToString((int)File.GetUnixFileMode(folder), 8)}.");
        var path = Path.Combine(folder, ".storage-probe-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var registry = new FileTenantRegistry(path))
            {
                var record = await registry.CreateOrGetAsync(new("storage-probe", "Local storage check", "Local storage check",
                    "US", "UTC", "LOCAL", "probe@example.test", TenantPlacementMode.Shared), TimeProvider.System, default).ConfigureAwait(false);
                await registry.ReplaceAsync(record with { State = TenantLifecycleState.Provisioning, UpdatedAt = DateTimeOffset.UtcNow },
                    record.ConfigurationVersion, default).ConfigureAwait(false);
                using var reopened = new FileTenantRegistry(path);
                var saved = await reopened.GetAsync(record.TenantId, default).ConfigureAwait(false);
                if (saved?.State != TenantLifecycleState.Provisioning || saved.ConfigurationVersion != record.ConfigurationVersion + 1)
                    throw new InvalidOperationException("LOCAL_STORAGE_READBACK_FAILED");
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".lock")) File.Delete(path + ".lock");
        }
        Console.WriteLine("Local UAT onboarding storage: create, replace, readback and cleanup passed.");
    }
}
