using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Monergy.Platform;

namespace Monergy.UatGateway;

internal sealed class AssistantFeedbackClient(IConfiguration configuration) : IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
    { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 98304 };
    public async Task<T> SendAsync<T>(string path, object body, bool review, CancellationToken ct)
    {
        if ((configuration["Monergy:Assistant:FeedbackUrl"] ?? "http://127.0.0.1:5119/").TrimEnd('/') != "http://127.0.0.1:5119")
            throw new InvalidOperationException("Feedback destination invalid.");
        var token = configuration["Monergy:Assistant:" + (review ? "FeedbackReviewToken" : "FeedbackIngestToken")];
        if (token is not { Length: >= 32 }) throw new InvalidOperationException("Feedback dependency unavailable.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:5119/internal/assistant-feedback/v1/" + path);
        request.Headers.Add("X-Monergy-Assistant-Feedback", token);
        request.Content = JsonContent.Create(body, options: AssistantContract.Json);
        var mutation = path is "topics" or "review/change" or "review/delivery/retry" or "review/audit-deliveries/retry";
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                if (mutation)
                {
                    using var failure = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
                    // An HTTP status alone cannot establish that a dispatched mutation
                    // did not commit. Only the owner's complete, unambiguous rejection can.
                    AssistantKnowledgeCatalogue.RequireFields(failure.RootElement, "error", "outcome", "saved");
                    if (failure.RootElement.GetProperty("error").GetString() is not { } error || !AssistantContract.Code(error) ||
                        failure.RootElement.GetProperty("outcome").GetString() != "REJECTED" ||
                        failure.RootElement.GetProperty("saved").ValueKind != JsonValueKind.False)
                        throw new AssistantOutcomeUnknownException();
                }
                // Reviewer entitlement failures do not revoke an otherwise valid platform login.
                throw new TenantBoundaryException(status == 403 ? "ASSISTANT_REVIEW_REQUIRED" : status == 409 ? "ASSISTANT_FEEDBACK_CONFLICT" : "ASSISTANT_FEEDBACK_UNAVAILABLE",
                    status is 400 or 403 or 404 or 409 or 429 ? status : 503);
            }
            using var success = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false),
                new JsonDocumentOptions { MaxDepth = AssistantContract.Json.MaxDepth });
            RequireUniqueFields(success.RootElement);
            return success.RootElement.Deserialize<T>(AssistantContract.Json) ?? throw new JsonException();
        }
        catch (Exception failure) when (mutation && failure is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException or NotSupportedException)
        { throw new AssistantOutcomeUnknownException(); }
    }
    private static void RequireUniqueFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                RequireUniqueFields(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RequireUniqueFields(item);
    }
    public void Dispose() => client.Dispose();
}

internal sealed class AssistantCounterQueue(IConfiguration configuration, ILogger<AssistantCounterQueue> logger) : BackgroundService
{
    private readonly Channel<AnonymousAssistantCounter> queue = Channel.CreateBounded<AnonymousAssistantCounter>(new BoundedChannelOptions(128)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private long dropped;
    private static readonly Action<ILogger, long, Exception?> Dropped = LoggerMessage.Define<long>(LogLevel.Warning,
        new EventId(71102, "AssistantCounterDropped"), "Assistant anonymous counter delivery dropped {Count} counters.");
    public void Enqueue(AnonymousAssistantCounter counter) { if (!queue.Writer.TryWrite(counter)) Interlocked.Increment(ref dropped); }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
        { Timeout = TimeSpan.FromMilliseconds(500), MaxResponseContentBufferSize = 2048 };
        await foreach (var counter in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var token = configuration["Monergy:Assistant:FeedbackIngestToken"];
                if (token is not { Length: >= 32 }) throw new InvalidOperationException();
                using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:5119/internal/assistant-feedback/v1/counters");
                request.Headers.Add("X-Monergy-Assistant-Feedback", token);
                request.Content = JsonContent.Create(counter, options: AssistantContract.Json);
                using var response = await client.SendAsync(request, stoppingToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) Interlocked.Increment(ref dropped);
            }
            catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException or InvalidOperationException) { Interlocked.Increment(ref dropped); }
            var count = Interlocked.Exchange(ref dropped, 0);
            if (count > 0) Dropped(logger, count, null);
        }
    }
}

internal sealed class AssistantRateLimiter
{
    private readonly object gate = new();
    private readonly Dictionary<string, Window> windows = new(StringComparer.Ordinal);
    public void Require(HttpContext http, string principal, string purpose, int limit, TimeSpan duration, string? operation = null)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expiredKey in windows.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) windows.Remove(expiredKey);
            var key = purpose + ":" + AssistantContract.Hash(principal);
            if (!windows.TryGetValue(key, out var window))
            {
                if (windows.Count >= 4096) throw new TenantBoundaryException("ASSISTANT_RATE_CAPACITY", 503);
                window = new(now.Add(duration)); windows.Add(key, window);
            }
            if (operation is not null && window.Operations.Contains(operation)) return;
            if (window.Count >= limit)
            {
                http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((window.ExpiresAt - now).TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                throw new TenantBoundaryException("ASSISTANT_RATE_LIMITED", 429);
            }
            window.Count++;
            if (operation is not null) window.Operations.Add(operation);
        }
    }
    private sealed class Window(DateTimeOffset expiresAt)
    {
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public int Count { get; set; }
        public HashSet<string> Operations { get; } = new(StringComparer.Ordinal);
    }
}
