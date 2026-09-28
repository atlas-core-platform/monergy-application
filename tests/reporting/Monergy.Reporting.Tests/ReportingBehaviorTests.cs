using Monergy.Contracts;
using Xunit;

namespace Monergy.Reporting.Tests;

public sealed class ReportingBehaviorTests
{
    [Fact]
    public async Task GeneratesBasicReportFromAuthoritativeSourceReferences()
    {
        var context = new ReportingTestContext();
        var result = await context.Application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a")));
        Assert.Equal(ContractOutcome.Success, result.Outcome);
        Assert.Equal(2, result.Data!.Items.Length);
        Assert.Contains(result.Data.Items, item => item.Source.AuthoritativeOwner == "Financial Profile Service");
        Assert.Contains(result.Data.Items, item => item.Source.AuthoritativeOwner == "Financial Rules Service");
    }

    [Fact]
    public async Task PreservesEvidenceProvenanceAndCalculationLineage()
    {
        var context = new ReportingTestContext();
        var report = (await context.Application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a")))).Data!;
        Assert.Collection(report.EvidenceReferences, value => Assert.Equal("evidence-customer-a", value));
        Assert.Collection(report.FinancialProvenanceReferences, value => Assert.Equal("provenance-customer-a", value));
        Assert.Collection(report.CalculationLineageReferences, value => Assert.Equal("lineage-customer-a", value));
        Assert.Null(report.AiResponseTraceReference);
    }

    [Fact]
    public async Task SameIdempotencyIdentityReplaysCommittedReportWithoutRegeneration()
    {
        var context = new ReportingTestContext();
        var first = (await context.Application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a"), idempotencyKey: "same"))).Data!;
        context.Clock.UtcNow = context.Clock.UtcNow.AddHours(1);
        var second = (await context.Application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a"), idempotencyKey: "same"))).Data!;
        Assert.Equal(first.ReportId, second.ReportId);
        Assert.Equal(first.GeneratedAt, second.GeneratedAt);
        Assert.Equal(first.Export, second.Export);
        Assert.Equal("application/json", first.Export.MediaType);
        Assert.Equal(1, context.Source.ReadCount);
        Assert.Single(context.Evidence.Events);
    }

    [Fact]
    public async Task NewIdempotencyKeyCreatesANewGenerationOperation()
    {
        var context = new ReportingTestContext();
        var first = (await context.Application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a"), idempotencyKey: "first"))).Data!;
        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(5);
        var second = (await context.Application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a"), idempotencyKey: "second"))).Data!;
        Assert.NotEqual(first.ReportId, second.ReportId);
        Assert.NotEqual(first.GeneratedAt, second.GeneratedAt);
        Assert.Equal(2, context.Source.ReadCount);
        Assert.Equal(2, context.Evidence.Events.Count);
    }

    [Fact]
    public async Task GeneratedAndEventTimesUseTheCommittedGenerationOccurrence()
    {
        var context = new ReportingTestContext();
        var report = (await context.Application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a")))).Data!;
        var generated = Assert.Single(context.Evidence.Events);
        Assert.Equal(context.Clock.UtcNow, report.GeneratedAt);
        Assert.Equal(report.GeneratedAt, generated.OccurredAt);
        Assert.NotEqual(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), report.GeneratedAt);
    }

    [Fact]
    public async Task ConcurrentReplayCommitsOnlyOneReportAndEvent()
    {
        var context = new ReportingTestContext();
        var requests = Enumerable.Range(0, 4).Select(_ => context.Application.GenerateAsync(
            context.Request(ReportingContractNames.GenerateReport, new GenerateReport("customer-a"),
                idempotencyKey: "concurrent")));
        var results = await Task.WhenAll(requests);
        Assert.All(results, result => Assert.Equal(results[0].Data, result.Data));
        Assert.Equal(1, context.Source.ReadCount);
        Assert.Single(context.Evidence.Events);
    }

    [Fact]
    public async Task EmitsReportGeneratedWithAuditCompatibilityEvidence()
    {
        var context = new ReportingTestContext();
        var report = (await context.Application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a")))).Data!;
        var generated = Assert.Single(context.Evidence.Events);
        Assert.Equal("CID-053", generated.ContractId);
        Assert.Equal(report.AuditCompatibilityReferenceId, generated.Payload.AuditCompatibilityReferenceId);
        Assert.Equal(report.Export.Sha256, generated.Payload.ExportSha256);
        Assert.Equal(report.GeneratedAt, generated.OccurredAt);
    }

    [Fact]
    public async Task CustomerCannotGenerateAgainstAnotherCustomerBoundary()
    {
        var context = new ReportingTestContext();
        var result = await context.Application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-b"), context.CustomerA));
        Assert.Equal(ContractOutcome.Rejected, result.Outcome);
        Assert.Equal(ContractErrorCategory.AccessDenied, result.Error!.Category);
        Assert.Empty(context.Evidence.Events);
    }

    [Fact]
    public async Task CustomerCannotReadAnotherCustomersReport()
    {
        var context = new ReportingTestContext();
        var report = (await context.Application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-b"), context.CustomerB))).Data!;
        var result = await context.Application.GetAsync(context.Request(ReportingContractNames.GetReport,
            new GetReport("customer-a", report.ReportId), context.CustomerA));
        Assert.Equal(ContractOutcome.Rejected, result.Outcome);
        Assert.Equal(ContractErrorCategory.NotFound, result.Error!.Category);
    }

    [Fact]
    public async Task RequiresCurrentAuthorizedContext()
    {
        var context = new ReportingTestContext();
        var unauthorized = context.CustomerA with
        {
            Access = context.CustomerA.Access with { AuthorizationContextId = "unknown" },
        };
        var result = await context.Application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a"), unauthorized));
        Assert.Equal("report.authorization.denied", result.Error!.Code);
    }

    [Fact]
    public async Task RequiresIdempotencyKeyForGeneration()
    {
        var context = new ReportingTestContext();
        var result = await context.Application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a"), idempotencyKey: null));
        Assert.Equal("report.idempotency.required", result.Error!.Code);
    }

    [Theory]
    [InlineData("QA")]
    [InlineData("UAT")]
    [InlineData("PRODUCTION")]
    public void ReferenceAdaptersFailClosedOutsidePermittedZones(string zone)
    {
        var configuration = ReportingTestContext.BuildConfiguration(zone);
        Assert.Throws<InvalidOperationException>(() => new Monergy.Services.Reporting.Infrastructure.ReferenceReportSourceReader(configuration));
    }

    [Fact]
    public void ContractCatalogContainsOnlyCanonicalReportingSurface()
    {
        Assert.Collection(D08ContractCatalog.Contracts,
            item => Assert.Equal("CID-051", item.ContractId),
            item => Assert.Equal("CID-052", item.ContractId),
            item => Assert.Equal("CID-053", item.ContractId));
        Assert.DoesNotContain(D08ContractCatalog.Contracts, item => item.ContractId == "CID-054");
        Assert.All(D08ContractCatalog.Contracts, item => Assert.Equal("Reporting Service", item.Owner));
    }
}
