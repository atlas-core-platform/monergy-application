using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Xunit;
namespace Monergy.FinancialProfile.Tests;

public sealed partial class ReviewedVehicleRecordTests
{
    [Fact]
    public async Task ExistingVehiclePayloadsRemainUnreviewedWithoutInventingAnOwnershipDate()
    {
        var (financial, source) = await Seed();
        var first = await new VehicleRecordsApplication(financial, new InMemoryVehicleRecords(), TimeProvider.System)
            .CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId), default);
        var legacy = JsonSerializer.Serialize(first, ContractJson.Options)
            .Replace(",\"ownershipStatus\":\"UNREVIEWED\",\"ownershipAsOfDate\":null", "", StringComparison.Ordinal);
        var restored = JsonSerializer.Deserialize<ReviewedVehicleRecord>(legacy, ContractJson.Options);
        Assert.NotNull(restored); Assert.Equal("UNREVIEWED", restored.OwnershipStatus); Assert.Null(restored.OwnershipAsOfDate);
    }

    [Fact]
    public async Task CustomerCanDisposeAndRestoreVehicleWhileEveryVersionAndSourceRemainImmutable()
    {
        var (financial, source) = await Seed(); var repository = new InMemoryVehicleRecords();
        var app = new VehicleRecordsApplication(financial, repository, TimeProvider.System);
        var first = await app.CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId), default);
        var dispose = new ReviewVehicleOwnership(Guid.NewGuid().ToString("N"), 1, "DISPOSED", new(2026, 10, 2));
        var disposed = await app.ReviewOwnershipAsync("T001", Customer, "actor-001", first.VehicleRecordId, dispose, default);
        Assert.Equal("DISPOSED", disposed.OwnershipStatus); Assert.Equal(dispose.AsOfDate, disposed.OwnershipAsOfDate);
        Assert.Equal(disposed, await app.ReviewOwnershipAsync("T001", Customer, "actor-001", first.VehicleRecordId, dispose, default));

        var restore = new ReviewVehicleOwnership(Guid.NewGuid().ToString("N"), 2, "OWNED", new(2026, 10, 3));
        var owned = await app.ReviewOwnershipAsync("T001", Customer, "actor-001", first.VehicleRecordId, restore, default);
        Assert.Equal("OWNED", owned.OwnershipStatus); Assert.Equal(3, owned.Revision);
        Assert.Equal(first.Vehicle, owned.Vehicle); Assert.Equal(first.SourceProvenanceId, owned.SourceProvenanceId);
        var history = await app.HistoryAsync("T001", Customer, first.VehicleRecordId, null, default);
        Assert.Equal([3, 2, 1], history.Items.Select(x => x.Revision));
        Assert.Equal(["OWNED", "DISPOSED", "UNREVIEWED"], history.Items.Select(x => x.OwnershipStatus));
    }

    [Theory]
    [InlineData("INVALID", "2026-10-02")]
    [InlineData("OWNED", "2026-09-30")]
    [InlineData("DISPOSED", "2099-01-01")]
    public async Task InvalidVehicleOwnershipReviewDoesNotAppendARevision(string status, string date)
    {
        var (financial, source) = await Seed(); var repository = new InMemoryVehicleRecords();
        var app = new VehicleRecordsApplication(financial, repository, TimeProvider.System);
        var first = await app.CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId), default);
        var input = new ReviewVehicleOwnership(Guid.NewGuid().ToString("N"), 1, status,
            DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(400, (await Assert.ThrowsAsync<TenantBoundaryException>(() =>
            app.ReviewOwnershipAsync("T001", Customer, "actor-001", first.VehicleRecordId, input, default))).Status);
        Assert.Equal(first, await app.ReadAsync("T001", Customer, first.VehicleRecordId, default));
    }

    [Fact]
    public async Task StaleVehicleOwnershipReviewLosesWithoutChangingTheWinningVersion()
    {
        var (financial, source) = await Seed(); var repository = new InMemoryVehicleRecords();
        var app = new VehicleRecordsApplication(financial, repository, TimeProvider.System);
        var first = await app.CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId), default);
        async Task<bool> Attempt(string status)
        {
            try { await app.ReviewOwnershipAsync("T001", Customer, "actor-001", first.VehicleRecordId,
                new(Guid.NewGuid().ToString("N"), 1, status, new(2026, 10, 2)), default); return true; }
            catch (TenantBoundaryException error) when (error.Status == 409) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => Attempt("OWNED")), Task.Run(() => Attempt("DISPOSED")));
        Assert.Single(results, x => x); Assert.Equal(2, (await app.ReadAsync("T001", Customer, first.VehicleRecordId, default)).Revision);
    }

    [Fact]
    public async Task CorrectionsPreserveVehicleOwnershipAndDisposedVehicleLeavesTheEffectiveSnapshot()
    {
        var (financial, source) = await Seed(); var repository = new InMemoryVehicleRecords();
        var app = new VehicleRecordsApplication(financial, repository, TimeProvider.System);
        var input = Input(source.FinancialFactId) with { Vehicle = Input(source.FinancialFactId).Vehicle with { ValueAsOfDate = new(2026, 10, 2) } };
        var first = await app.CreateAsync("T001", Customer, "actor-001", input, default);
        var disposed = await app.ReviewOwnershipAsync("T001", Customer, "actor-001", first.VehicleRecordId,
            new(Guid.NewGuid().ToString("N"), 1, "DISPOSED", new(2026, 10, 2)), default);
        Assert.Empty((await repository.SnapshotAsync("T001", Customer, new(2026, 10, 2), first.Currency, default)).Items);
        var correction = new ReviseVehicleRecord(Guid.NewGuid().ToString("N"), 2, disposed.Vehicle with { RegistrationNumber = "Corrected reference" });
        var revised = await app.ReviseAsync("T001", Customer, "actor-001", first.VehicleRecordId, correction, default);
        Assert.Equal("DISPOSED", revised.OwnershipStatus); Assert.Equal(disposed.OwnershipAsOfDate, revised.OwnershipAsOfDate);
        var invalid = correction with { OperationId = Guid.NewGuid().ToString("N"), ExpectedRevision = 3,
            Vehicle = revised.Vehicle with { ValueAsOfDate = new(2026, 10, 3) } };
        Assert.Equal(400, (await Assert.ThrowsAsync<TenantBoundaryException>(() =>
            app.ReviseAsync("T001", Customer, "actor-001", first.VehicleRecordId, invalid, default))).Status);
    }

    [Fact]
    public async Task GatewayVehicleOwnershipReviewIsAuthorizedReadBackAndIdempotent()
    {
        await using var h = await Harness.Create(); using var client = h.Client();
        var created = await (await client.PostAsJsonAsync("/product-api/v1/vehicle-records", Input(h.Source.FinancialFactId), ContractJson.Options))
            .Content.ReadFromJsonAsync<ReviewedVehicleRecord>(ContractJson.Options);
        Assert.NotNull(created);
        var input = new ReviewVehicleOwnership(Guid.NewGuid().ToString("N"), 1, "DISPOSED", new(2026, 10, 2));
        var path = "/product-api/v1/vehicle-records/" + created.VehicleRecordId + "/ownership";
        var response = await client.PostAsJsonAsync(path, input, ContractJson.Options); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposed = await response.Content.ReadFromJsonAsync<ReviewedVehicleRecord>(ContractJson.Options);
        Assert.Equal("DISPOSED", disposed!.OwnershipStatus); Assert.Equal(2, disposed.Revision);
        Assert.Equal(disposed, await (await client.PostAsJsonAsync(path, input, ContractJson.Options)).Content.ReadFromJsonAsync<ReviewedVehicleRecord>(ContractJson.Options));
        Assert.Equal([2, 1], (await client.GetFromJsonAsync<VehicleRecordHistory>(
            "/product-api/v1/vehicle-records/" + created.VehicleRecordId + "/history", ContractJson.Options))!.Items.Select(x => x.Revision));
    }

    [Theory, InlineData("update", 403), InlineData("advisor", 403), InlineData("tenant", 401)]
    public async Task GatewayVehicleOwnershipReviewRequiresCurrentSelfUpdateAuthority(string failure, int status)
    {
        await using var h = await Harness.Create(); using var client = h.Client();
        var created = await (await client.PostAsJsonAsync("/product-api/v1/vehicle-records", Input(h.Source.FinancialFactId), ContractJson.Options))
            .Content.ReadFromJsonAsync<ReviewedVehicleRecord>(ContractJson.Options);
        h.Authority.Failure = failure;
        using var attempt = failure == "tenant" ? h.Client("T002") : h.Client();
        var response = await attempt.PostAsJsonAsync("/product-api/v1/vehicle-records/" + created!.VehicleRecordId + "/ownership",
            new ReviewVehicleOwnership(Guid.NewGuid().ToString("N"), 1, "OWNED", new(2026, 10, 2)), ContractJson.Options);
        Assert.Equal(status, (int)response.StatusCode); Assert.Equal(1, (await h.Records.ReadAsync("T001", Customer, created.VehicleRecordId, default))!.Revision);
    }
}
