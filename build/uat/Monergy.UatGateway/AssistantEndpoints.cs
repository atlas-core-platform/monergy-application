using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Platform;

namespace Monergy.UatGateway;

internal sealed record BrowserHelpTopic(string ContractVersion, string GeneralQuestion, string? TopicId = null);
internal sealed record AssistantReviewStatus(bool ReviewAllowed);
internal sealed record AssistantDeliveryRetry(string ContractVersion);

internal static class AssistantEndpoints
{
    public static IServiceCollection AddAssistant(this IServiceCollection services)
    {
        services.AddSingleton<AssistantKnowledgeClient>();
        services.AddSingleton<AssistantFeedbackClient>();
        services.AddSingleton<AssistantRateLimiter>();
        services.AddSingleton<AssistantCounterQueue>();
        services.AddHostedService(provider => provider.GetRequiredService<AssistantCounterQueue>());
        return services;
    }

    public static void MapAssistant(this WebApplication app)
    {
        foreach (var audience in new[] { "product", "platform" })
        {
            var group = app.MapGroup(audience == "product" ? "/assistant-api/local/v1" : "/platform-api/local/v1/assistant");
            group.AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                try
                {
                    if (!app.Configuration.GetValue<bool>("Monergy:Assistant:Enabled")) return Results.NotFound();
                    return await next(context).ConfigureAwait(false);
                }
                catch (AssistantOutcomeUnknownException)
                { return Results.Json(new { error = "ASSISTANT_OUTCOME_UNKNOWN", outcome = "UNKNOWN", message = "We couldn’t confirm whether your change was saved. Retry to check." }, statusCode: 503); }
                catch (TenantBoundaryException failure)
                { return Results.Json(new { error = failure.Code, message = Message(failure.Status), outcome = "REJECTED", saved = false }, statusCode: failure.Status); }
                catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
                { return Results.Json(new { error = "ASSISTANT_KNOWLEDGE_UNAVAILABLE", message = Message(503), outcome = "REJECTED", saved = false }, statusCode: 503); }
            });
            group.MapGet("/status", async (HttpContext http, AssistantKnowledgeClient knowledge, AssistantFeedbackClient feedback, AssistantRateLimiter rate) =>
            {
                var caller = await AuthenticateAsync(http, audience).ConfigureAwait(false);
                rate.Require(http, caller, "status", 20, TimeSpan.FromMinutes(1));
                if (http.Request.Query.Count != 0) throw new TenantBoundaryException("ASSISTANT_REQUEST_INVALID", 400);
                var available = await knowledge.ReadyAsync(http.RequestAborted).ConfigureAwait(false);
                var reviewAllowed = false;
                if (audience == "platform")
                {
                    try
                    {
                        var context = await http.RequestServices.GetRequiredService<PlatformAdministratorSessions>().RequireContextAsync(http, "list", null).ConfigureAwait(false);
                        var result = await feedback.SendAsync<AssistantReviewStatus>("review/status", new PlatformContextValidation(context.AuthenticationContext, "list", null), true, http.RequestAborted).ConfigureAwait(false);
                        reviewAllowed = result.ReviewAllowed;
                    }
                    catch (Exception failure) when (failure is TenantBoundaryException or HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { reviewAllowed = false; }
                }
                await AuthenticateAsync(http, audience).ConfigureAwait(false);
                return Results.Ok(new { enabled = true, available, reviewAllowed });
            });
            group.MapPost("/answers", async (HttpContext http, AssistantKnowledgeClient knowledge, AssistantRateLimiter rate, AssistantCounterQueue counters) =>
            {
                var caller = await AuthenticateAsync(http, audience).ConfigureAwait(false);
                rate.Require(http, caller, "answer", 20, TimeSpan.FromMinutes(1));
                var request = await AssistantContract.ReadAsync<AssistantQuestion>(http.Request, 8192, http.RequestAborted).ConfigureAwait(false);
                var answer = await knowledge.AnswerAsync(audience, request, http.RequestAborted).ConfigureAwait(false);
                if (await AuthenticateAsync(http, audience).ConfigureAwait(false) != caller) throw new TenantBoundaryException("ASSISTANT_CONTEXT_CHANGED", 403);
                counters.Enqueue(answer.Counter);
                return Results.Ok(answer.Projection);
            });
            group.MapPost("/help-topics", async (HttpContext http, AssistantKnowledgeClient knowledge, AssistantFeedbackClient feedback, AssistantRateLimiter rate, IConfiguration config) =>
            {
                var caller = await AuthenticateAsync(http, audience).ConfigureAwait(false);
                var operation = Operation(http);
                rate.Require(http, caller, "suggestion-attempt", 20, TimeSpan.FromMinutes(1));
                rate.Require(http, caller, "suggestion", 5, TimeSpan.FromHours(1), operation);
                var request = await AssistantContract.ReadAsync<BrowserHelpTopic>(http.Request, 2048, http.RequestAborted).ConfigureAwait(false);
                if (request.ContractVersion != "1.0.0" || request.GeneralQuestion.Length is < 5 or > 500 || !knowledge.Catalogue.TopicAllowed(audience, request.TopicId) ||
                    AssistantInputSafety.ContainsProtectedMaterial(request.GeneralQuestion) || AssistantInputSafety.ContainsUnsafeInstruction(request.GeneralQuestion))
                    throw new TenantBoundaryException("HELP_TOPIC_INPUT_REJECTED", 400);
                var secret = config["Monergy:Assistant:FeedbackIngestToken"] ?? "";
                if (secret.Length < 32) throw new InvalidOperationException("Feedback unavailable.");
                var idempotency = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(caller + "|" + operation)));
                // The owner receives only explicit text, trusted audience/revisions and opaque idempotency; no tenant, actor or session.
                var submission = new HelpTopicSubmission("1.0.0", request.GeneralQuestion, request.TopicId, audience,
                    knowledge.Catalogue.KnowledgeRevision, knowledge.Catalogue.RetrievalRevision, idempotency);
                if (await AuthenticateAsync(http, audience).ConfigureAwait(false) != caller) throw new TenantBoundaryException("ASSISTANT_CONTEXT_CHANGED", 403);
                var saved = await feedback.SendAsync<FeedbackSaved>("topics", submission, false, http.RequestAborted).ConfigureAwait(false);
                if (!saved.Saved || !AssistantContract.OpaqueId(saved.GapId)) throw new AssistantOutcomeUnknownException();
                return Results.Ok(new { saved = true, saved.GapId });
            });
            if (audience == "platform") MapReview(group);
        }
    }

    private static void MapReview(RouteGroupBuilder group)
    {
        group.MapGet("/review/audit-deliveries", async (HttpContext http, AssistantFeedbackClient feedback, AssistantRateLimiter rate) =>
        {
            var caller = await AuthenticateAsync(http, "platform").ConfigureAwait(false);
            rate.Require(http, caller, "review", 20, TimeSpan.FromMinutes(1));
            if (http.Request.Query.Any(pair => pair.Key is not ("limit" or "cursor") || pair.Value.Count != 1) ||
                !int.TryParse(http.Request.Query["limit"].FirstOrDefault() ?? "20", System.Globalization.CultureInfo.InvariantCulture, out var limit) || limit is < 1 or > 20)
                throw new TenantBoundaryException("FEEDBACK_AUDIT_QUERY_INVALID", 400);
            var cursor = http.Request.Query.TryGetValue("cursor", out var value) ? value.ToString() : null;
            if (cursor?.Length > 256) throw new TenantBoundaryException("FEEDBACK_CURSOR_INVALID", 400);
            var context = await http.RequestServices.GetRequiredService<PlatformAdministratorSessions>().RequireContextAsync(http, "list", null).ConfigureAwait(false);
            var page = await feedback.SendAsync<FeedbackAuditPage>("review/audit-deliveries", new FeedbackAuditRequest(context.AuthenticationContext, null, cursor, limit, null), true, http.RequestAborted).ConfigureAwait(false);
            if (page.Events.Count > limit || page.NextCursor?.Length > 256 || page.Events.Any(item => !ValidAuditEvent(item)))
                throw new InvalidOperationException("Feedback audit projection invalid.");
            await AuthenticateAsync(http, "platform").ConfigureAwait(false);
            return Results.Ok(page);
        });
        group.MapGet("/review/audit-deliveries/{eventId}", async (string eventId, HttpContext http, AssistantFeedbackClient feedback, AssistantRateLimiter rate) =>
            await AuditEventAsync(eventId, http, feedback, rate, false).ConfigureAwait(false));
        group.MapPost("/review/audit-deliveries/{eventId}/retry", async (string eventId, HttpContext http, AssistantFeedbackClient feedback, AssistantRateLimiter rate) =>
        {
            await AuthenticateAsync(http, "platform").ConfigureAwait(false);
            var request = await AssistantContract.ReadAsync<AssistantDeliveryRetry>(http.Request, 256, http.RequestAborted).ConfigureAwait(false);
            if (request.ContractVersion != "1.0.0") throw new TenantBoundaryException("FEEDBACK_AUDIT_REQUEST_INVALID", 400);
            return await AuditEventAsync(eventId, http, feedback, rate, true).ConfigureAwait(false);
        });
        group.MapGet("/review/topics", async (HttpContext http, AssistantFeedbackClient feedback, AssistantRateLimiter rate) =>
        {
            var caller = await AuthenticateAsync(http, "platform").ConfigureAwait(false);
            rate.Require(http, caller, "review", 20, TimeSpan.FromMinutes(1));
            string[] keys = ["limit", "cursor", "status", "sourceAudience"];
            if (http.Request.Query.Any(pair => !keys.Contains(pair.Key, StringComparer.Ordinal) || pair.Value.Count != 1) ||
                !int.TryParse(http.Request.Query["limit"].FirstOrDefault() ?? "20", System.Globalization.CultureInfo.InvariantCulture, out var limit) || limit is < 1 or > 20)
                throw new TenantBoundaryException("FEEDBACK_QUERY_INVALID", 400);
            string? Q(string key) => http.Request.Query.TryGetValue(key, out var value) ? value.ToString() : null;
            if (Q("cursor")?.Length > 256) throw new TenantBoundaryException("FEEDBACK_CURSOR_INVALID", 400);
            var context = await http.RequestServices.GetRequiredService<PlatformAdministratorSessions>().RequireContextAsync(http, "list", null).ConfigureAwait(false);
            var page = await feedback.SendAsync<FeedbackPage>("review/topics", new FeedbackReviewRequest(context.AuthenticationContext, null, Q("cursor"), limit,
                Q("status"), Q("sourceAudience"), null, null), true, http.RequestAborted).ConfigureAwait(false);
            if (page.Topics.Count > limit || page.NextCursor?.Length > 256 || page.Topics.Any(topic => !AssistantContract.OpaqueId(topic.GapId) ||
                    topic.GeneralQuestion.Length > 500 || topic.SourceAudience is not ("product" or "platform") || topic.Revision < 1))
                throw new InvalidOperationException("Feedback projection invalid.");
            await AuthenticateAsync(http, "platform").ConfigureAwait(false);
            return Results.Ok(page);
        });
        group.MapPatch("/review/topics/{gapId}", async (string gapId, HttpContext http, AssistantFeedbackClient feedback, AssistantRateLimiter rate) =>
        {
            var caller = await AuthenticateAsync(http, "platform").ConfigureAwait(false);
            rate.Require(http, caller, "review", 20, TimeSpan.FromMinutes(1));
            if (!AssistantContract.OpaqueId(gapId)) throw new TenantBoundaryException("HELP_TOPIC_INVALID", 400);
            gapId = Guid.Parse(gapId).ToString();
            var change = await AssistantContract.ReadAsync<TopicChange>(http.Request, 4096, http.RequestAborted).ConfigureAwait(false);
            var operation = change.PrivacyDelete ? "privacy-delete" : change.Resolution is not null ? "resolution" : "status";
            var context = await http.RequestServices.GetRequiredService<PlatformAdministratorSessions>().RequireContextAsync(http, operation, gapId).ConfigureAwait(false);
            var result = await feedback.SendAsync<FeedbackSaved>("review/change", new FeedbackReviewRequest(context.AuthenticationContext, gapId, null, 1,
                null, null, Operation(http), change), true, http.RequestAborted).ConfigureAwait(false);
            if (!result.Saved || result.GapId != gapId || result.AuditState is not ("Pending" or "Accepted"))
                throw new AssistantOutcomeUnknownException();
            return Results.Ok(result);
        });
        group.MapGet("/review/topics/{gapId}/delivery", async (string gapId, HttpContext http, AssistantFeedbackClient feedback, AssistantRateLimiter rate) =>
            await DeliveryAsync(gapId, http, feedback, rate, false).ConfigureAwait(false));
        group.MapPost("/review/topics/{gapId}/delivery/retry", async (string gapId, HttpContext http, AssistantFeedbackClient feedback, AssistantRateLimiter rate) =>
        {
            await AuthenticateAsync(http, "platform").ConfigureAwait(false);
            var request = await AssistantContract.ReadAsync<AssistantDeliveryRetry>(http.Request, 256, http.RequestAborted).ConfigureAwait(false);
            if (request.ContractVersion != "1.0.0") throw new TenantBoundaryException("FEEDBACK_DELIVERY_REQUEST_INVALID", 400);
            return await DeliveryAsync(gapId, http, feedback, rate, true).ConfigureAwait(false);
        });
    }

    private static async Task<IResult> AuditEventAsync(string eventId, HttpContext http, AssistantFeedbackClient feedback, AssistantRateLimiter rate, bool retry)
    {
        var caller = await AuthenticateAsync(http, "platform").ConfigureAwait(false);
        rate.Require(http, caller, "review", 20, TimeSpan.FromMinutes(1));
        if (!AssistantContract.OpaqueId(eventId) || http.Request.Query.Count != 0) throw new TenantBoundaryException("FEEDBACK_AUDIT_REQUEST_INVALID", 400);
        eventId = Guid.Parse(eventId).ToString();
        var context = await http.RequestServices.GetRequiredService<PlatformAdministratorSessions>().RequireContextAsync(http, "status", eventId).ConfigureAwait(false);
        var result = await feedback.SendAsync<FeedbackAuditResult>(retry ? "review/audit-deliveries/retry" : "review/audit-deliveries/status",
            new FeedbackAuditRequest(context.AuthenticationContext, eventId, null, 1, retry ? Operation(http) : null), true, http.RequestAborted).ConfigureAwait(false);
        if (result.Event.EventId != eventId || !ValidAuditEvent(result.Event) || retry && (result.Saved != true || result.AuditState is not ("Pending" or "Accepted")))
        {
            if (retry) throw new AssistantOutcomeUnknownException();
            throw new InvalidOperationException("Feedback audit projection invalid.");
        }
        if (!retry) await AuthenticateAsync(http, "platform").ConfigureAwait(false);
        return Results.Ok(result);
    }
    private static bool ValidAuditEvent(FeedbackAuditEvent item) => AssistantContract.OpaqueId(item.EventId) && AssistantContract.OpaqueId(item.OperationId) &&
        (item.ResourceId is null || AssistantContract.OpaqueId(item.ResourceId)) && item.Attempts is >= 0 and <= 16 && item.Recoveries is >= 0 and <= 3 &&
        item.State is "Accepted" or "Pending" or "Paused" && item.Action is "FeedbackRead" or "FeedbackClassify" or "FeedbackAssign" or "FeedbackDuplicate" or "FeedbackStatus" or "FeedbackPrivacyDelete";

    private static async Task<IResult> DeliveryAsync(string gapId, HttpContext http, AssistantFeedbackClient feedback, AssistantRateLimiter rate, bool retry)
    {
        var caller = await AuthenticateAsync(http, "platform").ConfigureAwait(false);
        rate.Require(http, caller, "review", 20, TimeSpan.FromMinutes(1));
        if (!AssistantContract.OpaqueId(gapId) || http.Request.Query.Count != 0) throw new TenantBoundaryException("HELP_TOPIC_INVALID", 400);
        var context = await http.RequestServices.GetRequiredService<PlatformAdministratorSessions>().RequireContextAsync(http, "status", gapId).ConfigureAwait(false);
        var request = new FeedbackReviewRequest(context.AuthenticationContext, gapId, null, 1, null, null, retry ? Operation(http) : null, null);
        var result = await feedback.SendAsync<FeedbackDeliveryStatus>(retry ? "review/delivery/retry" : "review/delivery", request, true, http.RequestAborted).ConfigureAwait(false);
        if (result.GapId != gapId || result.RetryLimit != 16 || result.RecoveryLimit != 3 || result.Deliveries.Count > 50 ||
            result.Deliveries.Any(item => !AssistantContract.OpaqueId(item.EventId) || item.Attempts is < 0 or > 16 ||
                item.Recoveries is < 0 or > 3 || item.State is not ("Accepted" or "Pending" or "Paused")))
        {
            if (retry) throw new AssistantOutcomeUnknownException();
            throw new InvalidOperationException("Feedback delivery projection invalid.");
        }
        if (!retry) await AuthenticateAsync(http, "platform").ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static string Operation(HttpContext http) => http.Request.Headers["Idempotency-Key"].Count == 1 &&
        AssistantContract.OpaqueId(http.Request.Headers["Idempotency-Key"].ToString())
        ? Guid.Parse(http.Request.Headers["Idempotency-Key"].ToString()).ToString() : throw new TenantBoundaryException("ASSISTANT_OPERATION_ID_REQUIRED", 400);

    private static async Task<string> AuthenticateAsync(HttpContext http, string audience)
    {
        if (audience == "product")
        {
            var tenant = TrustedEntryContext.RequireTenant(http);
            var owner = http.RequestServices.GetRequiredService<CustomerOwnerClient>();
            var session = await owner.SessionAsync(http).ConfigureAwait(false);
            if (session.TenantId != tenant.TenantId) throw new TenantBoundaryException("TENANT_CONTEXT_MISMATCH", 403);
            await owner.RequireActiveMemberAsync(session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false);
            return "product|" + session.TenantId + "|" + session.ActorId;
        }
        if (!http.Items.TryGetValue(TrustedEntryContext.ItemKey, out var item) || item is not TrustedEntryContext { Kind: EntryHostKind.Platform })
            throw new TenantBoundaryException("PLATFORM_HOST_REQUIRED", 403);
        var context = await http.RequestServices.GetRequiredService<PlatformAdministratorSessions>().RequireContextAsync(http, "ask", null).ConfigureAwait(false);
        return "platform|" + context.Realm + "|" + context.PrincipalId;
    }
    private static string Message(int status) => status switch
    {
        401 => "Your session has ended. Please sign in again.", 403 => "You do not have access to this action.",
        400 => "Please enter a general product question without personal or account details.",
        409 => "The item has changed. Please refresh before trying again.", 429 => "Please wait before trying again.",
        _ => "Help is temporarily unavailable. Please try again.",
    };
}
