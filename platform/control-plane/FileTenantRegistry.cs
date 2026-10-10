using System.Text.Json;
using System.Diagnostics;

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

public sealed class FileTenantRegistry : ITenantRegistry, IDisposable
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

    public void Dispose() => gate.Dispose();

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
            await using var lease = await AcquireFileLeaseAsync(cancellationToken).ConfigureAwait(false);
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
            next.PlacementMode != current.PlacementMode || next.CreatedAt != current.CreatedAt || next.UpdatedAt < current.UpdatedAt)
            throw new InvalidOperationException("TENANT_REGISTRY_IDENTITY_IMMUTABLE");
        if (next.Receipts is null || next.Receipts.Count < current.Receipts.Count ||
            next.Receipts.Count > current.Receipts.Count + 1 || next.Receipts.Count > 6 ||
            !next.Receipts.Take(current.Receipts.Count).SequenceEqual(current.Receipts))
            throw new InvalidOperationException("TENANT_PROVISIONING_RECEIPTS_IMMUTABLE");
        for (var i = 0; i < next.Receipts.Count; i++)
        {
            var receipt = next.Receipts[i];
            if ((int)receipt.Step != i + 1 || receipt.OperationId != $"{next.TenantId}:{i + 1:D2}" ||
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
            (TenantLifecycleState.Provisioning, TenantLifecycleState.ReadyForAdmin) => next.Receipts.Count == 6,
            (TenantLifecycleState.ProvisioningFailed, TenantLifecycleState.Provisioning) => true,
            (TenantLifecycleState.ReadyForAdmin, TenantLifecycleState.Active) => next.Receipts.Count == 6 &&
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
        string.Equals(existing.CountryCode, request.CountryCode, StringComparison.OrdinalIgnoreCase) &&
        existing.TimeZone == request.TimeZone &&
        existing.Environment == request.Environment &&
        string.Equals(existing.InitialAdministratorEmail, request.InitialAdministratorEmail.Trim(), StringComparison.OrdinalIgnoreCase) &&
        existing.PlacementMode == request.PlacementMode;

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

    private sealed class RegistryDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<TenantRegistryRecord> Tenants { get; set; } = [];
    }
}
