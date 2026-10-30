using Microsoft.AspNetCore.Http;
using Monergy.Platform;
using Monergy.Services.IntegrationGateway.Connections;
using Xunit;

namespace Monergy.IntegrationGateway.Tests;

public sealed class ConnectionOverviewTests
{
    [Fact]
    public async Task CurrentCustomerReceivesCanonicalProviderStatesInStableOrder()
    {
        var app = new ConnectionOverviewApplication(new Owners(),
        [
            new Source("onedrive", "OneDrive", "/connections/onedrive", new("Needs attention")),
            new Source("google-drive", "Google Drive", "/connections/google-drive",
                new("Connected", DateTimeOffset.Parse("2026-10-30T06:00:00Z"))),
        ], new Clock());

        var result = await app.GetAsync(new DefaultHttpContext());

        Assert.Equal("T001", result.TenantId);
        Assert.Equal("C001", result.CustomerId);
        Assert.Equal(["google-drive", "onedrive"], result.Connections.Select(item => item.ProviderCode));
        Assert.Equal("Connected", result.Connections[0].State);
        Assert.Equal("Current", result.Connections[0].PermissionState);
        Assert.Equal("Status available", result.Connections[0].HealthState);
        Assert.Equal("Review required", result.Connections[1].PermissionState);
        Assert.Equal("Attention required", result.Connections[1].HealthState);
        Assert.All(result.Connections, item => Assert.Equal(DateTimeOffset.Parse("2026-10-30T06:15:00Z"), item.CheckedAt));
        Assert.NotNull(result.Connections[0].LastConfirmedAt);
        Assert.Null(result.Connections[1].LastConfirmedAt);
    }

    [Fact]
    public async Task OneProviderFailureDoesNotExposeDetailsOrHideTheOtherProvider()
    {
        var app = new ConnectionOverviewApplication(new Owners(),
        [
            new Source("google-drive", "Google Drive", "/connections/google-drive", new("Connected")),
            new Source("onedrive", "OneDrive", "/connections/onedrive", failure: new TenantBoundaryException("SECRET_OWNER_FAILURE", 503)),
        ], new Clock());

        var result = await app.GetAsync(new DefaultHttpContext());

        Assert.Equal("Connected", result.Connections[0].State);
        Assert.Equal("Unavailable", result.Connections[1].State);
        Assert.Equal("Unavailable", result.Connections[1].PermissionState);
        Assert.Equal("Status unavailable", result.Connections[1].HealthState);
        Assert.DoesNotContain("SECRET_OWNER_FAILURE", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthorizationFailureStopsBeforeAnyProviderRead()
    {
        var source = new Source("google-drive", "Google Drive", "/connections/google-drive", new("Connected"));
        var app = new ConnectionOverviewApplication(new Owners(new TenantBoundaryException("ACCESS_DENIED", 403)), [source], new Clock());

        var failure = await Assert.ThrowsAsync<TenantBoundaryException>(() => app.GetAsync(new DefaultHttpContext()));

        Assert.Equal(403, failure.Status);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task UnknownProviderStateFailsClosedWithoutLeakingProviderPayload()
    {
        var app = new ConnectionOverviewApplication(new Owners(),
            [new Source("google-drive", "Google Drive", "/connections/google-drive", new("provider-secret-state"))], new Clock());

        var result = await app.GetAsync(new DefaultHttpContext());

        Assert.Equal("Unavailable", Assert.Single(result.Connections).State);
        Assert.Equal("Unavailable", Assert.Single(result.Connections).PermissionState);
        Assert.Equal("Status unavailable", Assert.Single(result.Connections).HealthState);
        Assert.DoesNotContain("provider-secret-state", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Not configured", "Not requested")]
    [InlineData("Ready to connect", "Not requested")]
    [InlineData("Setup in progress", "Pending")]
    [InlineData("Connected", "Current")]
    [InlineData("Needs attention", "Review required")]
    [InlineData("Unavailable", "Unavailable")]
    public void PermissionReadinessIsCanonicalAndCannotGrantConsent(string state, string expected)
    {
        Assert.Equal(expected, ConnectionPermissionProjection.From(state));
        Assert.Equal("Unavailable", ConnectionPermissionProjection.From("provider-private-permission"));
    }

    [Theory]
    [InlineData("Not configured", "Not configured")]
    [InlineData("Ready to connect", "Status available")]
    [InlineData("Setup in progress", "Status available")]
    [InlineData("Connected", "Status available")]
    [InlineData("Needs attention", "Attention required")]
    [InlineData("Unavailable", "Status unavailable")]
    public void OperationalHealthIsCanonicalAndDoesNotExposeCounters(string state, string expected)
    {
        Assert.Equal(expected, ConnectionHealthProjection.From(state));
        Assert.Equal("Status unavailable", ConnectionHealthProjection.From("provider-private-health"));
    }

    private sealed class Owners(Exception? failure = null) : IConnectionOverviewOwners
    {
        public Task<ConnectionOverviewOwner> AuthorizeAsync(HttpContext http) => failure is null
            ? Task.FromResult(new ConnectionOverviewOwner("T001", "A001", "C001", 3))
            : Task.FromException<ConnectionOverviewOwner>(failure);
    }

    private sealed class Source(string code, string label, string path, ConnectionSourceSnapshot? value = null,
        Exception? failure = null) : IConnectionSummarySource
    {
        public int Reads { get; private set; }
        public string ProviderCode => code;
        public string Provider => label;
        public string ManagePath => path;
        public Task<ConnectionSourceSnapshot> ReadAsync(HttpContext http)
        {
            Reads++;
            return failure is null ? Task.FromResult(value!) : Task.FromException<ConnectionSourceSnapshot>(failure);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-30T06:15:00Z");
    }
}
