using System.Text.Json;

namespace Monergy.Platform.ControlPlane;

public interface ITenantRegistry
{
    Task<TenantRegistryRecord> CreateOrGetAsync(
        TenantOnboardingRequest request,
        TimeProvider clock,
        CancellationToken cancellationToken);

    Task<TenantRegistryRecord?> GetAsync(string tenantId, CancellationToken cancellationToken);

    Task<TenantRegistryRecord> ReplaceAsync(
        TenantRegistryRecord record,
        long expectedConfigurationVersion,
        CancellationToken cancellationToken);
}

public sealed class FileTenantRegistry : ITenantRegistry
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);

    public FileTenantRegistry(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Registry path is required.", nameof(path));
        this.path = Path.GetFullPath(path);
    }

    public async Task<TenantRegistryRecord> CreateOrGetAsync(
        TenantOnboardingRequest request,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        Validate(request);
        ArgumentNullException.ThrowIfNull(clock);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var existing = state.Tenants.SingleOrDefault(item => item.RequestId == request.RequestId);
            if (existing is not null)
            {
                if (!SemanticallyMatches(existing, request))
                    throw new InvalidOperationException("TENANT_ONBOARDING_IDEMPOTENCY_CONFLICT");
                return existing;
            }

            var now = clock.GetUtcNow();
            var tenant = new TenantRegistryRecord(
                "tenant-" + Guid.NewGuid().ToString("N"),
                request.RequestId,
                request.OrganizationName.Trim(),
                request.DisplayName.Trim(),
                request.CountryCode.ToUpperInvariant(),
                request.TimeZone,
                request.Environment,
                request.InitialAdministratorEmail.Trim().ToLowerInvariant(),
                request.PlacementMode,
                TenantLifecycleState.Requested,
                1,
                [],
                null,
                now,
                now);
            state.Tenants.Add(tenant);
            await WriteAsync(state, cancellationToken).ConfigureAwait(false);
            return tenant;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TenantRegistryRecord?> GetAsync(string tenantId, CancellationToken cancellationToken)
    {
        Identifier(tenantId, 80);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return state.Tenants.SingleOrDefault(item => item.TenantId == tenantId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TenantRegistryRecord> ReplaceAsync(
        TenantRegistryRecord record,
        long expectedConfigurationVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        Identifier(record.TenantId, 80);
        if (expectedConfigurationVersion < 1) throw new InvalidOperationException("INVALID_CONFIGURATION_VERSION");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = state.Tenants.FindIndex(item => item.TenantId == record.TenantId);
            if (index < 0) throw new InvalidOperationException("TENANT_NOT_FOUND");
            var current = state.Tenants[index];
            if (current.ConfigurationVersion != expectedConfigurationVersion)
                throw new InvalidOperationException("TENANT_CONFIGURATION_CONFLICT");

            var replacement = record with { ConfigurationVersion = expectedConfigurationVersion + 1 };
            state.Tenants[index] = replacement;
            await WriteAsync(state, cancellationToken).ConfigureAwait(false);
            return replacement;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RegistryDocument> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new RegistryDocument();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<RegistryDocument>(stream, Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("TENANT_REGISTRY_INVALID");
    }

    private async Task WriteAsync(RegistryDocument document, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, Json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool SemanticallyMatches(TenantRegistryRecord existing, TenantOnboardingRequest request) =>
        existing.OrganizationName == request.OrganizationName.Trim() &&
        existing.DisplayName == request.DisplayName.Trim() &&
        existing.CountryCode == request.CountryCode.ToUpperInvariant() &&
        existing.TimeZone == request.TimeZone &&
        existing.Environment == request.Environment &&
        existing.InitialAdministratorEmail == request.InitialAdministratorEmail.Trim().ToLowerInvariant() &&
        existing.PlacementMode == request.PlacementMode;

    private static void Validate(TenantOnboardingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Identifier(request.RequestId, 128);
        Text(request.OrganizationName, 200);
        Text(request.DisplayName, 200);
        if (request.CountryCode.Length != 2 || request.CountryCode.Any(c => !char.IsAsciiLetter(c)))
            throw new InvalidOperationException("INVALID_COUNTRY_CODE");
        Text(request.TimeZone, 100);
        Identifier(request.Environment, 40);
        if (request.InitialAdministratorEmail.Length is < 3 or > 320 ||
            !request.InitialAdministratorEmail.Contains('@') ||
            request.InitialAdministratorEmail.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("INVALID_INITIAL_ADMINISTRATOR_EMAIL");
    }

    private static void Identifier(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')))
            throw new InvalidOperationException("INVALID_IDENTIFIER");
    }

    private static void Text(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new InvalidOperationException("INVALID_TEXT");
    }

    private sealed class RegistryDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<TenantRegistryRecord> Tenants { get; set; } = [];
    }
}
