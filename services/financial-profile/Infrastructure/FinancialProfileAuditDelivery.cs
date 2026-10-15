using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;

namespace Monergy.Services.FinancialProfile.Infrastructure;

public static class FinancialProfileAuditDeliveryOptions
{
    public static bool Selected(IConfiguration configuration) =>
        configuration.GetValue<bool>("Monergy:AuditDelivery:Enabled");

    public static Uri Endpoint(IConfiguration configuration)
    {
        var value = TenantAccessIntegration.Required(configuration, "Monergy:AuditDelivery:Url");
        return TenantAccessIntegration.LoopbackRoot(value);
    }
}

public sealed class HttpFinancialProfileAuditEventConsumer(
    HttpClient client,
    IConfiguration configuration,
    TenantBoundaryOptions tenants,
    IFinancialProfileRepository repository) : IGovernedEventConsumer<AuditableEvent>
{
    private const string Recipient = "Audit Service LOCAL/UAT composition";
    private readonly string token = RequiredToken(configuration);

    public async Task<bool> ConsumeAsync(AuditableEvent source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var fact = await repository.GetFactAsync(source.SubjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Financial Profile audit source fact is unavailable.");
        if (!tenants.CustomerTenants.TryGetValue(fact.CustomerId, out var tenantId))
            throw new InvalidOperationException("The Financial Profile audit source has no governed tenant binding.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "reference/events/financial-audit")
        {
            Content = JsonContent.Create(source, options: ContractJson.Options),
        };
        request.Headers.Add("X-Monergy-Reference-Transport", token);
        request.Headers.Add("X-Monergy-Reference-Tenant", tenantId);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"The Audit owner rejected Financial Profile event delivery with HTTP {(int)response.StatusCode}.");

        FinancialAuditReceipt receipt;
        try
        {
            receipt = await response.Content.ReadFromJsonAsync<FinancialAuditReceipt>(
                ContractJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new HttpRequestException("The Audit owner returned an empty Financial Profile receipt.");
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException("The Audit owner returned a malformed Financial Profile receipt.", exception);
        }

        if (receipt.Recipient != Recipient ||
            receipt.TenantId != tenantId ||
            receipt.SourceEventId != source.EventId ||
            receipt.SourceContractId != source.ContractId ||
            receipt.EventName != source.EventName ||
            receipt.EventVersion != source.EventVersion ||
            receipt.Producer != source.Producer ||
            receipt.SubjectType != source.SubjectType ||
            receipt.SubjectId != source.SubjectId ||
            string.IsNullOrWhiteSpace(receipt.AuditEvidenceId))
        {
            throw new HttpRequestException(
                "The Audit owner receipt does not match the submitted Financial Profile event.");
        }

        return receipt.Created;
    }

    private static string RequiredToken(IConfiguration configuration)
    {
        var value = TenantAccessIntegration.Required(configuration, "Monergy:AuditDelivery:Token");
        TenantAccessIntegration.ValidateToken(value);
        return value;
    }

    private sealed record FinancialAuditReceipt(
        string Recipient,
        string TenantId,
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

public sealed class FinancialProfileOutboxDispatcher(
    IFinancialProfileRepository repository,
    IGovernedEventTransport<AuditableEvent> transport,
    IEventDeliveryTelemetry telemetry,
    TimeProvider timeProvider)
{
    public async Task<int> DispatchAsync(CancellationToken cancellationToken = default)
    {
        var dispatched = 0;
        foreach (var message in await repository.PendingAuditEventsAsync(cancellationToken).ConfigureAwait(false))
        {
            telemetry.RecordDispatchAttempt();
            try
            {
                await transport.PublishAsync(message, cancellationToken).ConfigureAwait(false);
                await repository.MarkAuditEventDispatchedAsync(
                    message.EventId,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                dispatched++;
            }
            catch
            {
                telemetry.RecordDispatchFailure();
                throw;
            }
        }
        return dispatched;
    }
}

public sealed partial class FinancialProfileOutboxDispatchWorker : BackgroundService
{
    private readonly FinancialProfileOutboxDispatcher dispatcher;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<FinancialProfileOutboxDispatchWorker> logger;

    public FinancialProfileOutboxDispatchWorker(
        IConfiguration configuration,
        FinancialProfileOutboxDispatcher dispatcher,
        TimeProvider timeProvider,
        ILogger<FinancialProfileOutboxDispatchWorker> logger)
    {
        D10ReferenceTransportGuard.EnsureAllowed(configuration);
        this.dispatcher = dispatcher;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await dispatcher.DispatchAsync(stoppingToken).ConfigureAwait(false) == 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                LogDispatchCycleFailure(logger, error);
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Financial Profile audit outbox dispatch cycle failed closed.")]
    private static partial void LogDispatchCycleFailure(ILogger logger, Exception error);
}
