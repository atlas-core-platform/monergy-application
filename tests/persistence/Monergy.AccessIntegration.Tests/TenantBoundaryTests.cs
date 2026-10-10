using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Monergy.Platform;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class TenantBoundaryTests
{
    [Theory]
    [InlineData("Allow", "C001", 1, 200)]
    [InlineData("Deny", "C001", 1, 403)]
    [InlineData("Allow", "C002", 1, 503)]
    [InlineData("Allow", "C001", 0, 503)]
    public async Task RelationshipModeUsesBoundCustomerConsentDecisionWithoutLegacyFallback(string outcome, string customer, long consentVersion, int expected)
    {
        var invoked = false;
        var options = Options();
        var middleware = new TenantBoundaryMiddleware(http => { invoked = true; return Task.CompletedTask; }, new()
        {
            Service = options.Service,
            CapabilityPrefix = options.CapabilityPrefix,
            ProbeUrl = options.ProbeUrl,
            CustomerTenants = options.CustomerTenants,
            CustomerRelationships = true,
        });
        var http = Http(); http.Request.Path = "/contracts/cid-021/v1";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"contractName":"GetDocument","security":{"actor":{"actorId":"A100","authenticationContextId":"token"},"access":{"customerId":"C001"}},"payload":{"customerId":"C001","documentId":"D001"}}
            """));
        using var client = Client(new CustomerAccessDecision("decision-1", outcome, "T001", "A100", customer, "evidence.document.read",
            "customer-advice", outcome == "Allow" ? "Granted" : "ConsentRequired", "Advisor", 3, 2, 1, consentVersion, DateTimeOffset.UtcNow));
        await middleware.InvokeAsync(http, client);
        Assert.Equal(expected, http.Response.StatusCode); Assert.Equal(expected == 200, invoked);
    }

    [Fact]
    public async Task AdministratorContextRejectsAnotherTenantAndMissingHeaders()
    {
        using var client = Client(new TenantAdminContext("T002", "A900", 3, "TenantAdmin"));
        var error = await Assert.ThrowsAsync<TenantBoundaryException>(() => client.RequireAdministratorAsync(Http()));
        Assert.Equal(503, error.Status);
        var missing = new DefaultHttpContext();
        error = await Assert.ThrowsAsync<TenantBoundaryException>(() => client.RequireAdministratorAsync(missing));
        Assert.Equal(401, error.Status);
    }

    [Fact]
    public async Task DecisionCannotBeReusedForAnotherResourceOrExpiredTimestamp()
    {
        var request = new TenantAccessRequest("evidence.document.read", "document", "D001");
        using var wrong = Client(Decision() with { ResourceId = "D002" });
        Assert.Equal(503, (await Assert.ThrowsAsync<TenantBoundaryException>(() => wrong.EvaluateAsync(Http(), request))).Status);
        using var stale = Client(Decision() with { EvaluatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        Assert.Equal(503, (await Assert.ThrowsAsync<TenantBoundaryException>(() => stale.EvaluateAsync(Http(), request))).Status);
        using var duplicate = Client(Decision());
        var http = Http(); http.Request.Headers.Append("X-Monergy-Tenant", "T002");
        Assert.Equal(401, (await Assert.ThrowsAsync<TenantBoundaryException>(() => duplicate.EvaluateAsync(http, request))).Status);
    }

    [Theory]
    [InlineData("T002", "A100", "token", 403)]
    [InlineData("T001", "forged", "token", 403)]
    [InlineData("T001", "A100", "forged", 403)]
    [InlineData("T001", "A100", "token", 200)]
    public async Task ContractRequiresCustomerBindingAndCanonicalActor(string tenant, string actor, string bodySession, int expected)
    {
        var invoked = false;
        var middleware = new TenantBoundaryMiddleware(http => { invoked = true; Assert.NotNull(http.Features.Get<TenantBoundaryProof>()); return Task.CompletedTask; }, Options());
        var http = Http(); http.Request.Headers["X-Monergy-Tenant"] = tenant; http.Request.Path = "/contracts/cid-021/v1";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($$$"""
            {"contractName":"GetDocument","security":{"actor":{"actorId":"{{{actor}}}","authenticationContextId":"{{{bodySession}}}"},"access":{"customerId":"C001"}},"payload":{"customerId":"C001","documentId":"D001"}}
            """));
        using var client = Client(Decision());
        await middleware.InvokeAsync(http, client);
        Assert.Equal(expected, http.Response.StatusCode); Assert.Equal(expected == 200, invoked);
    }

    [Theory]
    [InlineData("/contracts/cid-022/v1", "{}", 503)]
    [InlineData("/local/v1/tenant-access/check", "{\"capabilityId\":\"audit.evidence.read\",\"resourceType\":null,\"resourceId\":null}", 400)]
    [InlineData("/local/v1/tenant-access/check", "{\"capabilityId\":\"evidence.document.read\",\"capabilityId\":\"evidence.document.read\",\"resourceType\":\"document\",\"resourceId\":\"D001\"}", 400)]
    public async Task UnknownContractOtherServiceAndAmbiguousJsonFailClosed(string path, string body, int expected)
    {
        var middleware = new TenantBoundaryMiddleware(_ => throw new InvalidOperationException("Owner must not execute."), Options());
        var http = Http(); http.Request.Path = path; http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        using var client = Client(Decision()); await middleware.InvokeAsync(http, client);
        Assert.Equal(expected, http.Response.StatusCode);
    }

    [Fact]
    public async Task CapabilityProbeReturnsDecisionWithoutExecutingBusinessOperation()
    {
        var middleware = new TenantBoundaryMiddleware(_ => throw new InvalidOperationException("Probe must not execute."), Options());
        var http = Http(); http.Request.Path = "/local/v1/tenant-access/check";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"capabilityId\":\"evidence.document.read\",\"resourceType\":\"document\",\"resourceId\":\"D001\"}"));
        using var client = Client(Decision()); await middleware.InvokeAsync(http, client);
        Assert.Equal(200, http.Response.StatusCode); http.Response.Body.Position = 0;
        using var body = await System.Text.Json.JsonDocument.ParseAsync(http.Response.Body);
        Assert.False(body.RootElement.GetProperty("operationExecuted").GetBoolean());
    }
    private static TenantBoundaryOptions Options() => new() { Service = "evidence", CapabilityPrefix = "evidence.", ProbeUrl = "http://127.0.0.1:5111/", CustomerTenants = new Dictionary<string, string> { ["C001"] = "T001" } };
    private static DefaultHttpContext Http()
    {
        var http = new DefaultHttpContext(); http.Request.Method = "POST";
        http.Request.Headers["X-Monergy-Tenant"] = "T001"; http.Request.Headers["X-Monergy-Session"] = "token";
        http.Response.Body = new MemoryStream(); return http;
    }
    private static TenantAccessDecision Decision() => new("decision-1", "Allow", "T001", "A100", "evidence.document.read", "document", "D001", "Granted", 3, 2, "catalog", DateTimeOffset.UtcNow, null, "NoCache", "correlation-1");
    private static TenantAccessClient Client(object value) => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Monergy:ExecutionZone"] = "CI_EPHEMERAL" }).Build(), new HttpClient(new ResponseHandler(value)));
    private sealed class ResponseHandler(object value) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) });
    }
}
