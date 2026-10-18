using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
using Monergy.UatGateway;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class CustomerSetupAdmissionTests
{
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Monergy:ExecutionZone"] = "CI_EPHEMERAL", ["Monergy:AccessIntegration:Enabled"] = "true",
        ["Monergy:AccessIntegration:CustomerIdentityUrl"] = "http://127.0.0.1:5101/",
        ["Monergy:AccessIntegration:AccessManagementUrl"] = "http://127.0.0.1:5088/",
        ["Monergy:AccessIntegration:CustomerContextToken"] = new string('a', 32),
        ["Monergy:AccessIntegration:CustomerIdentityToken"] = new string('b', 32),
        ["Monergy:AccessIntegration:MembershipToken"] = new string('c', 32),
    }).Build();
    [Fact]
    public async Task PendingOwnedCustomerIsReadableOnlyThroughExplicitSetupIdentityPort()
    {
        using var client = new HttpClient(new Handler(false)); using var owner = new CustomerOwnerClient(Configuration(), client);
        Assert.False((await owner.OwnedCustomerForSetupAsync("T001", "A100", default))!.WorkspaceReady);
        var failure = await Assert.ThrowsAsync<TenantBoundaryException>(() => owner.OwnedCustomerAsync("T001", "A100", default));
        Assert.Equal(403, failure.Status); Assert.Equal("CUSTOMER_SETUP_REQUIRED", failure.Code);
    }
    [Fact]
    public async Task MissingReadinessFlagFailsClosedRatherThanDefaultingToReady()
    {
        using var client = new HttpClient(new Handler(true) { MissingFlag = true }); using var owner = new CustomerOwnerClient(Configuration(), client);
        Assert.Equal(503, (await Assert.ThrowsAsync<TenantBoundaryException>(() => owner.OwnedCustomerAsync("T001", "A100", default))).Status);
    }
    [Theory]
    [InlineData("tenant")]
    [InlineData("customer")]
    [InlineData("ready")]
    [InlineData("revision")]
    public async Task InvalidSetupResponseCannotUnlockTheWorkspace(string mutation)
    {
        var handler = new Handler(true);
        handler.Setup = mutation switch
        {
            "tenant" => handler.Setup with { TenantId = "T002" },
            "customer" => handler.Setup with { CustomerId = "C900" },
            "ready" => handler.Setup with { ProfileReviewed = false },
            _ => handler.Setup with { Revision = null },
        };
        using var client = new HttpClient(handler); using var owner = new CustomerOwnerClient(Configuration(), client);
        Assert.Equal(503, (await Assert.ThrowsAsync<TenantBoundaryException>(() => owner.WorkspaceSetupAsync("T001", "A100", "C001", default))).Status);
    }
    [Fact]
    public async Task SetupWorkloadRequestRetainsItsOwnCredentialAndCurrentBinding()
    {
        var handler = new Handler(true); using var client = new HttpClient(handler); using var owner = new CustomerOwnerClient(Configuration(), client);
        Assert.True((await owner.WorkspaceSetupAsync("T001", "A100", "C001", default)).Ready);
        Assert.Equal(new string('a', 32), handler.LastToken); Assert.Equal("T001", handler.LastTenant);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealContractIngressCannotRunItsDownstreamActionUntilCurrentOwnerIsReady(bool ready)
    {
        using var client = new HttpClient(new Handler(ready)); using var owner = new CustomerOwnerClient(Configuration(), client);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); var downstream = 0;
        app.MapPost("/contracts/cid-020/v1", async (HttpContext http) =>
        {
            try { await CustomerSetupAdmission.RequireAsync(http, owner); downstream++; return Results.Ok(); }
            catch (TenantBoundaryException failure) { return Results.StatusCode(failure.Status); }
        });
        await app.StartAsync();
        using var browser = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        browser.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001"); browser.DefaultRequestHeaders.Add("X-Monergy-Session", "current-session");
        var response = await browser.PostAsJsonAsync("/contracts/cid-020/v1", new { });
        Assert.Equal(ready ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode); Assert.Equal(ready ? 1 : 0, downstream);
        await app.StopAsync();
    }
    private sealed class Handler(bool ready) : HttpMessageHandler
    {
        public bool MissingFlag { get; set; }
        public string? LastToken { get; private set; }
        public string? LastTenant { get; private set; }
        public CustomerWorkspaceSetupStatus Setup { get; set; } = new("T001", "A100", "C001", true, true, true, true, true, true, new string('a', 64));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastToken = request.Headers.GetValues("X-Monergy-Owner-Token").Single(); LastTenant = request.Headers.GetValues("X-Monergy-Tenant").Single();
            object body = request.RequestUri!.AbsolutePath switch
            {
                "/internal/v1/tenant-sessions/validate" => new CustomerOwnerSession("T001", "A100", "current-session", 3, DateTimeOffset.UtcNow.AddMinutes(30)),
                "/internal/v1/customer-workspace-setup/A100" => Setup,
                _ when MissingFlag => new { tenantId = "T001", customerId = "C001", displayName = "Customer", secondaryLabel = (string?)null, ownerActorId = "A100", version = 1, active = true },
                _ => new CustomerOwnerWorkspace("T001", "C001", "Customer", null, "A100", 1, true) { WorkspaceReady = ready },
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
}
