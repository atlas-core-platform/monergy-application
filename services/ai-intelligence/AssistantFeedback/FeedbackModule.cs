using System.Text.Json;
using Monergy.Platform;

namespace Monergy.Services.AiIntelligence.AssistantFeedback;

public static class FeedbackModule
{
    public static void AddAssistantFeedback(this WebApplicationBuilder builder)
    {
        // Optional modules do not eagerly open connections or contribute to existing business health checks.
        builder.Services.AddSingleton<FeedbackStore>();
        builder.Services.AddSingleton<FeedbackAuthority>();
        builder.Services.AddSingleton(provider =>
        {
            var config = provider.GetRequiredService<IConfiguration>();
            return new AssistantKnowledgeCatalogue(config["Monergy:AssistantFeedback:CataloguePath"] ?? "",
                config["Monergy:AssistantFeedback:CatalogueSha256"] ?? "");
        });
        builder.Services.AddHostedService<FeedbackMaintenance>();
    }

    public static void MapAssistantFeedback(this WebApplication app)
    {
        var group = app.MapGroup("/internal/assistant-feedback/v1");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                if (!string.Equals(app.Configuration["Monergy:AssistantFeedback:Enabled"], "true", StringComparison.OrdinalIgnoreCase))
                    return Results.Json(new { error = "FEEDBACK_UNAVAILABLE", saved = false }, statusCode: 503);
                var review = http.Request.Path.StartsWithSegments("/internal/assistant-feedback/v1/review");
                var purpose = review ? "ReviewToken" : "IngestToken";
                var token = app.Configuration["Monergy:AssistantFeedback:" + purpose];
                if (token == app.Configuration["Monergy:AssistantFeedback:" + (review ? "IngestToken" : "ReviewToken")])
                    throw new InvalidOperationException("Feedback workload purposes must be separate.");
                AssistantContract.RequireWorkload(http, "X-Monergy-Assistant-Feedback", token);
                return await next(context).ConfigureAwait(false);
            }
            catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code, saved = false }, statusCode: failure.Status); }
            catch (Exception failure) when (failure is System.Data.Common.DbException or System.Data.DataException or InvalidOperationException or IOException or JsonException or HttpRequestException or OperationCanceledException)
            { return Results.Json(new { error = "FEEDBACK_UNAVAILABLE", saved = false }, statusCode: 503); }
        });
        group.MapGet("/status", async (FeedbackStore store, CancellationToken ct) =>
            await store.ReadyAsync(ct).ConfigureAwait(false) ? Results.Ok(new { available = true }) : Results.Json(new { available = false }, statusCode: 503));
        group.MapPost("/topics", async (HttpContext http, FeedbackStore store, IServiceProvider services) =>
        {
            var request = await AssistantContract.ReadAsync<HelpTopicSubmission>(http.Request, 4096, http.RequestAborted).ConfigureAwait(false);
            FeedbackRules.Submission(request, services.GetRequiredService<AssistantKnowledgeCatalogue>());
            return Results.Ok(await store.SubmitAsync(request, http.RequestAborted).ConfigureAwait(false));
        });
        group.MapPost("/counters", async (HttpContext http, FeedbackStore store, IServiceProvider services) =>
        {
            var request = await AssistantContract.ReadAsync<FeedbackCounter>(http.Request, 2048, http.RequestAborted).ConfigureAwait(false);
            var catalogue = services.GetRequiredService<AssistantKnowledgeCatalogue>();
            string[] categories = ["ANSWERED", "UNKNOWN", "INSUFFICIENT_GROUNDING", "UNRESOLVED", "NOT_IMPLEMENTED", "UNSUPPORTED", "CONFLICT", "DEPENDENCY_UNAVAILABLE"];
            if (request.ContractVersion != "1.0.0" || request.SourceAudience is not ("product" or "platform") ||
                !categories.Contains(request.Category, StringComparer.Ordinal) || request.KnowledgeRevision != catalogue.KnowledgeRevision ||
                request.RetrievalRevision != catalogue.RetrievalRevision || request.Intent != "UNCLASSIFIED" && !catalogue.TopicAllowed(request.SourceAudience, request.Intent))
                throw new TenantBoundaryException("FEEDBACK_COUNTER_INVALID", 400);
            await store.CountAsync(request, http.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        });
        group.MapPost("/review/status", async (HttpContext http, FeedbackAuthority authority, FeedbackStore store) =>
        {
            var request = await AssistantContract.ReadAsync<PlatformContextValidation>(http.Request, 2048, http.RequestAborted).ConfigureAwait(false);
            if (request.Operation != "list" || request.ResourceId is not null) throw new TenantBoundaryException("FEEDBACK_CONTEXT_INVALID", 400);
            await authority.RequireAsync(request.AuthenticationContext, "list", null, http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new { reviewAllowed = await store.ReadyAsync(http.RequestAborted).ConfigureAwait(false) });
        });
        group.MapPost("/review/topics", async (HttpContext http, FeedbackStore store, FeedbackAuthority authority) =>
        {
            var request = await AssistantContract.ReadAsync<FeedbackReviewRequest>(http.Request, 4096, http.RequestAborted).ConfigureAwait(false);
            if (request.Change is not null || request.OperationId is not null || request.GapId is not null && !AssistantContract.OpaqueId(request.GapId))
                throw new TenantBoundaryException("FEEDBACK_QUERY_INVALID", 400);
            return Results.Ok(await store.ReadAsync(request, authority, http.RequestAborted).ConfigureAwait(false));
        });
        group.MapPost("/review/change", async (HttpContext http, FeedbackStore store, FeedbackAuthority authority) =>
        {
            var request = await AssistantContract.ReadAsync<FeedbackReviewRequest>(http.Request, 8192, http.RequestAborted).ConfigureAwait(false);
            if (request.Cursor is not null || request.Status is not null || request.SourceAudience is not null)
                throw new TenantBoundaryException("FEEDBACK_REVIEW_INVALID", 400);
            return Results.Ok(await store.ChangeAsync(request, authority, http.RequestAborted).ConfigureAwait(false));
        });
        group.MapPost("/review/delivery", async (HttpContext http, FeedbackStore store, FeedbackAuthority authority) =>
        {
            var request = await AssistantContract.ReadAsync<FeedbackReviewRequest>(http.Request, 2048, http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(await store.DeliveryStatusAsync(request, authority, false, http.RequestAborted).ConfigureAwait(false));
        });
        group.MapPost("/review/delivery/retry", async (HttpContext http, FeedbackStore store, FeedbackAuthority authority) =>
        {
            var request = await AssistantContract.ReadAsync<FeedbackReviewRequest>(http.Request, 2048, http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(await store.DeliveryStatusAsync(request, authority, true, http.RequestAborted).ConfigureAwait(false));
        });
    }
}

internal sealed class FeedbackMaintenance(FeedbackStore store, IConfiguration configuration, ILogger<FeedbackMaintenance> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> DeliveryUnavailable = LoggerMessage.Define(LogLevel.Warning,
        new EventId(71101, "AssistantFeedbackDeliveryUnavailable"), "Assistant feedback audit delivery unavailable; durable events remain pending.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 8192 };
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var nextRetention = DateTimeOffset.MinValue;
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!string.Equals(configuration["Monergy:AssistantFeedback:Enabled"], "true", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                await store.DeliverAsync(client, stoppingToken).ConfigureAwait(false);
                if (nextRetention <= DateTimeOffset.UtcNow)
                {
                    await store.RetainAsync(stoppingToken).ConfigureAwait(false);
                    nextRetention = DateTimeOffset.UtcNow.AddHours(1);
                }
            }
            catch (Exception failure) when (failure is System.Data.Common.DbException or System.Data.DataException or InvalidOperationException or HttpRequestException or JsonException or OperationCanceledException)
            {
                if (stoppingToken.IsCancellationRequested) break;
                DeliveryUnavailable(logger, null); // No exception, connection string, request body or principal enters logs.
            }
        }
    }
}
