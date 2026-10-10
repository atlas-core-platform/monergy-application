using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Monergy.Platform;

public sealed record TenantBoundaryProof(string TenantId, string ActorId, string SessionId,
    string CustomerId, string ContractName, string CapabilityId, string DecisionId);

public sealed class TenantBoundaryOptions
{
    public required string Service { get; init; }
    public required string CapabilityPrefix { get; init; }
    public required IReadOnlyDictionary<string, string> CustomerTenants { get; init; }
    public required string ProbeUrl { get; init; }
    public bool CustomerRelationships { get; init; }

    public static bool Selected(IConfiguration configuration) => configuration["Monergy:TenantBoundary:Enabled"] == "true";

    public static TenantBoundaryOptions Read(IConfiguration configuration, IHostEnvironment environment, string serviceName)
    {
        if (!environment.IsDevelopment() || configuration["Monergy:ExecutionZone"] is not ("LOCAL" or "CI_EPHEMERAL"))
            throw new InvalidOperationException("The local tenant boundary cannot run outside Development LOCAL/CI.");
        var service = serviceName switch
        {
            "Customer & Identity Service" => "customer-identity",
            "Consent Service" => "consent",
            "Evidence Service" => "evidence",
            "Document Intelligence Service" => "document-intelligence",
            "Financial Profile Service" => "financial-profile",
            "Financial Rules Service" => "financial-rules",
            "Search & Retrieval Service" => "search-retrieval",
            "Reporting Service" => "reporting",
            "Integration Gateway Service" => "integration-gateway",
            "Job Management Service" => "job-management",
            "Audit Service" => "audit",
            "AI Intelligence Service" => "ai-intelligence",
            _ => throw new InvalidOperationException("Unregistered service boundary."),
        };
        var customers = configuration.GetSection("Monergy:TenantBoundary:CustomerTenants").GetChildren()
            .ToDictionary(entry => entry.Key, entry => entry.Value ?? "", StringComparer.Ordinal);
        if (customers.Any(entry => !TenantAccessClient.Identifier(entry.Key, 128) || !TenantAccessClient.Identifier(entry.Value, 64)))
            throw new InvalidOperationException("Invalid operator-owned customer/tenant bindings.");
        var address = configuration["Monergy:TenantBoundary:ProbeUrl"] ?? "http://127.0.0.1:5199/";
        _ = TenantAccessIntegration.LoopbackRoot(address);
        return new()
        {
            Service = service,
            CapabilityPrefix = service == "search-retrieval" ? "search." : service + ".",
            CustomerTenants = customers,
            ProbeUrl = address,
            CustomerRelationships = configuration["Monergy:TenantBoundary:CustomerRelationships"] == "true"
        };
    }
}

