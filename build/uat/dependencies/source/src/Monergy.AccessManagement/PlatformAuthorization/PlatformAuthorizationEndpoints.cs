using System.Text.Json;
using Npgsql;

namespace Monergy.AccessManagement;

public sealed class PlatformAuthorizationModule(IConfiguration configuration, IHostEnvironment environment, TimeProvider time) : IDisposable
{
    private readonly object initializationLock = new();
    private PlatformReviewerDependencies? dependencies;
    public bool Enabled => environment.IsDevelopment() && configuration["Monergy:ExecutionZone"] == "LOCAL" &&
        string.Equals(configuration["Monergy:AccessManagement:PlatformAuthorization:Enabled"], "true", StringComparison.OrdinalIgnoreCase);

    public PlatformReviewerDependencies GetDependencies()
    {
        lock (initializationLock)
        {
            if (dependencies is not null) return dependencies;
            var section = configuration.GetSection("Monergy:AccessManagement:PlatformAuthorization");
            try
            {
                var evaluationToken = section["EvaluationToken"] ?? "";
                var operatorFile = section["OperatorCredentialFile"] ?? "";
                if (!Path.IsPathFullyQualified(operatorFile)) throw new InvalidOperationException();
                var operatorToken = File.ReadAllText(operatorFile).Trim();
                var identity = new ReferenceDeliveryRoute(new Uri(section["IdentityUrl"] ?? ""), section["IdentityToken"] ?? "");
                var audit = new ReferenceDeliveryRoute(new Uri(section["AuditUrl"] ?? ""), section["AuditToken"] ?? "");
                ReferenceOwnerBoundary.Validate(identity);
                ReferenceOwnerBoundary.Validate(audit);
                ReferenceOwnerBoundary.ValidateToken(evaluationToken);
                ReferenceOwnerBoundary.ValidateToken(operatorToken);
                var tokens = new[] { evaluationToken, operatorToken, identity.Token, audit.Token };
                if (tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Length) throw new InvalidOperationException();
                var connection = section["ConnectionString"];
                if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException();
                var parsed = new NpgsqlConnectionStringBuilder(connection);
                if (parsed.IncludeErrorDetail || parsed.LogParameters || parsed.PersistSecurityInfo ||
                    string.IsNullOrWhiteSpace(parsed.Database) || string.IsNullOrWhiteSpace(parsed.Username)) throw new InvalidOperationException();
                dependencies = new(evaluationToken, operatorToken, new PlatformIdentityClient(identity, time),
                    new PostgresPlatformAuthorizationStore(connection, time), audit);
                return dependencies;
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException or IOException or UnauthorizedAccessException)
            { throw new PlatformAuthorizationException("PLATFORM_REVIEW_CONFIGURATION_UNAVAILABLE", 503); }
        }
    }

    public void Dispose() => dependencies?.Identity.Dispose();
}

public sealed record PlatformReviewerDependencies(string EvaluationToken, string OperatorToken,
    PlatformIdentityClient Identity, PostgresPlatformAuthorizationStore Store, ReferenceDeliveryRoute AuditRoute);

public static class PlatformAuthorizationEndpoints
{
    private static readonly Action<ILogger, Exception?> DependencyUnavailable = LoggerMessage.Define(LogLevel.Warning,
        new EventId(71102, "PlatformReviewerDependencyUnavailable"),
        "Platform reviewer operation denied because its dependency or durable audit obligation is unavailable.");

