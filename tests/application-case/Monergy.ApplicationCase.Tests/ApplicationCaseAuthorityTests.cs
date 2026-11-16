using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Monergy.Platform;
using Monergy.Services.ApplicationCase;
using Monergy.Services.ApplicationCase.Application;
using Xunit;

namespace Monergy.ApplicationCase.Tests;

public sealed class ApplicationCaseAuthorityTests
{
    [Fact]
    public async Task AdvisorEngagementUsesTenantCapabilityAndCustomerConsentOnlyForSharedSources()
    {
        var ownerHandler = new OwnerHandler();
        var accessHandler = new AccessHandler();
        var configuration = Configuration();
        using var owners = new CustomerOwnerClient(configuration, new HttpClient(ownerHandler));
        using var access = new TenantAccessClient(configuration, new HttpClient(accessHandler));
        using var consent = new FinancialProfessionalConsentClient(configuration,
            new HttpClient(new RejectingHandler()));
        var authority = new ApplicationCaseAuthority(owners, access, consent);
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Monergy-Tenant"] = "T001";
        http.Request.Headers["X-Monergy-Session"] = "session-001";

        var result = await authority.RequireAdvisorEngagementAsync(http, "reference-customer");

        Assert.Equal("A300", result.ActorId);
        Assert.Equal(7, result.PolicyVersion);
        Assert.Equal(3, result.SubjectVersion);
        Assert.Equal(4, result.CustomerVersion);
        Assert.Equal(5, result.ConsentVersion);
        Assert.Equal([
            "evidence.document.read",
            "financial-profile.profile.read",
        ], accessHandler.CustomerCapabilities.Order(StringComparer.Ordinal));
        Assert.Equal([AdvisorEngagementPolicy.Capability], accessHandler.TenantCapabilities);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monergy:ExecutionZone"] = "LOCAL",
            ["Monergy:AccessIntegration:Enabled"] = "true",
            ["Monergy:AccessIntegration:CustomerIdentityUrl"] = "http://127.0.0.1:5101/",
            ["Monergy:AccessIntegration:AccessManagementUrl"] = "http://127.0.0.1:5088/",
            ["Monergy:TenantAccess:AccessManagementUrl"] = "http://127.0.0.1:5088/",
            ["Monergy:AccessIntegration:CustomerContextToken"] = new string('a', 32),
            ["Monergy:AccessIntegration:CustomerIdentityToken"] = new string('b', 32),
            ["Monergy:AccessIntegration:MembershipToken"] = new string('c', 32),
            ["Monergy:ApplicationCase:ConsentUrl"] = "http://127.0.0.1:5107/",
            ["Monergy:ApplicationCase:ConsentToken"] = new string('d', 32),
        }).Build();

    private sealed class OwnerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object body = request.RequestUri!.AbsolutePath switch
            {
                "/internal/v1/tenant-sessions/validate" => new CustomerOwnerSession(
                    "T001", "A300", "session-001", 3, DateTimeOffset.UtcNow.AddMinutes(10)),
                "/internal/v1/customer-context/reference-customer" => new CustomerOwnerContext(
                    "T001", "reference-customer", "A100", 4, true),
                _ => throw new InvalidOperationException("Unexpected owner route: " + request.RequestUri.AbsolutePath),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body),
            });
        }
    }

    private sealed class AccessHandler : HttpMessageHandler
    {
        public List<string> CustomerCapabilities { get; } = [];
        public List<string> TenantCapabilities { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (path == "/v1/advisor/context")
            {
                body = new AdvisorEntryContext("T001", "A300", "FinancialAdvisor", 7, 3, 1);
            }
            else if (path == "/v1/authorization/customer-evaluations")
            {
                using var json = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(ct),
                    cancellationToken: ct);
                var capability = json.RootElement.GetProperty("capabilityId").GetString()!;
                CustomerCapabilities.Add(capability);
                if (capability == AdvisorEngagementPolicy.Capability)
                    throw new InvalidOperationException("Engagement must not use the customer Consent evaluator.");
                body = new CustomerAccessDecision("decision-" + CustomerCapabilities.Count, "Allow", "T001", "A300",
                    "reference-customer", capability, "customer-advice", "Granted", "Advisor",
                    7, 3, 4, 5, DateTimeOffset.UtcNow);
            }
            else if (path == "/v1/authorization/evaluations")
            {
                using var json = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(ct),
                    cancellationToken: ct);
                var capability = json.RootElement.GetProperty("capabilityId").GetString()!;
                Assert.Equal("customer", json.RootElement.GetProperty("resourceType").GetString());
                Assert.Equal("reference-customer", json.RootElement.GetProperty("resourceId").GetString());
                TenantCapabilities.Add(capability);
                body = new TenantAccessDecision("decision-engagement", "Allow", "T001", "A300", capability,
                    "customer", "reference-customer", "Granted", 7, 3, "catalog-v1", DateTimeOffset.UtcNow, null,
                    "NoStore", "correlation-001");
            }
            else
            {
                throw new InvalidOperationException("Unexpected access route: " + path);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("The advisor path must not call the professional Consent client.");
    }
}
