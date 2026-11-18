using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Monergy.Platform.ControlPlane;

public interface ITenantRegistry
{
    Task<TenantRegistryRecord> CreateOrGetAsync(
        TenantOnboardingRequest request,
        TimeProvider clock,
        CancellationToken cancellationToken);

    Task<TenantRegistryRecord?> GetAsync(string tenantId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TenantRegistryRecord>> ListAsync(CancellationToken cancellationToken);

    Task<TenantEndpointResolution?> ResolveEndpointAsync(
        string hostname,
        string environment,
        CancellationToken cancellationToken);

    Task<TenantRegistryRecord> ReplaceAsync(
        TenantRegistryRecord record,
        long expectedConfigurationVersion,
        CancellationToken cancellationToken);
}

public sealed class FileTenantRegistry : ITenantRegistry, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IReadOnlyDictionary<string, string> localTenantBindings;
    private readonly IReadOnlyDictionary<string, TenantEndpointResolution> staticEndpoints;

    private static readonly HashSet<string> ReservedSlugs = new(StringComparer.Ordinal)
    { "platform", "www", "api", "admin", "login", "auth", "gateway" };
    private static readonly Regex SlugPattern = new("^[a-z0-9](?:[a-z0-9-]{1,46}[a-z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public FileTenantRegistry(
        string path,
        IReadOnlyDictionary<string, string>? localTenantBindings = null,
        IReadOnlyDictionary<string, TenantEndpointResolution>? staticEndpoints = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Registry path is required.", nameof(path));
        this.path = Path.GetFullPath(path);
        this.localTenantBindings = localTenantBindings is null ? new Dictionary<string, string>() :
            new Dictionary<string, string>(localTenantBindings, StringComparer.Ordinal);
        foreach (var binding in this.localTenantBindings) { Identifier(binding.Key, 128); Identifier(binding.Value, 64); }
        if (this.localTenantBindings.Values.Distinct(StringComparer.Ordinal).Count() != this.localTenantBindings.Count)
            throw new InvalidOperationException("LOCAL_TENANT_BINDING_CONFLICT");
        this.staticEndpoints = staticEndpoints is null
            ? new Dictionary<string, TenantEndpointResolution>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, TenantEndpointResolution>(staticEndpoints, StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in this.staticEndpoints)
        {
            Hostname(endpoint.Key);
            ValidateEndpoint(endpoint.Value.Endpoint, requireRegistered: true);
            if (!string.Equals(endpoint.Key, endpoint.Value.Endpoint.Hostname, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("STATIC_TENANT_ENDPOINT_INVALID");
        }
    }

    public void Dispose() => gate.Dispose();

    public async Task<TenantRegistryRecord> CreateOrGetAsync(
        TenantOnboardingRequest request,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        Validate(request);
        ArgumentNullException.ThrowIfNull(clock);
        var fixedTenant = localTenantBindings.GetValueOrDefault(request.RequestId);
        if (fixedTenant is not null && request.Environment is not ("LOCAL" or "CI_EPHEMERAL"))
            throw new InvalidOperationException("LOCAL_TENANT_BINDING_ENVIRONMENT_INVALID");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await AcquireFileLeaseAsync(cancellationToken).ConfigureAwait(false);
            var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var existing = state.Tenants.SingleOrDefault(item => item.RequestId == request.RequestId);
            if (existing is not null)
            {
                if (!SemanticallyMatches(existing, request))
                    throw new InvalidOperationException("TENANT_ONBOARDING_IDEMPOTENCY_CONFLICT");
                return existing;
            }

            var now = clock.GetUtcNow();
            if (fixedTenant is not null && state.Tenants.Any(item => item.TenantId == fixedTenant))
                throw new InvalidOperationException("LOCAL_TENANT_BINDING_CONFLICT");
            var tenantId = fixedTenant ?? "tenant-" + Guid.NewGuid().ToString("N");
            var slug = NormalizeSlug(request.TenantSlug, request.DisplayName);
            if (ReservedSlugs.Contains(slug) ||
                state.Tenants.Any(item => string.Equals(item.Endpoint?.TenantSlug, slug, StringComparison.Ordinal)) ||
                staticEndpoints.Values.Any(item => string.Equals(item.Endpoint.TenantSlug, slug, StringComparison.Ordinal)))
                throw new InvalidOperationException("TENANT_ENDPOINT_CONFLICT");
            var organizationId = "org-" + Hash(tenantId)[..24];
            var hostname = slug + ".localhost";
            var tenant = new TenantRegistryRecord(
                tenantId,
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
                now)
            {
                OrganizationId = organizationId,
                Endpoint = new TenantEndpointRegistration(
                    tenantId,
                    organizationId,
                    slug,
                    hostname,
                    request.Environment,
                    request.Environment + "-" + request.PlacementMode.ToString().ToLowerInvariant(),
                    request.PlacementMode,
                    TenantEndpointStatus.Reserved,
                    true,
                    now),
            };
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
            await using var lease = await AcquireFileLeaseAsync(cancellationToken).ConfigureAwait(false);
            var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return state.Tenants.SingleOrDefault(item => item.TenantId == tenantId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<TenantRegistryRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await AcquireFileLeaseAsync(cancellationToken).ConfigureAwait(false);
            var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return state.Tenants.OrderByDescending(item => item.CreatedAt).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<TenantEndpointResolution?> ResolveEndpointAsync(
        string hostname,
        string environment,
        CancellationToken cancellationToken)
    {
        Hostname(hostname);
        Identifier(environment, 40);
        if (staticEndpoints.TryGetValue(hostname, out var fixedEndpoint) &&
            string.Equals(fixedEndpoint.Endpoint.EnvironmentReference, environment, StringComparison.Ordinal))
            return fixedEndpoint;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await AcquireFileLeaseAsync(cancellationToken).ConfigureAwait(false);
            var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var tenant = state.Tenants.SingleOrDefault(item =>
                item.Endpoint is { Status: TenantEndpointStatus.Registered, IsPrimary: true } endpoint &&
                string.Equals(endpoint.Hostname, hostname, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(endpoint.EnvironmentReference, environment, StringComparison.Ordinal));
            return tenant?.Endpoint is null ? null :
                new TenantEndpointResolution(tenant.TenantId, tenant.OrganizationId, tenant.DisplayName, tenant.Endpoint);
        }
        finally { gate.Release(); }
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
            await using var lease = await AcquireFileLeaseAsync(cancellationToken).ConfigureAwait(false);
            var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = state.Tenants.FindIndex(item => item.TenantId == record.TenantId);
            if (index < 0) throw new InvalidOperationException("TENANT_NOT_FOUND");
            var current = state.Tenants[index];
            if (current.ConfigurationVersion != expectedConfigurationVersion)
                throw new InvalidOperationException("TENANT_CONFIGURATION_CONFLICT");

            ValidateReplacement(current, record);

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

    // The instance semaphore alone cannot serialize two registry instances/processes.
    // FileShare.None is held across read/compare/write, with a bounded cancellable wait.
    private async Task<FileStream> AcquireFileLeaseAsync(CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            { await Task.Delay(25, cancellationToken).ConfigureAwait(false); }
        }
    }

    private static void ValidateReplacement(TenantRegistryRecord current, TenantRegistryRecord next)
    {
        if (next.ConfigurationVersion != current.ConfigurationVersion || next.RequestId != current.RequestId ||
            next.OrganizationName != current.OrganizationName || next.DisplayName != current.DisplayName ||
            next.CountryCode != current.CountryCode || next.TimeZone != current.TimeZone ||
            next.Environment != current.Environment || next.InitialAdministratorEmail != current.InitialAdministratorEmail ||
            next.PlacementMode != current.PlacementMode || next.CreatedAt != current.CreatedAt || next.UpdatedAt < current.UpdatedAt ||
            next.OrganizationId != current.OrganizationId || next.Endpoint is null || current.Endpoint is null ||
            (next.Endpoint with { Status = current.Endpoint.Status, RegisteredAt = current.Endpoint.RegisteredAt }) != current.Endpoint)
            throw new InvalidOperationException("TENANT_REGISTRY_IDENTITY_IMMUTABLE");
        ValidateEndpoint(next.Endpoint, requireRegistered: next.HasCompleted(TenantProvisioningStep.TenantEndpointRegistered));
        if (current.Endpoint.Status != next.Endpoint.Status &&
            !(current.Endpoint.Status == TenantEndpointStatus.Reserved && next.Endpoint.Status == TenantEndpointStatus.Registered &&
              next.Receipts.Count == current.Receipts.Count + 1 &&
              next.Receipts[^1].Step == TenantProvisioningStep.TenantEndpointRegistered))
            throw new InvalidOperationException("TENANT_ENDPOINT_TRANSITION_INVALID");
        if (next.Receipts is null || next.Receipts.Count < current.Receipts.Count ||
            next.Receipts.Count > current.Receipts.Count + 1 || next.Receipts.Count > TenantProvisioningPlan.Count ||
            !next.Receipts.Take(current.Receipts.Count).SequenceEqual(current.Receipts))
            throw new InvalidOperationException("TENANT_PROVISIONING_RECEIPTS_IMMUTABLE");
        for (var i = 0; i < next.Receipts.Count; i++)
        {
            var receipt = next.Receipts[i];
            var expected = TenantProvisioningPlan.RequiredSteps[i];
            if (receipt.Step != expected || receipt.OperationId != $"{next.TenantId}:{(int)expected:D2}" ||
                string.IsNullOrWhiteSpace(receipt.EvidenceReference) || receipt.EvidenceReference.Length > 300 ||
                receipt.EvidenceReference.Any(char.IsControl) || receipt.CompletedAt < next.CreatedAt ||
                receipt.CompletedAt > next.UpdatedAt)
                throw new InvalidOperationException("TENANT_PROVISIONING_RECEIPT_INVALID");
        }
        if (next.Receipts.Count != current.Receipts.Count &&
            (current.State != TenantLifecycleState.Provisioning || next.State != TenantLifecycleState.Provisioning))
            throw new InvalidOperationException("TENANT_PROVISIONING_STATE_INVALID");
        var valid = (current.State, next.State) switch
        {
            (TenantLifecycleState.Requested, TenantLifecycleState.Provisioning) => true,
            (TenantLifecycleState.Provisioning, TenantLifecycleState.Provisioning or TenantLifecycleState.ProvisioningFailed) => true,
            (TenantLifecycleState.Provisioning, TenantLifecycleState.ReadyForAdmin) => next.Receipts.Count == TenantProvisioningPlan.Count,
            (TenantLifecycleState.ProvisioningFailed, TenantLifecycleState.Provisioning) => true,
            (TenantLifecycleState.ReadyForAdmin, TenantLifecycleState.Active) => next.Receipts.Count == TenantProvisioningPlan.Count &&
                !string.IsNullOrWhiteSpace(next.SetupReceiptReference) && next.SetupReceiptReference.Length <= 300 &&
                !next.SetupReceiptReference.Any(char.IsControl),
            _ => false,
        };
        if (!valid || current.SetupReceiptReference is not null && next.SetupReceiptReference != current.SetupReceiptReference ||
            next.State != TenantLifecycleState.Active && next.SetupReceiptReference != current.SetupReceiptReference)
            throw new InvalidOperationException("TENANT_LIFECYCLE_TRANSITION_INVALID");
    }

    private async Task<RegistryDocument> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new RegistryDocument();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<RegistryDocument>(stream, Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("TENANT_REGISTRY_INVALID");
        await stream.DisposeAsync().ConfigureAwait(false);
        if (document.SchemaVersion == 1)
        {
            UpgradeFromVersionOne(document);
            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
        }
        if (document.SchemaVersion != 2) throw new InvalidOperationException("TENANT_REGISTRY_SCHEMA_UNSUPPORTED");
        return document;
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
        string.Equals(existing.CountryCode, request.CountryCode, StringComparison.OrdinalIgnoreCase) &&
        existing.TimeZone == request.TimeZone &&
        existing.Environment == request.Environment &&
        string.Equals(existing.InitialAdministratorEmail, request.InitialAdministratorEmail.Trim(), StringComparison.OrdinalIgnoreCase) &&
        existing.PlacementMode == request.PlacementMode &&
        existing.Endpoint?.TenantSlug == NormalizeSlug(request.TenantSlug, request.DisplayName);

    private static void Validate(TenantOnboardingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Identifier(request.RequestId, 128);
        Text(request.OrganizationName, 200);
        Text(request.DisplayName, 200);
        if (request.CountryCode is not { Length: 2 } || request.CountryCode.Any(c => !char.IsAsciiLetter(c)))
            throw new InvalidOperationException("INVALID_COUNTRY_CODE");
        Text(request.TimeZone, 100);
        Identifier(request.Environment, 40);
        if (!Enum.IsDefined(request.PlacementMode)) throw new InvalidOperationException("INVALID_PLACEMENT_MODE");
        if (ReservedSlugs.Contains(NormalizeSlug(request.TenantSlug, request.DisplayName)))
            throw new InvalidOperationException("TENANT_SLUG_RESERVED");
        if (request.InitialAdministratorEmail is not { Length: >= 3 and <= 320 } ||
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

    private static string NormalizeSlug(string? requested, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(requested) ? fallback : requested;
        var slug = Regex.Replace(source.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        if (!string.IsNullOrWhiteSpace(requested) && !string.Equals(requested, slug, StringComparison.Ordinal))
            throw new InvalidOperationException("INVALID_TENANT_SLUG");
        if (!SlugPattern.IsMatch(slug)) throw new InvalidOperationException("INVALID_TENANT_SLUG");
        return slug;
    }

    private static void Hostname(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253 ||
            !value.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.')))
            throw new InvalidOperationException("INVALID_TENANT_HOSTNAME");
    }

    private static void ValidateEndpoint(TenantEndpointRegistration endpoint, bool requireRegistered)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Identifier(endpoint.TenantId, 80);
        Identifier(endpoint.OrganizationId, 80);
        var slug = NormalizeSlug(endpoint.TenantSlug, endpoint.TenantSlug);
        if (ReservedSlugs.Contains(slug)) throw new InvalidOperationException("TENANT_SLUG_RESERVED");
        if (endpoint.Hostname != slug + ".localhost") throw new InvalidOperationException("TENANT_ENDPOINT_INVALID");
        Hostname(endpoint.Hostname);
        Identifier(endpoint.EnvironmentReference, 40);
        Identifier(endpoint.DeploymentReference, 100);
        if (!endpoint.IsPrimary || requireRegistered && endpoint.Status != TenantEndpointStatus.Registered)
            throw new InvalidOperationException("TENANT_ENDPOINT_INVALID");
    }

    private static void UpgradeFromVersionOne(RegistryDocument document)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < document.Tenants.Count; index++)
        {
            var tenant = document.Tenants[index];
            var organizationId = string.IsNullOrWhiteSpace(tenant.OrganizationId)
                ? "org-" + Hash(tenant.TenantId)[..24]
                : tenant.OrganizationId;
            var baseSlug = tenant.TenantId is "T001" or "T002"
                ? tenant.TenantId.ToLowerInvariant()
                : NormalizeSlug(null, tenant.DisplayName);
            var slug = baseSlug;
            if (ReservedSlugs.Contains(slug) || !used.Add(slug))
            {
                slug = (baseSlug[..Math.Min(baseSlug.Length, 39)].TrimEnd('-') + "-" + Hash(tenant.TenantId)[..8]);
                if (!used.Add(slug)) throw new InvalidOperationException("TENANT_ENDPOINT_CONFLICT");
            }
            var receipts = tenant.Receipts.ToList();
            var placementIndex = receipts.FindIndex(item => item.Step == TenantProvisioningStep.PlacementResolved);
            var endpointReceiptIndex = receipts.FindIndex(item => item.Step == TenantProvisioningStep.TenantEndpointRegistered);
            if (placementIndex >= 0 && endpointReceiptIndex < 0)
            {
                var completed = receipts[placementIndex].CompletedAt;
                receipts.Insert(placementIndex + 1, new TenantProvisioningReceipt(
                    TenantProvisioningStep.TenantEndpointRegistered,
                    $"{tenant.TenantId}:{(int)TenantProvisioningStep.TenantEndpointRegistered:D2}",
                    "endpoint:migrated:" + Hash(tenant.TenantId + ":" + slug),
                    completed));
            }
            var registered = receipts.Any(item => item.Step == TenantProvisioningStep.TenantEndpointRegistered);
            document.Tenants[index] = tenant with
            {
                OrganizationId = organizationId,
                Endpoint = new TenantEndpointRegistration(
                    tenant.TenantId, organizationId, slug, slug + ".localhost", tenant.Environment,
                    tenant.Environment + "-" + tenant.PlacementMode.ToString().ToLowerInvariant(),
                    tenant.PlacementMode,
                    registered ? TenantEndpointStatus.Registered : TenantEndpointStatus.Reserved,
                    true,
                    registered ? receipts.Single(item => item.Step == TenantProvisioningStep.TenantEndpointRegistered).CompletedAt : tenant.CreatedAt),
                Receipts = receipts,
            };
        }
        document.SchemaVersion = 2;
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class RegistryDocument
    {
        public int SchemaVersion { get; set; } = 2;
        public List<TenantRegistryRecord> Tenants { get; set; } = [];
    }
}
