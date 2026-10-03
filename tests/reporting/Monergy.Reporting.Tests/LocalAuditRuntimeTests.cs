using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.LocalAuditHost;
using Monergy.Platform;
using Monergy.Services.Audit.Application;
using Monergy.Services.Audit.Infrastructure;
using Monergy.Services.Reporting.Application;
using Monergy.Services.Reporting.Infrastructure;
using Xunit;

namespace Monergy.Reporting.Tests;

public sealed class LocalAuditRuntimeTests
{
    [Fact]
    public async Task HostedDispatcherSurvivesTimeoutMalformedReceiptAndConnectionFailureThenRecovers()
    {
        var context = new ReportingTestContext();
        var report = await Generate(context, "worker-recovery");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new SequencedAuditHandler(report.ReportId, gate);
        using var client = new HttpClient(handler) { BaseAddress = new("http://audit/"), Timeout = TimeSpan.FromMilliseconds(40) };
        var telemetry = new EventDeliveryTelemetry();
        var transport = new LocalHttpAuditEventTransport(client, Configuration(), telemetry);
        var dispatcher = new ReportingAuditOutboxDispatcher(context.Repository, transport, telemetry);
        var worker = new ReportingOutboxHostedService(dispatcher, context.Repository, telemetry,
            context.Clock, Configuration());

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => telemetry.Snapshot().DispatchFailures >= 3, TimeSpan.FromSeconds(3));
        Assert.Single(context.Repository.PendingEvents());
        Assert.Equal("DEGRADED", worker.Snapshot().State);

