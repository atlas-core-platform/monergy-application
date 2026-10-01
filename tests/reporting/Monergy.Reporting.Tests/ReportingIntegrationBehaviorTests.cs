using System.Collections.Immutable;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Audit.Application;
using Monergy.Services.Audit.Infrastructure;
using Monergy.Services.Reporting.Application;
using Monergy.Services.Reporting.Domain;
using Monergy.Services.Reporting.Infrastructure;
using Xunit;

namespace Monergy.Reporting.Tests;

public sealed class ReportingIntegrationBehaviorTests
{
    [Fact]
    public async Task FreshKeyReflectsOwnerControlledSourceUpdateWhileOldReportRemainsUnchanged()
    {
        var context = new ReportingTestContext();
        var source = new MutableSourceReader(Snapshot(125000m, 1));
        var application = new ReportingApplication(source, context.Repository, context.Authorization, context.Clock);
        var first = (await application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a"), idempotencyKey: "before-update"))).Data!;

        source.Current = Snapshot(130000m, 2);
        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(1);
        var second = (await application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a"), idempotencyKey: "after-update"))).Data!;
        var reopened = (await application.GetAsync(context.Request(ReportingContractNames.GetReport,
            new GetReport("customer-a", first.ReportId)))).Data!;

        Assert.Equal(125000m, first.Items.Single().Value);
        Assert.Equal(130000m, second.Items.Single().Value);
        Assert.NotEqual(first.ReportId, second.ReportId);
        Assert.Equal(first, reopened);
        Assert.Equal(2, source.ReadCount);
    }

    [Fact]
    public async Task CrashAfterAuditCommitRedeliversWithoutSecondAuditEvidence()
    {
        var context = new ReportingTestContext();
        var report = (await context.Application.GenerateAsync(context.Request(
            ReportingContractNames.GenerateReport, new GenerateReport("customer-a"),
            idempotencyKey: "audit-crash-window"))).Data!;
        var auditRepository = new AppendOnlyInMemoryAuditRepository();
        var audit = new AuditApplication(auditRepository, new NoopTelemetry(), context.Clock);
        var delivery = new EventDeliveryTelemetry();
        var transport = new ReferenceGovernedEventTransport<AuditableEvent>(context.Configuration,
            [new AuditTransportConsumer(audit)], delivery);
        var dispatcher = new ReportingAuditOutboxDispatcher(context.Repository, transport, delivery)
        {
            FailAfterPublishBeforeMarkerOnce = true,
        };

        await Assert.ThrowsAsync<IOException>(() => dispatcher.DispatchAsync());
        Assert.Single(context.Repository.PendingEvents(), item => item.SubjectId == report.ReportId);
        Assert.Single(await audit.ReadAllAsync(), item => item.SubjectId == report.ReportId);

        Assert.Equal(1, await dispatcher.DispatchAsync());
        Assert.Empty(context.Repository.PendingEvents());
        Assert.Single(await audit.ReadAllAsync(), item => item.SubjectId == report.ReportId);
        Assert.Equal(1, delivery.Snapshot().DuplicateDeliveries);
    }

    private static ReportSourceSnapshot Snapshot(decimal value, int revision) => new("customer-a",
        new DateTimeOffset(2026, 10, 1, 0, revision, 0, TimeSpan.Zero),
        ImmutableArray.Create(new ReportLineItem("Synthetic income", value, "INR",
            new("FinancialFact", "fact-a", ReportingAuthority.FinancialProfileService,
                "evidence-a", $"provenance-a-{revision}", null))));

    private sealed class MutableSourceReader(ReportSourceSnapshot current) : IReportSourceReader
    {
        public ReportSourceSnapshot Current { get; set; } = current;
        public int ReadCount { get; private set; }

        public Task<ReportSourceSnapshot?> ReadAsync(ReportSourceReadContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult<ReportSourceSnapshot?>(
                Current.CustomerId == context.CustomerId ? Current : null);
        }
    }

    private sealed class NoopTelemetry : ILifecycleTelemetry
    {
        public void Record(LifecycleSignal signal) => ArgumentNullException.ThrowIfNull(signal);
    }
}
