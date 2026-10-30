using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Xunit;
namespace Monergy.FinancialProfile.Tests;

public sealed partial class ReviewedInsuranceRecordTests
{
    [Fact]
    public async Task ExistingInsurancePayloadsRemainUnreviewedWithoutInventingALifecycleDate()
    {
        var (financial, source) = await Seed();
        var first = await new InsuranceRecordsApplication(financial, new InMemoryInsuranceRecords(), TimeProvider.System)
            .CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId), default);
        var legacy = JsonSerializer.Serialize(first, ContractJson.Options)
            .Replace(",\"lifecycleStatus\":\"UNREVIEWED\",\"lifecycleAsOfDate\":null", "", StringComparison.Ordinal);
        var restored = JsonSerializer.Deserialize<ReviewedInsuranceRecord>(legacy, ContractJson.Options);
        Assert.NotNull(restored); Assert.Equal("UNREVIEWED", restored.LifecycleStatus); Assert.Null(restored.LifecycleAsOfDate);
    }

    [Fact]
    public async Task CustomerCanCloseAndReopenAPolicyWhileEveryVersionAndSourceRemainImmutable()
    {
        var (financial, source) = await Seed(); var repository = new InMemoryInsuranceRecords();
        var app = new InsuranceRecordsApplication(financial, repository, TimeProvider.System);
        var first = await app.CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId), default);
        var close = new ReviewInsuranceRecordStatus(Guid.NewGuid().ToString("N"), 1, "CLOSED", new(2026, 10, 2));
        var closed = await app.ReviewStatusAsync("T001", Customer, "actor-001", first.InsuranceRecordId, close, default);
        Assert.Equal("CLOSED", closed.LifecycleStatus); Assert.Equal(close.AsOfDate, closed.LifecycleAsOfDate);
        Assert.Equal(closed, await app.ReviewStatusAsync("T001", Customer, "actor-001", first.InsuranceRecordId, close, default));

        var reopen = new ReviewInsuranceRecordStatus(Guid.NewGuid().ToString("N"), 2, "ACTIVE", new(2026, 10, 3));
        var active = await app.ReviewStatusAsync("T001", Customer, "actor-001", first.InsuranceRecordId, reopen, default);
        Assert.Equal("ACTIVE", active.LifecycleStatus); Assert.Equal(3, active.Revision);
        Assert.Equal(first.Insurance, active.Insurance); Assert.Equal(first.SourceProvenanceId, active.SourceProvenanceId);
        var history = await app.HistoryAsync("T001", Customer, first.InsuranceRecordId, null, default);
        Assert.Equal([3, 2, 1], history.Items.Select(x => x.Revision));
        Assert.Equal(["ACTIVE", "CLOSED", "UNREVIEWED"], history.Items.Select(x => x.LifecycleStatus));
    }

    [Theory]
    [InlineData("INVALID", "2026-10-02")]
    [InlineData("ACTIVE", "2026-09-30")]
    [InlineData("CLOSED", "2099-01-01")]
    public async Task InvalidLifecycleReviewDoesNotAppendARevision(string status, string date)
    {
        var (financial, source) = await Seed(); var repository = new InMemoryInsuranceRecords();
        var app = new InsuranceRecordsApplication(financial, repository, TimeProvider.System);
        var first = await app.CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId), default);
        var input = new ReviewInsuranceRecordStatus(Guid.NewGuid().ToString("N"), 1, status,
            DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(400, (await Assert.ThrowsAsync<TenantBoundaryException>(() =>
            app.ReviewStatusAsync("T001", Customer, "actor-001", first.InsuranceRecordId, input, default))).Status);
        Assert.Equal(first, await app.ReadAsync("T001", Customer, first.InsuranceRecordId, default));
    }

    [Fact]
    public async Task StaleLifecycleReviewLosesWithoutChangingTheWinningVersion()
    {
        var (financial, source) = await Seed(); var repository = new InMemoryInsuranceRecords();
        var app = new InsuranceRecordsApplication(financial, repository, TimeProvider.System);
        var first = await app.CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId), default);
        async Task<bool> Attempt(string status)
        {
            try { await app.ReviewStatusAsync("T001", Customer, "actor-001", first.InsuranceRecordId,
                new(Guid.NewGuid().ToString("N"), 1, status, new(2026, 10, 2)), default); return true; }
            catch (TenantBoundaryException error) when (error.Status == 409) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => Attempt("ACTIVE")), Task.Run(() => Attempt("CLOSED")));
        Assert.Single(results, x => x); Assert.Equal(2, (await app.ReadAsync("T001", Customer, first.InsuranceRecordId, default)).Revision);
    }

    [Fact]
    public async Task CorrectionsPreserveReviewedStatusAndCannotMovePolicyInformationPastClosure()
    {
        var (financial, source) = await Seed(); var repository = new InMemoryInsuranceRecords();
        var app = new InsuranceRecordsApplication(financial, repository, TimeProvider.System);
        var first = await app.CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId), default);
        var closed = await app.ReviewStatusAsync("T001", Customer, "actor-001", first.InsuranceRecordId,
            new(Guid.NewGuid().ToString("N"), 1, "CLOSED", new(2026, 10, 2)), default);
        var correction = new ReviseInsuranceRecord(Guid.NewGuid().ToString("N"), 2, closed.Insurance with { Provider = "Corrected provider" });
        var revised = await app.ReviseAsync("T001", Customer, "actor-001", first.InsuranceRecordId, correction, default);
        Assert.Equal("CLOSED", revised.LifecycleStatus); Assert.Equal(closed.LifecycleAsOfDate, revised.LifecycleAsOfDate);
        var invalid = correction with { OperationId = Guid.NewGuid().ToString("N"), ExpectedRevision = 3,
            Insurance = revised.Insurance with { PolicyAsOfDate = new(2026, 10, 3) } };
        Assert.Equal(400, (await Assert.ThrowsAsync<TenantBoundaryException>(() =>
            app.ReviseAsync("T001", Customer, "actor-001", first.InsuranceRecordId, invalid, default))).Status);
        Assert.Equal(revised, await app.ReadAsync("T001", Customer, first.InsuranceRecordId, default));
    }

    [Fact]
    public async Task GatewayLifecycleReviewIsAuthorizedReadBackAndIdempotent()
    {
        await using var h = await Harness.Create(); using var client = h.Client();
        var created = await (await client.PostAsJsonAsync("/product-api/v1/insurance-records", Input(h.Source.FinancialFactId), ContractJson.Options))
            .Content.ReadFromJsonAsync<ReviewedInsuranceRecord>(ContractJson.Options);
        Assert.NotNull(created);
        var input = new ReviewInsuranceRecordStatus(Guid.NewGuid().ToString("N"), 1, "CLOSED", new(2026, 10, 2));
        var path = "/product-api/v1/insurance-records/" + created.InsuranceRecordId + "/lifecycle";
        var response = await client.PostAsJsonAsync(path, input, ContractJson.Options); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var closed = await response.Content.ReadFromJsonAsync<ReviewedInsuranceRecord>(ContractJson.Options);
        Assert.Equal("CLOSED", closed!.LifecycleStatus); Assert.Equal(2, closed.Revision);
        Assert.Equal(closed, await (await client.PostAsJsonAsync(path, input, ContractJson.Options)).Content.ReadFromJsonAsync<ReviewedInsuranceRecord>(ContractJson.Options));
        Assert.Equal([2, 1], (await client.GetFromJsonAsync<InsuranceRecordHistory>(
            "/product-api/v1/insurance-records/" + created.InsuranceRecordId + "/history", ContractJson.Options))!.Items.Select(x => x.Revision));
    }

    [Theory, InlineData("update", 403), InlineData("advisor", 403), InlineData("tenant", 401)]
    public async Task GatewayLifecycleReviewRequiresCurrentSelfUpdateAuthority(string failure, int status)
    {
        await using var h = await Harness.Create(); using var client = h.Client();
        var created = await (await client.PostAsJsonAsync("/product-api/v1/insurance-records", Input(h.Source.FinancialFactId), ContractJson.Options))
            .Content.ReadFromJsonAsync<ReviewedInsuranceRecord>(ContractJson.Options);
        h.Authority.Failure = failure;
        using var attempt = failure == "tenant" ? h.Client("T002") : h.Client();
        var response = await attempt.PostAsJsonAsync("/product-api/v1/insurance-records/" + created!.InsuranceRecordId + "/lifecycle",
            new ReviewInsuranceRecordStatus(Guid.NewGuid().ToString("N"), 1, "ACTIVE", new(2026, 10, 2)), ContractJson.Options);
        Assert.Equal(status, (int)response.StatusCode); Assert.Equal(1, (await h.Records.ReadAsync("T001", Customer, created.InsuranceRecordId, default))!.Revision);
    }
}
