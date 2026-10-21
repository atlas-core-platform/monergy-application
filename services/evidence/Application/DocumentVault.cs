using System.Globalization;
using Monergy.Platform;

namespace Monergy.Services.Evidence.Application;

public sealed record EvidenceSourceLocation(string Provider, string FolderId, string FileId, string FileVersion);

public sealed record VaultScope(string TenantId, string CustomerId)
{
    public bool Owns(EvidenceVersionRecord row) => row.TenantId == TenantId && row.CustomerId == CustomerId;
}
public sealed record VaultMetadata(string Category, string? DocumentType, string? DocumentDate, string? FinancialYear,
    int Revision, string? UpdatedBy, DateTimeOffset? UpdatedAt)
{
    public static VaultMetadata Empty { get; } = new("Other", null, null, null, 0, null, null);
}
public sealed record VaultMetadataChange(string OperationId, int ExpectedRevision, string Category,
    string? DocumentType, string? DocumentDate, string? FinancialYear);
public sealed record VaultItem(string DocumentId, string DocumentVersionId, string EvidenceId, int Version,
    string OriginalFileName, string ContentType, long ContentLength, string ContentSha256,
    string SourceName, DateTimeOffset ReceivedAt, DateTimeOffset CreatedAt, VaultMetadata Metadata,
    string ScanStatus, string VerificationStatus, string StorageMode, string? StorageLocation, string? StorageVersionId,
    EvidenceSourceLocation? SourceLocation);
public sealed record VaultPage(IReadOnlyList<VaultItem> Items, string? NextCursor);
public interface IDocumentVaultRepository
{
    Task<VaultPage> VaultListAsync(VaultScope scope, string category, string search, string? after, CancellationToken ct);
    Task<VaultItem?> VaultReadAsync(VaultScope scope, string versionId, CancellationToken ct);
    Task<VaultMetadata> VaultSaveAsync(VaultScope scope, string versionId, string actor, VaultMetadataChange change, CancellationToken ct);
}
public static class DocumentVault
{
    public static readonly IReadOnlyList<string> Categories = Array.AsReadOnly(new[]
        { "Identity", "Income Tax", "Banking", "Credit", "Loans", "Insurance", "Investments", "Property", "Vehicles", "Other" });
    public static void Validate(VaultMetadataChange change)
    {
        if (!TenantAccessClient.Identifier(change.OperationId, 80) || change.ExpectedRevision is < 0 or >= int.MaxValue ||
            !Categories.Contains(change.Category, StringComparer.Ordinal) ||
            change.DocumentType is not null && (string.IsNullOrWhiteSpace(change.DocumentType) || change.DocumentType.Length > 80 || change.DocumentType.Any(char.IsControl)) ||
            change.DocumentDate is not null && (!DateOnly.TryParseExact(change.DocumentDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year < 1900) ||
            change.FinancialYear is not null && !ValidYear(change.FinancialYear)) throw new TenantBoundaryException("VAULT_METADATA_INVALID", 400);
    }
    private static bool ValidYear(string value) => value.Length == 9 && value[4] == '-' &&
        int.TryParse(value[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var first) &&
        int.TryParse(value[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var last) && first >= 1900 && last == first + 1;
    public static void Query(string category, string search, string? after)
    {
        if (category != "all" && !Categories.Contains(category, StringComparer.Ordinal) || search.Length > 120 || search.Any(char.IsControl) ||
            after is not null && !TenantAccessClient.Identifier(after, 160)) throw new TenantBoundaryException("VAULT_QUERY_INVALID", 400);
    }
    public static void Replacement(EvidenceVersionRecord candidate, EvidenceVersionRecord? latest)
    {
        if (candidate.ExpectedPreviousVersionId is null) return;
        if (latest is null || latest.DocumentVersionId != candidate.ExpectedPreviousVersionId || latest.TenantId != candidate.TenantId || latest.CustomerId != candidate.CustomerId)
            throw new TenantBoundaryException("VAULT_VERSION_CHANGED", 409);
        if (string.Equals(latest.ContentSha256, candidate.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new TenantBoundaryException("VAULT_CONTENT_UNCHANGED", 409);
    }
    public static VaultItem View(EvidenceVersionRecord row, VaultMetadata metadata) => new(row.DocumentId, row.DocumentVersionId,
        row.EvidenceId, row.Version, row.OriginalFileName, row.ContentType, row.ContentLength, row.ContentSha256,
        row.SourceName, row.ReceivedAt, row.CreatedAt, metadata,
        DocumentMonitoring.Scanned(row) ? "VERIFIED_AT_INTAKE" : "UNRECORDED", "NOT_VERIFIED",
        row.ContentReference.StartsWith("reference://", StringComparison.Ordinal) ? "TEMPORARY_REFERENCE" : "OWNER_MANAGED",
        row.ContentStorageKey, row.ContentStorageVersionId, row.SourceLocation);
}
