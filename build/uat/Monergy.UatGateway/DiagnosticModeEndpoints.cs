using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Monergy.UatGateway;

internal sealed record DiagnosticStartRequest(string Reason, int DurationMinutes = 15);
internal sealed record DiagnosticEvidenceRequest(
    string Service, string Operation, string Outcome, string ReleaseCommit, string Environment,
    DateTimeOffset OccurredAt, string? Classification = null, string? CorrelationId = null,
    string? CausationId = null, long DurationMs = 0, string? Summary = null);

internal sealed class DiagnosticSessionStore(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, DiagnosticSession> sessions = new(StringComparer.Ordinal);

    public DiagnosticSession Start(string reason, int durationMinutes)
    {
        reason = Clean(reason, 256);
        if (reason.Length < 8 || durationMinutes is < 5 or > 30)
            throw new InvalidOperationException("DIAGNOSTIC_SESSION_INVALID");
        var now = clock.GetUtcNow();
        var id = "diag-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var session = new DiagnosticSession(id, reason, "A950", now, now.AddMinutes(durationMinutes), null, []);
        if (!sessions.TryAdd(id, session)) throw new InvalidOperationException("DIAGNOSTIC_SESSION_CONFLICT");
        return session;
    }

    public IReadOnlyList<DiagnosticSession> List() => sessions.Values.OrderByDescending(x => x.StartedAt).ToArray();

    public DiagnosticSession Require(string id)
    {
        if (!sessions.TryGetValue(id, out var session)) throw new KeyNotFoundException();
        return session;
    }

    public DiagnosticSession Stop(string id)
    {
        var current = Require(id);
        if (current.StoppedAt is not null) return current;
        var stopped = current with { StoppedAt = clock.GetUtcNow() };
        sessions[id] = stopped;
        return stopped;
    }

    public DiagnosticSession AddEvidence(string id, DiagnosticEvidenceRequest request)
    {
        var current = Require(id);
        var now = clock.GetUtcNow();
        if (current.StoppedAt is not null || current.ExpiresAt <= now)
            throw new InvalidOperationException("DIAGNOSTIC_SESSION_INACTIVE");
        if (request.DurationMs is < 0 or > 86_400_000)
            throw new InvalidOperationException("DIAGNOSTIC_EVIDENCE_INVALID");
        var evidence = new DiagnosticEvidence(
            current.Id, Clean(request.Service, 128), Clean(request.Operation, 160), Clean(request.Outcome, 64),
            Clean(request.ReleaseCommit, 64), Clean(request.Environment, 64), request.OccurredAt,
            Clean(request.Classification ?? "UNCLASSIFIED", 96), Clean(request.CorrelationId ?? "", 128),
            Clean(request.CausationId ?? "", 128), request.DurationMs, Redact(request.Summary ?? ""));
        if (new[] { evidence.Service, evidence.Operation, evidence.Outcome, evidence.ReleaseCommit, evidence.Environment }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("DIAGNOSTIC_EVIDENCE_INVALID");
        var updated = current with { Evidence = [.. current.Evidence, evidence] };
        sessions[id] = updated;
        return updated;
    }

    private static string Clean(string value, int max) =>
        Regex.Replace((value ?? "").Normalize(NormalizationForm.FormKC), @"[\x00-\x1f\x7f]+", " ").Trim() is var cleaned
            ? cleaned[..Math.Min(cleaned.Length, max)] : "";

    private static string Redact(string value)
    {
        var text = Clean(value, 512);
        text = Regex.Replace(text, @"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|authorization)\b\s*[:=]\s*[^\s,;]+", "$1=[REDACTED]");
        text = Regex.Replace(text, @"(?i)\bBearer\s+[A-Za-z0-9._~+\/-]+=*", "Bearer [REDACTED]");
        return text;
    }
}

internal sealed record DiagnosticSession(
    string Id, string Reason, string StartedBy, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? StoppedAt, IReadOnlyList<DiagnosticEvidence> Evidence)
{
    public bool Active(DateTimeOffset now) => StoppedAt is null && ExpiresAt > now;
}
internal sealed record DiagnosticEvidence(
    string DiagnosticSessionId, string Service, string Operation, string Outcome, string ReleaseCommit,
    string Environment, DateTimeOffset OccurredAt, string Classification, string CorrelationId,
    string CausationId, long DurationMs, string Summary);

internal static class DiagnosticModeEndpoints
{
    public static IServiceCollection AddDiagnosticMode(this IServiceCollection services)
    {
        services.AddSingleton<DiagnosticSessionStore>();
        return services;
    }

    public static void MapDiagnosticMode(this WebApplication app)
    {
        app.MapGet("/platform-api/local/v1/diagnostics/sessions", async (
            HttpContext http, PlatformAdministratorSessions platform, DiagnosticSessionStore store) =>
        {
            _ = await platform.RequireAsync(http).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(new { items = store.List().Select(x => View(x, now)) });
        });

        app.MapPost("/platform-api/local/v1/diagnostics/sessions", async (
            DiagnosticStartRequest request, HttpContext http, PlatformAdministratorSessions platform, DiagnosticSessionStore store) =>
        {
            _ = await platform.RequireAsync(http).ConfigureAwait(false);
            try { return Results.Ok(View(store.Start(request.Reason, request.DurationMinutes), DateTimeOffset.UtcNow)); }
            catch (InvalidOperationException failure) { return Results.UnprocessableEntity(new { error = failure.Message }); }
        });

        app.MapPost("/platform-api/local/v1/diagnostics/sessions/{id}/stop", async (
            string id, HttpContext http, PlatformAdministratorSessions platform, DiagnosticSessionStore store) =>
        {
            _ = await platform.RequireAsync(http).ConfigureAwait(false);
            try { return Results.Ok(View(store.Stop(id), DateTimeOffset.UtcNow)); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "DIAGNOSTIC_SESSION_NOT_FOUND" }); }
        });

        app.MapPost("/internal/local/v1/diagnostics/{id}/evidence", async (
            string id, DiagnosticEvidenceRequest request, HttpContext http, DiagnosticSessionStore store) =>
        {
            var expected = app.Configuration["Monergy:PlatformAdministration:RuntimeToken"] ?? "";
            var supplied = http.Request.Headers["X-Monergy-Diagnostic-Runtime"].ToString();
            if (!FixedTime(supplied, expected)) return Results.StatusCode(403);
            try { return Results.Ok(View(store.AddEvidence(id, request), DateTimeOffset.UtcNow)); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "DIAGNOSTIC_SESSION_NOT_FOUND" }); }
            catch (InvalidOperationException failure)
            { return Results.Json(new { error = failure.Message }, statusCode: failure.Message == "DIAGNOSTIC_SESSION_INACTIVE" ? 409 : 422); }
        });

        app.MapGet("/platform-api/local/v1/diagnostics/sessions/{id}/diagnosis", async (
            string id, HttpContext http, PlatformAdministratorSessions platform, DiagnosticSessionStore store, HttpClient client) =>
        {
            _ = await platform.RequireAsync(http).ConfigureAwait(false);
            DiagnosticSession session;
            try { session = store.Require(id); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "DIAGNOSTIC_SESSION_NOT_FOUND" }); }
            var root = app.Configuration["Monergy:SystemExpert:Url"] ?? "http://127.0.0.1:4310";
            using var response = await client.PostAsJsonAsync(root.TrimEnd('/') + "/api/diagnostics/analyze",
                new { session = new { id = session.Id, reason = session.Reason }, evidence = session.Evidence }, http.RequestAborted).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Results.Json(new { error = "DIAGNOSTIC_ANALYSIS_UNAVAILABLE" }, statusCode: 503);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Content(await response.Content.ReadAsStringAsync(http.RequestAborted).ConfigureAwait(false),
                "application/json", Encoding.UTF8, 200);
        });
    }

    private static object View(DiagnosticSession session, DateTimeOffset now) => new {
        session.Id, session.Reason, session.StartedBy, session.StartedAt, session.ExpiresAt, session.StoppedAt,
        active = session.Active(now), evidenceCount = session.Evidence.Count
    };

    private static bool FixedTime(string supplied, string expected) =>
        !string.IsNullOrEmpty(expected) && supplied.Length == expected.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
}
