using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Monergy.Platform;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class AdvisorCustomerLabelTests
{
    private static CustomerOwnerClient Client(Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
            ["Monergy:AccessIntegration:Enabled"] = "true",
            ["Monergy:AccessIntegration:CustomerIdentityUrl"] = "http://127.0.0.1:5101/",
            ["Monergy:AccessIntegration:AccessManagementUrl"] = "http://127.0.0.1:5088/",
            ["Monergy:AccessIntegration:CustomerContextToken"] = new string('a', 32),
            ["Monergy:AccessIntegration:CustomerIdentityToken"] = new string('b', 32),
            ["Monergy:AccessIntegration:MembershipToken"] = new string('c', 32)
        }).Build();
        return new CustomerOwnerClient(config, new HttpClient(new Reply(reply)));
    }

    [Fact]
    public async Task AuthorizedOwnerPortReturnsOnlyBoundedVerifiedDisplayLabel()
    {
        using var client = Client(req => {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("/internal/v1/customer-display/C001", req.RequestUri!.AbsolutePath);
            Assert.Equal("T001", req.Headers.GetValues("X-Monergy-Tenant").Single());
            Assert.Equal(new string('a', 32), req.Headers.GetValues("X-Monergy-Owner-Token").Single());
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = JsonContent.Create(new {
                    tenantId = "T001", customerId = "C001", displayName = "Verified Customer",
                    version = 2
                })
            };
        });
        var label = await client.CustomerLabelAsync("T001", "C001", default);
        Assert.Equal("Verified Customer", label.DisplayName);
        Assert.Equal(2, label.Version);
    }

    [Theory]
    [InlineData("T002", "C001", "Verified Customer", 2)]
    [InlineData("T001", "C002", "Verified Customer", 2)]
    [InlineData("T001", "C001", "", 2)]
    [InlineData("T001", "C001", "Verified Customer", 0)]
    public async Task MismatchedOrIncompleteOwnerLabelsFailClosed(
        string tenant, string customer, string name, long version)
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = JsonContent.Create(new {
                tenantId = tenant, customerId = customer, displayName = name, version
            })
        });
        var ex = await Assert.ThrowsAsync<TenantBoundaryException>(
            () => client.CustomerLabelAsync("T001", "C001", default));
        Assert.Equal("CUSTOMER_LABEL_INVALID", ex.Code);
        Assert.Equal(503, ex.Status);
    }

    private sealed class Reply(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