        gate.SetResult();
        await WaitUntil(() => context.Repository.PendingEvents().Count == 0, TimeSpan.FromSeconds(3));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("DELIVERED", worker.Snapshot().State);
        Assert.True(telemetry.Snapshot().AuditIngestionFailures >= 3);
    }

    [Fact]
    public async Task HostShutdownDuringAuditRequestStopsCleanlyAndLeavesIntentPending()
    {
        var context = new ReportingTestContext();
        await Generate(context, "worker-stop");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new BlockingHandler(started))
        {
            BaseAddress = new("http://audit/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var telemetry = new EventDeliveryTelemetry();
        var transport = new LocalHttpAuditEventTransport(client, Configuration(), telemetry);
        var worker = new ReportingOutboxHostedService(
            new(context.Repository, transport, telemetry), context.Repository, telemetry,
            context.Clock, Configuration());

        await worker.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Single(context.Repository.PendingEvents());
    }

    [Theory]
    [InlineData("Other boundary", "report-generated-report-x", "CID-053", "ReportGenerated", "1.0.0", "Reporting Service", "Report", "report-x", "audit-x")]
    [InlineData("Audit Service LOCAL composition", "wrong-event", "CID-053", "ReportGenerated", "1.0.0", "Reporting Service", "Report", "report-x", "audit-x")]
    [InlineData("Audit Service LOCAL composition", "report-generated-report-x", "CID-053", "ReportGenerated", "1.0.0", "Wrong producer", "Report", "report-x", "audit-x")]
    [InlineData("Audit Service LOCAL composition", "report-generated-report-x", "CID-053", "ReportGenerated", "1.0.0", "Reporting Service", "Report", "report-x", "")]
    public async Task TransportRejectsIncompleteOrMismatchedReceipt(
        string recipient, string eventId, string contractId, string eventName, string eventVersion,
        string producer, string subjectType, string subjectId, string auditEvidenceId)
    {
        var receipt = new
        {
            recipient,
            sourceEventId = eventId,
            sourceContractId = contractId,
            eventName,
            eventVersion,
            producer,
            subjectType,
            subjectId,
            auditEvidenceId,
            created = true,
        };
        using var client = new HttpClient(new StaticJsonHandler(receipt)) { BaseAddress = new("http://audit/") };
        var telemetry = new EventDeliveryTelemetry();
        var transport = new LocalHttpAuditEventTransport(client, Configuration(), telemetry);

        var exception = await Assert.ThrowsAsync<LocalAuditDeliveryException>(() =>
            transport.PublishAsync(Event("report-x"), CancellationToken.None));

        Assert.Equal("reporting.audit-delivery.receipt-mismatch", exception.Code);
        Assert.Equal(1, telemetry.Snapshot().AuditIngestionFailures);
    }

    [Theory]
    [InlineData("CID-052", "ReportGenerated", "1.0.0", "Reporting Service", "Report")]
    [InlineData("CID-053", "WrongName", "1.0.0", "Reporting Service", "Report")]
    [InlineData("CID-053", "ReportGenerated", "9.0.0", "Reporting Service", "Report")]
    [InlineData("CID-053", "ReportGenerated", "1.0.0", "Wrong producer", "Report")]
    [InlineData("CID-053", "ReportGenerated", "1.0.0", "Reporting Service", "Other")]
    public void LocalAuditIntakeRejectsUnsupportedReportingEnvelope(
        string contractId, string eventName, string eventVersion, string producer, string subjectType)
    {
        var candidate = Event("report-x") with
        {
            ContractId = contractId,
            EventName = eventName,
            EventVersion = eventVersion,
            Producer = producer,
            SubjectType = subjectType,
        };
        Assert.False(LocalAuditProtocol.IsValidReportingEvent(candidate));
    }

    [Fact]
    public async Task LostReceiptRedeliveryReturnsSameEventBoundAuditEffect()
    {
        var repository = new AppendOnlyInMemoryAuditRepository();
        var audit = new AuditApplication(repository, new NoopTelemetry(), TimeProvider.System);
        var handler = new CommitThenLoseReceiptHandler(audit);
        using var client = new HttpClient(handler) { BaseAddress = new("http://audit/") };
        var telemetry = new EventDeliveryTelemetry();
        var transport = new LocalHttpAuditEventTransport(client, Configuration(), telemetry);
        var source = Event("report-x");

        await Assert.ThrowsAsync<LocalAuditDeliveryException>(() =>
            transport.PublishAsync(source, CancellationToken.None));
        var replay = await transport.PublishAsync(source, CancellationToken.None);

        Assert.Equal(1, replay.Duplicates);
        var evidence = await audit.ReadAllAsync();
        Assert.Single(evidence, value => value.SourceEventId == source.EventId &&
            value.SubjectId == source.SubjectId && value.SourceContractId == source.ContractId);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Monergy:ExecutionZone"] = "LOCAL",
            ["Monergy:D11:LocalTransportToken"] = "test-token",
            ["Monergy:D11:AuditDispatchIntervalMilliseconds"] = "10",
        }).Build();

    private static AuditableEvent Event(string reportId) => new("CID-053", $"report-generated-{reportId}",
        ReportingContractNames.ReportGenerated, ContractGuard.CurrentVersion,
        new(2026, 10, 1, 1, 0, 0, TimeSpan.Zero), "correlation-x", "request-x",
        "Reporting Service", "Report", reportId);

    private static async Task<TrustedFinancialReport> Generate(ReportingTestContext context, string key) =>
        (await context.Application.GenerateAsync(context.Request(ReportingContractNames.GenerateReport,
            new GenerateReport("customer-a"), idempotencyKey: key))).Data!;

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class SequencedAuditHandler(string reportId, TaskCompletionSource gate) : HttpMessageHandler
    {
        private int attempt;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref attempt);
            if (current == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
            if (current == 2) return new(HttpStatusCode.OK) { Content = new StringContent("{", Encoding.UTF8, "application/json") };
            if (current == 3) throw new HttpRequestException("connection unavailable");
            await gate.Task.WaitAsync(cancellationToken);
            return StaticJsonHandler.Response(Receipt(Event(reportId), true));
        }
    }

    private sealed class BlockingHandler(TaskCompletionSource started) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class StaticJsonHandler(object value) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(Response(value));

        public static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value, options: ContractJson.Options),
        };
    }

    private sealed class CommitThenLoseReceiptHandler(AuditApplication audit) : HttpMessageHandler
    {
        private int attempt;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var source = await request.Content!.ReadFromJsonAsync<AuditableEvent>(ContractJson.Options,
                cancellationToken) ?? throw new InvalidOperationException("Missing source.");
            var result = await audit.ConsumeWithDispositionAsync(source, cancellationToken);
            if (Interlocked.Increment(ref attempt) == 1) throw new HttpRequestException("receipt lost");
            return StaticJsonHandler.Response(Receipt(source, result.Created));
        }
    }

    private static object Receipt(AuditableEvent source, bool created) => new
    {
        recipient = LocalAuditProtocol.Recipient,
        sourceEventId = source.EventId,
        sourceContractId = source.ContractId,
        source.EventName,
        source.EventVersion,
        source.Producer,
        source.SubjectType,
        source.SubjectId,
        auditEvidenceId = $"audit-{source.EventId}",
        created,
    };

    private sealed class NoopTelemetry : ILifecycleTelemetry
    {
        public void Record(LifecycleSignal signal) => ArgumentNullException.ThrowIfNull(signal);
    }
}
