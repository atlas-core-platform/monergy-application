using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialProfile.Application;

public interface IVehicleRecordRepository
{
    Task<ReviewedVehicleRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedVehicleRecord record, CancellationToken ct);
    Task<ReviewedVehicleRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct);
    Task<VehicleRecordList> ListAsync(string tenant, string customer, CancellationToken ct);
}

public sealed class VehicleRecordsApplication(IFinancialProfileRepository financial,
    IVehicleRecordRepository repository, TimeProvider clock)
{
    public static bool Valid(CreateVehicleRecord? input, DateTimeOffset now)
    {
        if (input?.Vehicle is not { } vehicle || !Guid.TryParseExact(input.OperationId, "N", out _) || input.OperationId.Any(char.IsAsciiLetterUpper) ||
            !Text(vehicle.Name) || !OptionalRegistration(vehicle.RegistrationNumber) ||
            !Text(vehicle.VehicleType) || !Money(vehicle.PurchaseValue) || !Money(vehicle.CurrentValue) ||
            vehicle.ValueAsOfDate.Year < 1900 || vehicle.ValueAsOfDate > DateOnly.FromDateTime(now.UtcDateTime.AddHours(14)) ||
            !TenantAccessClient.Identifier(vehicle.SourceFinancialFactId, 160) || vehicle.SourceRevision < 1)
            return false;
        return true;
    }
    public async Task<ReviewedVehicleRecord> CreateAsync(string tenant, string customer, string actor,
        CreateVehicleRecord input, CancellationToken ct)
    {
        if (!Valid(input, clock.GetUtcNow())) throw new TenantBoundaryException("VEHICLE_RECORD_INVALID", 400);
        var source = await financial.GetFactAsync(input.Vehicle.SourceFinancialFactId, input.Vehicle.SourceRevision, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.Vehicle)
            throw new TenantBoundaryException("VEHICLE_SOURCE_NOT_FOUND", 404);
        var provenance = await financial.GetProvenanceAsync(source.FinancialProvenanceId, ct).ConfigureAwait(false);
        if (provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || !Currency(source.Currency))
            throw new TenantBoundaryException("VEHICLE_SOURCE_UNAVAILABLE", 503);
        // Exact immutable source revision is a reference. Purchase and current values are explicitly reviewed separately.
        var normalized = input with { Vehicle = input.Vehicle with { Name = input.Vehicle.Name.Trim(), RegistrationNumber = string.IsNullOrWhiteSpace(input.Vehicle.RegistrationNumber) ? null : input.Vehicle.RegistrationNumber.Trim(), VehicleType = input.Vehicle.VehicleType.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var id = "vehicle-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var record = new ReviewedVehicleRecord(id, input.OperationId, customer, normalized.Vehicle, source.Currency,
            provenance.FinancialProvenanceId, provenance.DocumentVersionId, provenance.EvidenceId, actor, clock.GetUtcNow());
        return await repository.CreateAsync(tenant, customer, actor, fingerprint, record, ct).ConfigureAwait(false);
    }
    public Task<VehicleRecordList> ListAsync(string tenant, string customer, CancellationToken ct) => repository.ListAsync(tenant, customer, ct);
    public async Task<ReviewedVehicleRecord> ReadAsync(string tenant, string customer, string id, CancellationToken ct)
    {
        if (!TenantAccessClient.Identifier(id, 80)) throw new TenantBoundaryException("VEHICLE_RECORD_INVALID", 400);
        return await repository.ReadAsync(tenant, customer, id, ct).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("VEHICLE_RECORD_NOT_FOUND", 404);
    }
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 120 && !value.Any(char.IsControl);
    private static bool OptionalRegistration(string? value) => value is null || value.Trim().Length <= 40 && !value.Any(char.IsControl);
    private static bool Money(decimal value) => value is >= 0 and <= 999999999999999m && decimal.Round(value, 2) == value;
    private static bool Currency(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);
}
