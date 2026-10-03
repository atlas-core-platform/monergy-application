using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Reporting.Application;

namespace Monergy.Services.Reporting.Infrastructure;

public static class D11LocalSecurityContexts
{
    public static IEnumerable<TrustedSecurityContext> BrowserContexts(IConfiguration configuration) =>
        Customers(configuration).Select(customer => Context(customer, "customer-web", "d11-customer-web"));

    public static IEnumerable<TrustedSecurityContext> ReportingContexts(IConfiguration configuration) =>
        Customers(configuration).Select(customer => Context(customer, "reporting",
            configuration["Monergy:D11:ReportingWorkloadIdentityId"] ?? "d11-reporting-workload"));

    public static IEnumerable<TrustedSecurityContext> SeederContexts(IConfiguration configuration) =>
        Customers(configuration).Select(customer => Context(customer, "local-fixture-seeder", "d11-local-fixture-seeder"));

    private static IEnumerable<string> Customers(IConfiguration configuration) =>
        (configuration["Monergy:D11:SyntheticCustomers"] ?? "d11-customer-a,d11-customer-b")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal);

    private static TrustedSecurityContext Context(string customerId, string workloadId, string workloadIdentityId) =>
        new(new ActorContext($"d11-synthetic-actor-{customerId}", "SYNTHETIC_HUMAN",
                DateTimeOffset.UnixEpoch, $"d11-authentication-{customerId}"),
            new WorkloadContext(workloadId, workloadIdentityId),
            new AccessContext("D11_PERSISTED_REPORTING", $"d11-consent-{customerId}",
                $"d11-{workloadId}-{customerId}-authorization", customerId));
}

public sealed class LocalHttpAuditEventTransport(
    HttpClient client,
    IConfiguration configuration,
    IEventDeliveryTelemetry telemetry)
    : IGovernedEventTransport<AuditableEvent>
{
    private const string ExpectedRecipient = "Audit Service LOCAL composition";
    private readonly string token = PhysicalPersistenceGuard.Require(configuration, "Monergy:D11:LocalTransportToken");

    public async Task<EventDeliveryResult> PublishAsync(AuditableEvent source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var request = new HttpRequestMessage(HttpMethod.Post, "contracts/cid-061/v1")
        {
            Content = JsonContent.Create(source, options: ContractJson.Options),
        };
        request.Headers.Add("X-Monergy-Local-Transport", token);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw Failure("reporting.audit-delivery.http-rejected",
                    $"The LOCAL Audit ingestion boundary returned HTTP {(int)response.StatusCode}.");
            LocalAuditIngestionResponse result;
            try
            {
                result = await response.Content.ReadFromJsonAsync<LocalAuditIngestionResponse>(
                    ContractJson.Options, cancellationToken).ConfigureAwait(false)
                    ?? throw Failure("reporting.audit-delivery.receipt-invalid",
                        "The LOCAL Audit ingestion boundary returned an empty receipt.");
            }
            catch (JsonException exception)
            {
                throw Failure("reporting.audit-delivery.receipt-malformed",
                    "The LOCAL Audit ingestion boundary returned malformed receipt data.", exception);
            }

            if (!ValidReceipt(source, result))
                throw Failure("reporting.audit-delivery.receipt-mismatch",
                    "The LOCAL Audit receipt does not acknowledge the submitted source event and Audit boundary.");
            var duplicates = result.Created ? 0 : 1;
            telemetry.RecordDuplicateDeliveries(duplicates);
            return new(1, result.Created ? 1 : 0, duplicates);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            telemetry.RecordAuditIngestionFailure();
            throw Failure("reporting.audit-delivery.timeout", "The LOCAL Audit ingestion request timed out.", exception);
        }
        catch (LocalAuditDeliveryException)
        {
            telemetry.RecordAuditIngestionFailure();
            throw;
        }
        catch (HttpRequestException exception)
        {
            telemetry.RecordAuditIngestionFailure();
            throw Failure("reporting.audit-delivery.transport-unavailable",
                "The LOCAL Audit ingestion boundary is unavailable.", exception);
        }
    }

    private static bool ValidReceipt(AuditableEvent source, LocalAuditIngestionResponse receipt) =>
        receipt.Recipient == ExpectedRecipient &&
        receipt.SourceEventId == source.EventId &&
        receipt.SourceContractId == source.ContractId &&
        receipt.EventName == source.EventName &&
        receipt.EventVersion == source.EventVersion &&
        receipt.Producer == source.Producer &&
        receipt.SubjectType == source.SubjectType &&
        receipt.SubjectId == source.SubjectId &&
        !string.IsNullOrWhiteSpace(receipt.AuditEvidenceId);

    private static LocalAuditDeliveryException Failure(string code, string message, Exception? inner = null) =>
        new(code, message, inner);

    private sealed record LocalAuditIngestionResponse(
        string Recipient,
        string SourceEventId,
        string SourceContractId,
        string EventName,
        string EventVersion,
        string Producer,
        string SubjectType,
        string SubjectId,
        string AuditEvidenceId,
        bool Created);
}

public sealed class LocalAuditDeliveryException(string code, string message, Exception? innerException = null)
    : HttpRequestException(message, innerException)
{
    public string Code { get; } = code;
}

public sealed record ReportingDispatchSnapshot(
    string State,
    int PendingEvents,
    int DispatchAttempts,
    int DispatchFailures,
    int DuplicateDeliveries,
    int AuditIngestionFailures,
    DateTimeOffset? LastSuccessfulDispatchAt,
    string? LastFailureCode);

public interface IReportingDispatchStatus
{
    ReportingDispatchSnapshot Snapshot();
}

public sealed class ReportingOutboxHostedService(
    ReportingAuditOutboxDispatcher dispatcher,
    IReportRepository repository,
    IEventDeliveryTelemetry telemetry,
    TimeProvider timeProvider,
    IConfiguration configuration) : BackgroundService, IReportingDispatchStatus
{
    private const string AuditDeliveryUnavailable = "audit" + ".delivery.unavailable";
    private readonly object sync = new();
    private DateTimeOffset? lastSuccessfulDispatchAt;
    private string? lastFailureCode;
    private readonly TimeSpan interval = TimeSpan.FromMilliseconds(
        int.TryParse(configuration["Monergy:D11:AuditDispatchIntervalMilliseconds"], out var configured)
            ? Math.Clamp(configured, 10, 60_000)
            : 1000);

    public ReportingDispatchSnapshot Snapshot()
    {
        var delivery = telemetry.Snapshot();
        lock (sync)
        {
            return CreateSnapshot(repository.PendingEvents().Count,
                delivery, lastSuccessfulDispatchAt, lastFailureCode);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatched = await dispatcher.DispatchAsync(stoppingToken).ConfigureAwait(false);
                lock (sync)
                {
                    if (dispatched > 0) lastSuccessfulDispatchAt = timeProvider.GetUtcNow();
                    lastFailureCode = null;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (LocalAuditDeliveryException exception)
            {
                lock (sync) lastFailureCode = exception.Code;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                lock (sync) lastFailureCode = AuditDeliveryUnavailable;
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static ReportingDispatchSnapshot CreateSnapshot(int pending, EventDeliveryTelemetrySnapshot delivery,
        DateTimeOffset? success, string? failure) => new(
            pending == 0 && failure is null ? "DELIVERED" : failure is null ? "PENDING" : "DEGRADED",
            pending, delivery.DispatchAttempts, delivery.DispatchFailures, delivery.DuplicateDeliveries,
            delivery.AuditIngestionFailures, success, failure);
}
