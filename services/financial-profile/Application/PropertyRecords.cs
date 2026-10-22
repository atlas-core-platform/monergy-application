using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialProfile.Application;

public interface IPropertyRecordRepository
{
    Task<ReviewedPropertyRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedPropertyRecord record, CancellationToken ct);
    Task<ReviewedPropertyRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null);
    Task<PropertyRecordList> SnapshotAsync(string tenant, string customer, DateOnly asOfDate, string currency, CancellationToken ct);
    Task<PropertyRecordList> ListAsync(string tenant, string customer, CancellationToken ct, string? afterId = null);
    Task<ReviewedPropertyRecord> ReviseAsync(string tenant, string customer, string actor, string fingerprint,
        int expectedRevision, ReviewedPropertyRecord record, CancellationToken ct);
    Task<PropertyRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct);
}

public sealed class PropertyRecordsApplication(IFinancialProfileRepository financial,
    IPropertyRecordRepository repository, TimeProvider clock)
{
    public static bool Valid(CreatePropertyRecord? input, DateTimeOffset now)
    {
        if (input?.Property is not { } property || !Guid.TryParseExact(input.OperationId, "N", out _) || input.OperationId.Any(char.IsAsciiLetterUpper) ||
            !Text(property.Name) || !Text(property.Location) ||
            !Text(property.PropertyType) || !Money(property.PurchaseValue) || !Money(property.CurrentValue) ||
            property.ValueAsOfDate.Year < 1900 || property.ValueAsOfDate > DateOnly.FromDateTime(now.UtcDateTime.AddHours(14)) ||
            !TenantAccessClient.Identifier(property.SourceFinancialFactId, 160) || property.SourceRevision < 1)
            return false;
        return true;
    }
    public async Task<ReviewedPropertyRecord> CreateAsync(string tenant, string customer, string actor,
        CreatePropertyRecord input, CancellationToken ct)
    {
        if (!Valid(input, clock.GetUtcNow())) throw new TenantBoundaryException("PROPERTY_RECORD_INVALID", 400);
        var source = await financial.GetFactAsync(input.Property.SourceFinancialFactId, input.Property.SourceRevision, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.Property)
            throw new TenantBoundaryException("PROPERTY_SOURCE_NOT_FOUND", 404);
        var provenance = await financial.GetProvenanceAsync(source.FinancialProvenanceId, ct).ConfigureAwait(false);
        if (provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || !Currency(source.Currency))
            throw new TenantBoundaryException("PROPERTY_SOURCE_UNAVAILABLE", 503);
        // Exact immutable source revision is a reference. Values remain explicitly customer reviewed.
        var normalized = input with { Property = input.Property with { Name = input.Property.Name.Trim(), Location = input.Property.Location.Trim(), PropertyType = input.Property.PropertyType.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var id = "property-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var record = new ReviewedPropertyRecord(id, input.OperationId, customer, normalized.Property, source.Currency,
            provenance.FinancialProvenanceId, provenance.DocumentVersionId, provenance.EvidenceId, actor, clock.GetUtcNow());
        return await repository.CreateAsync(tenant, customer, actor, fingerprint, record, ct).ConfigureAwait(false);
    }
    public Task<PropertyRecordList> ListAsync(string tenant, string customer, CancellationToken ct, string? afterId = null) => repository.ListAsync(tenant, customer, ct, ReviewedRecordPaging.Validate(afterId));
    public async Task<ReviewedPropertyRecord> ReviseAsync(string tenant, string customer, string actor, string id,
        RevisePropertyRecord input, CancellationToken ct)
    {
        if (input is null || input.ExpectedRevision is < 1 or int.MaxValue ||
            !Valid(new(input.OperationId, input.Property), clock.GetUtcNow()))
            throw new TenantBoundaryException("PROPERTY_RECORD_INVALID", 400);
        var original = await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        if (original.Property.SourceFinancialFactId != input.Property.SourceFinancialFactId || original.Property.SourceRevision != input.Property.SourceRevision)
            throw new TenantBoundaryException("PROPERTY_SOURCE_IMMUTABLE", 400);
        var source = await financial.GetFactAsync(original.Property.SourceFinancialFactId, original.Property.SourceRevision, ct).ConfigureAwait(false);
        var provenance = await financial.GetProvenanceAsync(original.SourceProvenanceId, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.Property ||
            source.Currency != original.Currency || source.FinancialProvenanceId != original.SourceProvenanceId ||
            provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || provenance.EvidenceId != original.EvidenceId || provenance.DocumentVersionId != original.DocumentVersionId)
            throw new TenantBoundaryException("PROPERTY_SOURCE_UNAVAILABLE", 503);
        var normalized = input with { Property = input.Property with { Name = input.Property.Name.Trim(), Location = input.Property.Location.Trim(), PropertyType = input.Property.PropertyType.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var record = original with { OperationId = input.OperationId, Property = normalized.Property, RecordedBy = actor,
            RecordedAt = clock.GetUtcNow(), Revision = input.ExpectedRevision + 1 };
        return await repository.ReviseAsync(tenant, customer, actor, fingerprint, input.ExpectedRevision, record, ct).ConfigureAwait(false);
    }
    public async Task<PropertyRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct)
    {
        if (beforeRevision is < 1) throw new TenantBoundaryException("PROPERTY_RECORD_INVALID", 400);
        await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        return await repository.HistoryAsync(tenant, customer, id, beforeRevision, ct).ConfigureAwait(false);
    }
    public async Task<ReviewedPropertyRecord> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null)
    {
        if (!TenantAccessClient.Identifier(id, 80) || revision is < 1) throw new TenantBoundaryException("PROPERTY_RECORD_INVALID", 400);
        return await repository.ReadAsync(tenant, customer, id, ct, revision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("PROPERTY_RECORD_NOT_FOUND", 404);
    }
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 120 && !value.Any(char.IsControl);
    private static bool Money(decimal value) => value is >= 0 and <= 999999999999999m && decimal.Round(value, 2) == value;
    private static bool Currency(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);
}
