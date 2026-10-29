using Monergy.Contracts;
using Monergy.Services.Reporting.Application;
using Monergy.Services.Reporting.Infrastructure;
using Xunit;

namespace Monergy.Reporting.Tests;

public sealed class ReportGenerationJobTests
{
    private const string OperationId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task SchedulingIsIdempotentAndDefersOwnerReadsUntilExecution()
    {
        var context = new ReportingTestContext();
        var intents = new InMemoryReportGenerationIntentRepository();
        var jobs = new ReportGenerationJobs(intents, new ReferenceReportJobScheduler(context.Clock),
            context.Application, context.Authorization, context.Clock);
        var request = context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a"), idempotencyKey: OperationId);

        var first = await jobs.ScheduleAsync(request);
        var replay = await jobs.ScheduleAsync(request);

        Assert.Equal(ContractOutcome.Success, first.Outcome);
        Assert.Equal(first.Data, replay.Data);
        Assert.Equal(ReportingIdentity.JobId(OperationId), first.Data?.JobId);
        Assert.Equal(ReportingIdentity.ReportId(ContractGuard.CurrentVersion, "customer-a", OperationId),
            first.Data?.ReportId);
        Assert.Equal(JobState.Scheduled, first.Data?.State);
        Assert.Equal(0, context.Source.ReadCount);

        var generated = await jobs.ExecuteAsync(OperationId, ReportingIdentity.JobId(OperationId),
            context.CustomerA);
        Assert.Equal(ContractOutcome.Success, generated.Outcome);
        Assert.Equal(first.Data?.ReportId, generated.Data?.ReportId);
        Assert.Equal(1, context.Source.ReadCount);
    }

    [Fact]
    public async Task ReplayWithChangedReceiptSelectionConflictsWithoutReplacingIntent()
    {
        var context = new ReportingTestContext();
        var intents = new InMemoryReportGenerationIntentRepository();
        var jobs = new ReportGenerationJobs(intents, new ReferenceReportJobScheduler(context.Clock),
            context.Application, context.Authorization, context.Clock);
        var first = new FinancialReviewReceiptSelection("allocation-1", "rate-1", "share-1", "coverage-1");
        var changed = first with { MonthlyDebtServiceCoverageCalculationId = "coverage-2" };

        _ = await jobs.ScheduleAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a", first), idempotencyKey: OperationId));
        var conflict = await jobs.ScheduleAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a", changed), idempotencyKey: OperationId));

        Assert.Equal(ContractErrorCategory.Conflict, conflict.Error?.Category);
        Assert.Equal(first, (await intents.GetAsync(OperationId, default))?.FinancialReview);
        Assert.Equal(0, context.Source.ReadCount);
    }

    [Fact]
    public async Task ExecutionRechecksCurrentAuthorityAndKeepsCustomerAndActorBoundary()
    {
        var context = new ReportingTestContext();
        var jobs = new ReportGenerationJobs(new InMemoryReportGenerationIntentRepository(),
            new ReferenceReportJobScheduler(context.Clock), context.Application, context.Authorization, context.Clock);
        _ = await jobs.ScheduleAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a"), idempotencyKey: OperationId));
        var stale = context.CustomerA with
        {
            Access = context.CustomerA.Access with { AuthorizationContextId = "withdrawn" },
        };

        var denied = await jobs.ExecuteAsync(OperationId, ReportingIdentity.JobId(OperationId), stale);
        var otherCustomer = await jobs.ExecuteAsync(OperationId, ReportingIdentity.JobId(OperationId),
            context.CustomerB);

        Assert.Equal(ContractErrorCategory.AccessDenied, denied.Error?.Category);
        Assert.Equal(ContractErrorCategory.NotFound, otherCustomer.Error?.Category);
        Assert.Equal(0, context.Source.ReadCount);
    }
}
