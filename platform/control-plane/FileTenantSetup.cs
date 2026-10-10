using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Monergy.Platform.ControlPlane;

public sealed record TenantSetupSubmission(string RequestId, long ExpectedPolicyVersion, bool OrganizationReviewed, bool AccessReviewed);
public sealed record TenantSetupReceipt(string TenantId, string ActorId, string RequestId, string ContentHash,
    long ReviewedPolicyVersion, string ReceiptReference, DateTimeOffset CompletedAt);

// Protected server storage. This class accepts an already authenticated admin
// context from the host, never an actor or policy revision supplied by a browser.
public sealed class FileTenantSetup(string path) : ITenantSetupReadiness
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path = Path.GetFullPath(path);

    public async Task<TenantSetupReceipt?> GetAsync(string tenantId, CancellationToken ct)
    {
        await using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        return (await ReadAllAsync(ct).ConfigureAwait(false)).SingleOrDefault(receipt => receipt.TenantId == tenantId);
    }

    public async Task<TenantSetupReceipt> CompleteAsync(TenantRegistryRecord tenant, string trustedActor, long currentPolicyVersion,
        TenantSetupSubmission request, TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant); ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(clock);
        if (tenant.State is not (TenantLifecycleState.ReadyForAdmin or TenantLifecycleState.Active) || tenant.Receipts.Count != 6)
            throw new InvalidOperationException("TENANT_NOT_READY_FOR_SETUP");
        if (!Identifier(trustedActor) || !Identifier(request.RequestId) || request.ExpectedPolicyVersion < 1 ||
            !request.OrganizationReviewed || !request.AccessReviewed)
            throw new InvalidOperationException("TENANT_SETUP_REVIEW_REQUIRED");
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { tenant.TenantId, trustedActor, request }, Json)));
        await using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        var receipts = await ReadAllAsync(ct).ConfigureAwait(false);
        var prior = receipts.SingleOrDefault(receipt => receipt.TenantId == tenant.TenantId);
        if (prior is not null)
        {
            if (prior.RequestId != request.RequestId || prior.ContentHash != hash)
                throw new InvalidOperationException("TENANT_SETUP_ALREADY_COMPLETED");
            return prior;
        }
        if (currentPolicyVersion != request.ExpectedPolicyVersion) throw new InvalidOperationException("TENANT_SETUP_POLICY_CHANGED");
        var receipt = new TenantSetupReceipt(tenant.TenantId, trustedActor, request.RequestId, hash, currentPolicyVersion,
            "setup-" + Guid.NewGuid().ToString("N"), clock.GetUtcNow());
        receipts.Add(receipt);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, receipts, Json, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return receipt;
    }

    public async Task<TenantSetupReadiness> ReadAsync(string tenantId, CancellationToken cancellationToken)
    {
        var receipt = await GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return new(tenantId, receipt is not null, receipt?.ReceiptReference);
    }

    private async Task<List<TenantSetupReceipt>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return [];
        await using var stream = File.OpenRead(path);
        var receipts = await JsonSerializer.DeserializeAsync<List<TenantSetupReceipt>>(stream, Json, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("TENANT_SETUP_STORE_INVALID");
        if (receipts.Any(item => !Identifier(item.TenantId) || !Identifier(item.ActorId) || !Identifier(item.RequestId) ||
                !Identifier(item.ReceiptReference) || item.ReviewedPolicyVersion < 1 || item.ContentHash.Length != 64) ||
            receipts.Select(item => item.TenantId).Distinct(StringComparer.Ordinal).Count() != receipts.Count)
            throw new InvalidOperationException("TENANT_SETUP_STORE_INVALID");
        return receipts;
    }

    private async Task<FileStream> LeaseAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(25, ct).ConfigureAwait(false); }
        }
    }

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 128 } && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}
