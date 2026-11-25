using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Monergy.UatGateway;

internal sealed class DiagnosticStartRequest
{
    public string? Reason { get; init; }
    public int DurationMinutes { get; init; } = 15;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; init; }
}

internal sealed class DiagnosticEvidenceRequest
{
    public string? DiagnosticSessionId { get; init; }
    public string? Service { get; init; }
    public string? Operation { get; init; }
    public string? Outcome { get; init; }
    public string? ReleaseCommit { get; init; }
    public string? Environment { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string? Classification { get; init; }
    public string? CorrelationId { get; init; }
    public string? CausationId { get; init; }
    public long DurationMs { get; init; }
    public string? DetailCode { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; init; }
}

internal sealed class DiagnosticSessionStore(TimeProvider? timeProvider = null)
{
    internal const int MaximumEvidence = 256;
    private static readonly Regex ReasonPattern = new(@"^[\p{L}\p{N}][\p{L}\p{N} .,'()/_-]{7,255}$", RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveReasonPattern = new(@"(?i)(password|passwd|secret|token|api.?key|authorization|connection.?string|bearer|customer.?id|account.?number)", RegexOptions.CultureInvariant);
    private static readonly Regex CodePattern = new(@"^[A-Z][A-Z0-9_]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex NamePattern = new(@"^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex OperationPattern = new(@"^[a-z][a-z0-9./_-]{0,95}$", RegexOptions.CultureInvariant);
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,95}$", RegexOptions.CultureInvariant);
    private static readonly Regex CommitPattern = new(@"^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, DiagnosticSession> sessions = new(StringComparer.Ordinal);

    public DiagnosticSession Start(string? reason, int durationMinutes, string principalId)
    {
        reason = (reason ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
        if (!ReasonPattern.IsMatch(reason) || SensitiveReasonPattern.IsMatch(reason) ||
            durationMinutes is < 5 or > 30 || !IdPattern.IsMatch(principalId))
            throw new InvalidOperationException("DIAGNOSTIC_SESSION_INVALID");
        var now = clock.GetUtcNow();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var id = "diag-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            var session = new DiagnosticSession(id, reason, principalId, now, now.AddMinutes(durationMinutes), null, []);
            if (sessions.TryAdd(id, session)) return session;
        }
        throw new InvalidOperationException("DIAGNOSTIC_SESSION_CONFLICT");
    }

    public IReadOnlyList<DiagnosticSession> List() => sessions.Values.OrderByDescending(x => x.StartedAt).ToArray();

    public DiagnosticSession Require(string id)
    {
        if (!sessions.TryGetValue(id, out var session)) throw new KeyNotFoundException();
        return session;
    }

    public DiagnosticSession Stop(string id)
    {
        while (true)
        {
            var current = Require(id);
            if (current.StoppedAt is not null) return current;
            var stopped = current with { StoppedAt = clock.GetUtcNow() };
            if (sessions.TryUpdate(id, stopped, current)) return stopped;
        }
    }

    public DiagnosticSession AddEvidence(string id, DiagnosticEvidenceRequest request)
    {
        if (request.Unknown?.Count > 0 || request.DiagnosticSessionId != id || request.DurationMs is < 0 or > 86_400_000)
            throw new InvalidOperationException("DIAGNOSTIC_EVIDENCE_INVALID");
        var evidence = Validate(request);
        while (true)
        {
            var current = Require(id);
            var now = clock.GetUtcNow();
            if (current.StoppedAt is not null || current.ExpiresAt <= now)
                throw new InvalidOperationException("DIAGNOSTIC_SESSION_INACTIVE");
            if (evidence.OccurredAt < current.StartedAt.AddMinutes(-1) || evidence.OccurredAt > now.AddMinutes(1))
                throw new InvalidOperationException("DIAGNOSTIC_EVIDENCE_INVALID");
            if (current.Evidence.Contains(evidence)) return current;
            if (current.Evidence.Count >= MaximumEvidence)
                throw new InvalidOperationException("DIAGNOSTIC_EVIDENCE_CAPACITY");
            var updated = current with { Evidence = [.. current.Evidence, evidence] };
            if (sessions.TryUpdate(id, updated, current)) return updated;
        }
    }

    private static DiagnosticEvidence Validate(DiagnosticEvidenceRequest request)
    {
        var classification = request.Classification ?? "UNCLASSIFIED";
        var detailCode = request.DetailCode ?? string.Empty;
        var correlation = request.CorrelationId ?? string.Empty;
        var causation = request.CausationId ?? string.Empty;
        if (!NamePattern.IsMatch(request.Service ?? string.Empty) || !OperationPattern.IsMatch(request.Operation ?? string.Empty) ||
            !CodePattern.IsMatch(request.Outcome ?? string.Empty) || !CommitPattern.IsMatch(request.ReleaseCommit ?? string.Empty) ||
            request.Environment != "LOCAL" || !CodePattern.IsMatch(classification) ||
            (detailCode.Length > 0 && !CodePattern.IsMatch(detailCode)) ||
            (correlation.Length > 0 && !IdPattern.IsMatch(correlation)) || (causation.Length > 0 && !IdPattern.IsMatch(causation)) ||
            request.OccurredAt == default || request.OccurredAt.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("DIAGNOSTIC_EVIDENCE_INVALID");
        return new(request.DiagnosticSessionId!, request.Service!, request.Operation!, request.Outcome!, request.ReleaseCommit!,
            request.Environment!, request.OccurredAt, classification, correlation, causation, request.DurationMs, detailCode);
    }
}

internal sealed record DiagnosticSession(
    string Id, string Reason, string StartedBy, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? StoppedAt, IReadOnlyList<DiagnosticEvidence> Evidence)
{
    public bool Active(DateTimeOffset now) => StoppedAt is null && ExpiresAt > now;
    public string State(DateTimeOffset now) => StoppedAt is not null ? "Stopped" : ExpiresAt <= now ? "Expired" : "Active";
}
internal sealed record DiagnosticEvidence(
    string DiagnosticSessionId, string Service, string Operation, string Outcome, string ReleaseCommit,
    string Environment, DateTimeOffset OccurredAt, string Classification, string CorrelationId,
    string CausationId, long DurationMs, string DetailCode);

internal sealed class DiagnosticAnalysisClient(HttpClient client, IConfiguration configuration)
{
    public async Task<JsonElement> AnalyzeAsync(DiagnosticSession session, CancellationToken cancellationToken)
    {
        var token = configuration["Monergy:Diagnostics:RuntimeToken"];
        if (string.IsNullOrWhiteSpace(token) || !Regex.IsMatch(token, "^[0-9a-fA-F]{64}$"))
            throw new InvalidOperationException("DIAGNOSTIC_ANALYSIS_UNAVAILABLE");
        using var request = new HttpRequestMessage(HttpMethod.Post, "internal/v1/diagnostics/analyze")
        {
            Content = JsonContent.Create(new { session = new { id = session.Id, reason = session.Reason }, evidence = session.Evidence })
        };
        request.Headers.Add("X-Monergy-Diagnostic-Runtime", token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 131_072)
            throw new InvalidOperationException("DIAGNOSTIC_ANALYSIS_UNAVAILABLE");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (bounded.Length + read > 131_072) throw new InvalidOperationException("DIAGNOSTIC_ANALYSIS_UNAVAILABLE");
            bounded.Write(buffer, 0, read);
        }
        bounded.Position = 0;
        using var document = await JsonDocument.ParseAsync(bounded, new JsonDocumentOptions { MaxDepth = 8 }, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("diagnosticSessionId", out var sessionId) || sessionId.GetString() != session.Id ||
            !root.TryGetProperty("observations", out var observations) || observations.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("hypotheses", out var hypotheses) || hypotheses.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("verifiedCause", out _) || !root.TryGetProperty("sourceComparisons", out var sources) || sources.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("remediation", out var remediation) || remediation.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("DIAGNOSTIC_ANALYSIS_UNAVAILABLE");
        return root.Clone();
    }
}

internal static class DiagnosticModeEndpoints
{
    public static IServiceCollection AddDiagnosticMode(this IServiceCollection services, IConfiguration configuration)
    {
        if (!string.Equals(configuration["Monergy:ExecutionZone"], "LOCAL", StringComparison.Ordinal))
            throw new InvalidOperationException("Diagnostic Mode is LOCAL-only.");
        services.AddSingleton<DiagnosticSessionStore>();
        services.AddHttpClient<DiagnosticAnalysisClient>(client =>
        {
            var root = configuration["Monergy:Diagnostics:SystemExpertUrl"] ?? "http://127.0.0.1:5122/";
            if (!Uri.TryCreate(root, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp)
                throw new InvalidOperationException("Diagnostic analysis endpoint is required.");
            client.BaseAddress = endpoint;
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        return services;
    }

    public static void MapDiagnosticMode(this WebApplication app)
    {
        app.MapGet("/platform-api/local/v1/diagnostics/sessions", async (HttpContext http, PlatformAdministratorSessions platform, DiagnosticSessionStore store) =>
        {
            _ = await platform.RequirePrincipalAsync(http).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(new { items = store.List().Select(x => View(x, now)) });
        });
        app.MapPost("/platform-api/local/v1/diagnostics/sessions", async (DiagnosticStartRequest request, HttpContext http, PlatformAdministratorSessions platform, DiagnosticSessionStore store) =>
        {
            var actor = await platform.RequirePrincipalAsync(http).ConfigureAwait(false);
            try
            {
                if (request.Unknown?.Count > 0) throw new InvalidOperationException("DIAGNOSTIC_SESSION_INVALID");
                return Results.Ok(View(store.Start(request.Reason, request.DurationMinutes, actor), DateTimeOffset.UtcNow));
            }
            catch (InvalidOperationException failure) { return Results.UnprocessableEntity(new { error = failure.Message }); }
        });
        app.MapPost("/platform-api/local/v1/diagnostics/sessions/{id}/stop", async (string id, HttpContext http, PlatformAdministratorSessions platform, DiagnosticSessionStore store) =>
        {
            _ = await platform.RequirePrincipalAsync(http).ConfigureAwait(false);
            try { return Results.Ok(View(store.Stop(id), DateTimeOffset.UtcNow)); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "DIAGNOSTIC_SESSION_NOT_FOUND" }); }
        });
        app.MapPost("/internal/local/v1/diagnostics/{id}/evidence", (string id, DiagnosticEvidenceRequest request, HttpContext http, DiagnosticSessionStore store) =>
        {
            var expected = app.Configuration["Monergy:Diagnostics:RuntimeToken"] ?? string.Empty;
            var supplied = http.Request.Headers["X-Monergy-Diagnostic-Runtime"].ToString();
            if (!FixedTime(supplied, expected)) return Results.StatusCode(403);
            try { return Results.Ok(View(store.AddEvidence(id, request), DateTimeOffset.UtcNow)); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "DIAGNOSTIC_SESSION_NOT_FOUND" }); }
            catch (InvalidOperationException failure)
            { return Results.Json(new { error = failure.Message }, statusCode: failure.Message == "DIAGNOSTIC_SESSION_INACTIVE" ? 409 : failure.Message == "DIAGNOSTIC_EVIDENCE_CAPACITY" ? 429 : 422); }
        });
        app.MapGet("/platform-api/local/v1/diagnostics/sessions/{id}/diagnosis", async (string id, HttpContext http, PlatformAdministratorSessions platform, DiagnosticSessionStore store, DiagnosticAnalysisClient client) =>
        {
            _ = await platform.RequirePrincipalAsync(http).ConfigureAwait(false);
            DiagnosticSession session;
            try { session = store.Require(id); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "DIAGNOSTIC_SESSION_NOT_FOUND" }); }
            try
            {
                http.Response.Headers.CacheControl = "no-store";
                return Results.Json(await client.AnalyzeAsync(session, http.RequestAborted).ConfigureAwait(false));
            }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
            { return Results.Json(new { error = "DIAGNOSTIC_ANALYSIS_UNAVAILABLE" }, statusCode: 503); }
        });
    }

    private static object View(DiagnosticSession session, DateTimeOffset now) => new {
        session.Id, session.Reason, session.StartedBy, session.StartedAt, session.ExpiresAt, session.StoppedAt,
        active = session.Active(now), state = session.State(now), evidenceCount = session.Evidence.Count
    };

    private static bool FixedTime(string supplied, string expected)
    {
        var valid = Regex.IsMatch(supplied, "^[0-9a-fA-F]{64}$") && Regex.IsMatch(expected, "^[0-9a-fA-F]{64}$");
        var candidate = valid ? Convert.FromHexString(supplied) : new byte[32];
        var target = Regex.IsMatch(expected, "^[0-9a-fA-F]{64}$") ? Convert.FromHexString(expected) : new byte[32];
        return valid && CryptographicOperations.FixedTimeEquals(candidate, target);
    }
}
