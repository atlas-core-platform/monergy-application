using Monergy.Platform;
using Monergy.Services.Evidence.Application;

namespace Monergy.Services.Evidence.Infrastructure;

public sealed partial class InMemoryEvidenceRepository : IDocumentVaultRepository
{
    private readonly Dictionary<string, VaultMetadata> vaultMetadata = new(StringComparer.Ordinal);
    private readonly Dictionary<(VaultScope Scope, string Operation), (string Version, string Actor, VaultMetadataChange Change, VaultMetadata Receipt)> vaultOperations = [];
    public Task<VaultItem?> VaultReadAsync(VaultScope scope, string versionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult(versions.TryGetValue(versionId, out var row) && scope.Owns(row)
            ? DocumentVault.View(row, vaultMetadata.GetValueOrDefault(versionId) ?? VaultMetadata.Empty) : null);
    }
    public Task<VaultPage> VaultListAsync(VaultScope scope, string category, string search, string? after, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); DocumentVault.Query(category, search, after);
        lock (sync)
        {
            EvidenceVersionRecord? cursor = null;
            if (after is not null && (!versions.TryGetValue(after, out cursor) || !scope.Owns(cursor))) throw new TenantBoundaryException("VAULT_CURSOR_INVALID", 400);
            var rows = versions.Values.Where(scope.Owns)
                .Where(x => cursor is null || x.CreatedAt < cursor.CreatedAt || x.CreatedAt == cursor.CreatedAt && StringComparer.Ordinal.Compare(x.DocumentVersionId, cursor.DocumentVersionId) < 0)
                .Select(x => DocumentVault.View(x, vaultMetadata.GetValueOrDefault(x.DocumentVersionId) ?? VaultMetadata.Empty))
                .Where(x => (category == "all" || x.Metadata.Category == category) && x.OriginalFileName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.DocumentVersionId, StringComparer.Ordinal).Take(51).ToArray();
            return Task.FromResult(new VaultPage(rows.Take(50).ToArray(), rows.Length > 50 ? rows[49].DocumentVersionId : null));
        }
    }
    public Task<VaultMetadata> VaultSaveAsync(VaultScope scope, string versionId, string actor, VaultMetadataChange change, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); DocumentVault.Validate(change);
        lock (sync)
        {
            if (!versions.TryGetValue(versionId, out var row) || !scope.Owns(row)) throw new TenantBoundaryException("VAULT_NOT_FOUND", 404);
            var key = (scope, change.OperationId);
            if (vaultOperations.TryGetValue(key, out var prior))
            {
                if (prior.Version != versionId || prior.Actor != actor || prior.Change != change) throw new TenantBoundaryException("VAULT_OPERATION_CONFLICT", 409);
                return Task.FromResult(prior.Receipt);
            }
            var current = vaultMetadata.GetValueOrDefault(versionId) ?? VaultMetadata.Empty;
            if (current.Revision != change.ExpectedRevision) throw new TenantBoundaryException("VAULT_METADATA_CHANGED", 409);
            var receipt = new VaultMetadata(change.Category, change.DocumentType, change.DocumentDate, change.FinancialYear, current.Revision + 1, actor, DateTimeOffset.UtcNow);
            vaultMetadata[versionId] = receipt;
            vaultOperations.Add(key, (versionId, actor, change, receipt));
            return Task.FromResult(receipt);
        }
    }
}
