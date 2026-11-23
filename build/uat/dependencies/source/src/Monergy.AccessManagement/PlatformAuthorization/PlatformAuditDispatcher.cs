using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Monergy.AccessManagement;

public sealed class PlatformAuditDispatcher(PlatformAuthorizationModule module, TimeProvider time, ILogger<PlatformAuditDispatcher> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> DeliveryUnavailable = LoggerMessage.Define(LogLevel.Warning,
        new EventId(71103, "PlatformReviewerAuditDeliveryUnavailable"),
        "Platform authorization audit delivery is unavailable; its durable outbox remains pending.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 16_384 };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (module.Enabled)
                {
                    var dependencies = module.GetDependencies();
                    // Bounded batch, one lease at a time; unavailable central Audit never rolls back owner state.
                    for (var count = 0; count < 10 && !stoppingToken.IsCancellationRequested; count++)
                    {
                        var delivery = await dependencies.Store.ClaimAsync(stoppingToken).ConfigureAwait(false);
                        if (delivery is null) break;
                        PlatformAuditReceipt? receipt = null;
                        try
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(dependencies.AuditRoute.Endpoint, "internal/platform-audit/v1/events"));
                            request.Headers.Add("X-Monergy-Platform-Audit", dependencies.AuditRoute.Token);
                            request.Content = new StringContent(delivery.Payload, System.Text.Encoding.UTF8, "application/json");
                            using var response = await client.SendAsync(request, stoppingToken).ConfigureAwait(false);
                            if (response.IsSuccessStatusCode)
                            {
                                var candidate = await response.Content.ReadFromJsonAsync<PlatformAuditReceipt>(PlatformReviewerContract.Json, stoppingToken).ConfigureAwait(false);
                                if (candidate is { State: "Accepted" } && candidate.EventId == delivery.EventId.ToString("D") &&
                                    PlatformReviewerContract.IsIdentifier(candidate.ReceiptId) && candidate.RecordedAt > DateTimeOffset.UnixEpoch &&
                                    candidate.RecordedAt <= time.GetUtcNow().AddSeconds(30)) receipt = candidate;
                            }
                        }
                        catch (Exception exception) when (exception is HttpRequestException or JsonException ||
                            exception is OperationCanceledException && !stoppingToken.IsCancellationRequested) { /* Keep pending; never log payload/credentials. */ }
                        await dependencies.Store.CompleteDeliveryAsync(delivery, receipt, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception) when (exception is PlatformAuthorizationException or NpgsqlException or System.Data.DataException or InvalidOperationException or FormatException ||
                exception is OperationCanceledException && !stoppingToken.IsCancellationRequested)
            { DeliveryUnavailable(logger, null); }
            await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken).ConfigureAwait(false);
        }
    }
}