    public static void AddPlatformReviewerAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<PlatformAuthorizationModule>();
        services.AddHostedService<PlatformAuditDispatcher>();
    }

    public static void MapPlatformReviewerAuthorization(this WebApplication app)
    {
        const string prefix = "/platform/v1/assistant-feedback";
        app.MapGet(prefix + "/status", (HttpContext http, PlatformAuthorizationModule module) =>
            ExecuteAsync(http, module, false, async dependencies =>
            {
                var ready = await dependencies.Store.ReadyAsync(http.RequestAborted).ConfigureAwait(false);
                var status = await dependencies.Store.StatusAsync(http.RequestAborted).ConfigureAwait(false);
                return Results.Json(new { available = ready && status.PendingAudit < PlatformReviewerContract.MaximumPendingAudit }, statusCode: ready ? 200 : 503);
            }));
        app.MapPost(prefix + "/evaluate", (HttpContext http, PlatformAuthorizationModule module) =>
            ExecuteAsync(http, module, false, async dependencies =>
            {
                var request = await ReadRequestAsync<PlatformReviewEvaluationRequest>(http).ConfigureAwait(false);
                PlatformReviewerContract.Require(request.AuthenticationContext is { Length: >= 32 and <= 256 } &&
                    request.AuthenticationContext.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') &&
                    PlatformReviewerContract.ValidBinding(request.Operation, request.ResourceId), "PLATFORM_EVALUATION_INVALID");
                VerifiedPlatformContext? identity = null;
                var failure = "IdentityInvalid";
                try { identity = await dependencies.Identity.ResolveAsync(request, http.RequestAborted).ConfigureAwait(false); }
                catch (PlatformAuthorizationException) { failure = "IdentityUnavailable"; }
                var decision = await dependencies.Store.EvaluateAsync(request, identity, failure, dependencies.Identity, http.RequestAborted).ConfigureAwait(false);
                return Results.Ok(decision);
            }));
        app.MapGet(prefix + "/entitlements", (HttpContext http, PlatformAuthorizationModule module) =>
            ExecuteAsync(http, module, true, async dependencies =>
                Results.Ok(await dependencies.Store.StatusAsync(http.RequestAborted).ConfigureAwait(false))));
        app.MapPost(prefix + "/entitlements", (HttpContext http, PlatformAuthorizationModule module) =>
            ExecuteAsync(http, module, true, async dependencies =>
            {
                var command = await ReadRequestAsync<PlatformEntitlementCommand>(http).ConfigureAwait(false);
                PlatformReviewerContract.Require(command.ContractVersion == "1.0.0" && command.Capability == PlatformReviewerContract.Capability &&
                    Guid.TryParseExact(command.OperationId, "D", out _) &&
                    PlatformReviewerContract.IsIdentifier(command.PrincipalId) && command.Action is "Grant" or "Revoke" &&
                    command.ExpectedPolicyRevision >= 0 && command.ReasonCode is "REVIEW_ASSIGNMENT" or "REVIEW_REMOVAL" or "SECURITY_RESPONSE",
                    "PLATFORM_ENTITLEMENT_COMMAND_INVALID");
                var principal = await dependencies.Identity.FindPrincipalAsync(command.PrincipalId, http.RequestAborted).ConfigureAwait(false);
                PlatformReviewerContract.Require(principal is not null && (command.Action == "Revoke" || principal.Active), "PLATFORM_PRINCIPAL_UNAVAILABLE", 409);
                return Results.Ok(await dependencies.Store.ApplyAsync(command, principal!, http.RequestAborted).ConfigureAwait(false));
            }));
        app.MapGet(prefix + "/operations/{operationId:guid}", (Guid operationId, HttpContext http, PlatformAuthorizationModule module) =>
            ExecuteAsync(http, module, true, async dependencies =>
                Results.Ok(await dependencies.Store.DeliveryAsync(operationId, http.RequestAborted).ConfigureAwait(false))));
        app.MapGet(prefix + "/delivery", (HttpContext http, PlatformAuthorizationModule module) =>
            ExecuteAsync(http, module, true, async dependencies =>
            {
                var query = http.Request.Query;
                PlatformReviewerContract.Require(query.All(item => item.Key is "after" or "limit" or "state" && item.Value.Count == 1), "PLATFORM_DELIVERY_QUERY_INVALID");
                Guid? after = null;
                if (query.TryGetValue("after", out var cursor))
                {
                    PlatformReviewerContract.Require(Guid.TryParseExact(cursor.ToString(), "D", out _), "PLATFORM_DELIVERY_QUERY_INVALID");
                    after = Guid.Parse(cursor.ToString());
                }
                var limit = 20;
                if (query.TryGetValue("limit", out var count))
                    PlatformReviewerContract.Require(int.TryParse(count.ToString(), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out limit), "PLATFORM_DELIVERY_QUERY_INVALID");
                return Results.Ok(await dependencies.Store.DiscoverDeliveryAsync(after, limit,
                    query.TryGetValue("state", out var state) ? state.ToString() : null, http.RequestAborted).ConfigureAwait(false));
            }));
        app.MapPost(prefix + "/delivery/{eventId:guid}/resume", (Guid eventId, HttpContext http, PlatformAuthorizationModule module) =>
            ExecuteAsync(http, module, true, async dependencies =>
                Results.Ok(await dependencies.Store.ResumeDeliveryAsync(eventId,
                    await ReadRequestAsync<PlatformAuditRecoveryRequest>(http).ConfigureAwait(false), http.RequestAborted).ConfigureAwait(false))));
    }

    private static async Task<IResult> ExecuteAsync(HttpContext http, PlatformAuthorizationModule module, bool operatorCommand,
        Func<PlatformReviewerDependencies, Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!module.Enabled) return Results.NotFound();
        try
        {
            var dependencies = module.GetDependencies();
            var name = operatorCommand ? "X-Monergy-AM-Platform-Operator" : "X-Monergy-AM-Platform";
            var credential = http.Request.Headers[name];
            var expected = operatorCommand ? dependencies.OperatorToken : dependencies.EvaluationToken;
            if (credential.Count != 1 || !ReferenceOwnerBoundary.TokenMatches(credential.ToString(), expected) ||
                http.Request.Headers.ContainsKey("Authorization") || http.Request.Headers.ContainsKey("Cookie") ||
                http.Request.Headers.Keys.Any(key => key.StartsWith("X-Monergy-", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return Results.Json(new { error = "PLATFORM_WORKLOAD_DENIED" }, statusCode: 403);
            return await action(dependencies).ConfigureAwait(false);
        }
        catch (PlatformAuthorizationException exception)
        {
            if (exception.StatusCode == 503) SafeFailure(http);
            return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (Exception exception) when (exception is NpgsqlException or System.Data.DataException or InvalidOperationException or FormatException or JsonException ||
            exception is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
        {
            SafeFailure(http);
            return Results.Json(new { error = "PLATFORM_REVIEW_DEPENDENCY_UNAVAILABLE" }, statusCode: 503);
        }
    }

    private static void SafeFailure(HttpContext http) => DependencyUnavailable(
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PlatformAuthorization"), null);

    private static async Task<T> ReadRequestAsync<T>(HttpContext http)
    {
        PlatformReviewerContract.Require(http.Request.Query.Count == 0 && http.Request.ContentLength is not > 4096 &&
            http.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true &&
            !http.Request.Headers.ContainsKey("Content-Encoding"), "PLATFORM_REQUEST_INVALID");
        using var buffer = new MemoryStream();
        var bytes = new byte[4097];
        while (true)
        {
            var count = await http.Request.Body.ReadAsync(bytes, http.RequestAborted).ConfigureAwait(false);
            if (count == 0) break;
            PlatformReviewerContract.Require(buffer.Length + count <= 4096, "PLATFORM_REQUEST_INVALID");
            buffer.Write(bytes, 0, count);
        }
        try
        {
            using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            PlatformReviewerContract.Require(json.RootElement.ValueKind == JsonValueKind.Object, "PLATFORM_REQUEST_INVALID");
            var names = json.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            PlatformReviewerContract.Require(names.Distinct(StringComparer.Ordinal).Count() == names.Length, "PLATFORM_REQUEST_INVALID");
            return json.RootElement.Deserialize<T>(PlatformReviewerContract.Json) ?? throw new JsonException();
        }
        catch (JsonException) { throw new PlatformAuthorizationException("PLATFORM_REQUEST_INVALID", 400); }
    }
}