public sealed class TenantBoundaryMiddleware(RequestDelegate next, TenantBoundaryOptions options)
{
    private static readonly JsonSerializerOptions ProbeJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };
    public async Task InvokeAsync(HttpContext http, TenantAccessClient access)
    {
        var path = http.Request.Path.Value ?? "";
        if (path.StartsWith("/health/", StringComparison.Ordinal) || path == "/internal/health/ready")
        { await next(http).ConfigureAwait(false); return; }
        var probe = path == "/local/v1/tenant-access/check";
        var contract = path.StartsWith("/contracts/", StringComparison.Ordinal);
        if (!probe && !contract) { await next(http).ConfigureAwait(false); return; }
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            if (!HttpMethods.IsPost(http.Request.Method)) throw new TenantBoundaryException("METHOD_NOT_ALLOWED", 405);
            var (tenant, session) = TenantAccessClient.Headers(http);
            http.Request.EnableBuffering(32_768, 262_144);
            using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
            http.Request.Body.Position = 0;
            RejectDuplicates(body.RootElement);
            if (probe)
            {
                var evaluation = body.RootElement.Deserialize<TenantAccessRequest>(ProbeJson)
                    ?? throw new TenantBoundaryException("INVALID_ACCESS_REQUEST", 400);
                if (!TenantAccessClient.Identifier(evaluation.CapabilityId, 160) || !evaluation.CapabilityId.StartsWith(options.CapabilityPrefix, StringComparison.Ordinal))
                    throw new TenantBoundaryException("CAPABILITY_BOUNDARY_MISMATCH", 400);
                var decision = await access.EvaluateAsync(http, evaluation).ConfigureAwait(false);
                http.Response.StatusCode = decision.Outcome == "Allow" ? 200 : 403;
                await http.Response.WriteAsJsonAsync(new { service = options.Service, decision, operationExecuted = false }, http.RequestAborted).ConfigureAwait(false);
                return;
            }
            var rule = Rule(options.Service, path);
            if (rule is null && options.CustomerRelationships && options.Service == "evidence" && path == "/contracts/cid-022/v1")
                rule = ("evidence.document.read", "document", "documentVersionId");
            if (rule is null) throw new TenantBoundaryException("CONTRACT_TENANT_ADAPTER_REQUIRED", 503);
            var security = body.RootElement.GetProperty("security");
            var actor = security.GetProperty("actor");
            var customer = Text(security.GetProperty("access"), "customerId");
            if (!options.CustomerTenants.TryGetValue(customer, out var owner) || owner != tenant)
                throw new TenantBoundaryException("CUSTOMER_TENANT_MISMATCH", 403);
            var payload = body.RootElement.GetProperty("payload");
            if (payload.TryGetProperty("customerId", out var payloadCustomer) && payloadCustomer.GetString() != customer)
                throw new TenantBoundaryException("CUSTOMER_CONTEXT_MISMATCH", 403);
            var resource = rule.Value.ResourceProperty switch
            {
                null => null,
                "$intent" => "intent:" + Text(body.RootElement, "idempotencyKey"),
                "$customer" => customer,
                var property => Text(payload, property),
            };
            string authorizedActor;
            string decisionId;
            if (options.CustomerRelationships)
            {
                var result = await access.EvaluateCustomerAsync(http, rule.Value.Capability, customer).ConfigureAwait(false);
                if (result.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);
                authorizedActor = result.ActorId; decisionId = result.DecisionId;
            }
            else
            {
                var result = await access.EvaluateAsync(http, new(rule.Value.Capability, rule.Value.ResourceType, resource)).ConfigureAwait(false);
                if (result.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);
                authorizedActor = result.ActorId; decisionId = result.DecisionId;
            }
            if (Text(actor, "actorId") != authorizedActor || Text(actor, "authenticationContextId") != session)
                throw new TenantBoundaryException("ACTOR_CONTEXT_MISMATCH", 403);
            http.Features.Set(new TenantBoundaryProof(tenant, authorizedActor, session, customer,
                Text(body.RootElement, "contractName"), rule.Value.Capability, decisionId));
        }
        catch (TenantBoundaryException failure)
        { http.Response.StatusCode = failure.Status; await http.Response.WriteAsJsonAsync(new { error = failure.Code }, http.RequestAborted).ConfigureAwait(false); return; }
        catch (Exception failure) when (failure is JsonException or KeyNotFoundException or InvalidOperationException)
        { http.Response.StatusCode = 400; await http.Response.WriteAsJsonAsync(new { error = "INVALID_TENANT_REQUEST" }, http.RequestAborted).ConfigureAwait(false); return; }
        catch (IOException)
        { http.Response.StatusCode = 413; return; }
        await next(http).ConfigureAwait(false);
    }

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()
        ?? throw new TenantBoundaryException("INVALID_TENANT_REQUEST", 400);

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!keys.Add(property.Name)) throw new TenantBoundaryException("DUPLICATE_JSON_FIELD", 400); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    private static (string Capability, string? ResourceType, string? ResourceProperty)? Rule(string service, string path) => (service, path) switch
    {
        ("evidence", "/contracts/cid-020/v1") => ("evidence.document.upload", "document", "documentId"),
        ("evidence", "/contracts/cid-021/v1") => ("evidence.document.read", "document", "documentId"),
        ("financial-profile", "/contracts/cid-030/v1" or "/contracts/cid-032/v1" or "/contracts/cid-033/v1") => ("financial-profile.profile.read", "customer", "$customer"),
        ("financial-profile", "/contracts/cid-031/v1") => ("financial-profile.profile.update", "customer", "$customer"),
        ("financial-rules", "/contracts/cid-037/v1" or "/contracts/cid-038/v1" or "/contracts/cid-039/v1") => ("financial-rules.calculation.execute", "customer", "$customer"),
        ("search-retrieval", "/contracts/cid-042/v1" or "/contracts/cid-043/v1" or "/contracts/cid-044/v1") => ("search.query.execute", null, null),
        ("reporting", "/contracts/cid-051/v1") => ("reporting.report.generate", "report", "$intent"),
        ("reporting", "/contracts/cid-052/v1") => ("reporting.report.read", "report", "reportId"),
        ("integration-gateway", "/contracts/cid-015/v1" or "/contracts/cid-016/v1") => ("integration-gateway.connection.manage", "customer", "$customer"),
        _ => null,
    };
}

internal sealed class TenantBoundaryStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    { app.UseMiddleware<TenantBoundaryMiddleware>(); next(app); };
}

internal sealed class TenantBoundaryWorkerHost(TenantBoundaryOptions options, TenantAccessClient client) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(options.ProbeUrl);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(client);
        await using var app = builder.Build();
        app.UseMiddleware<TenantBoundaryMiddleware>();
        app.MapGet("/health/live", () => Results.Ok(new { state = "running" }));
        app.MapGet("/health/ready", () => Results.Ok(new { state = "boundary-wired", businessConsumer = "not-claimed" }));
        await app.StartAsync(stoppingToken).ConfigureAwait(false);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
