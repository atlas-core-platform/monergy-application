using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Monergy.Services.IntegrationGateway.GoogleDrive;
using Monergy.Services.IntegrationGateway.OneDrive;
using Xunit;

namespace Monergy.IntegrationGateway.Tests;

public sealed class DriveEndpointDiscoveryTests
{
    [Fact]
    public async Task EnabledGoogleDriveAndOneDriveEndpointsMaterialize()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
            ["Monergy:AccessIntegration:Enabled"] = "true",
            ["Monergy:GoogleDrive:Enabled"] = "true",
            ["Monergy:OneDrive:Enabled"] = "true"
        });
        builder.AddGoogleDrive();
        builder.AddOneDrive();
        await using var app = builder.Build();
        app.MapGoogleDrive();
        app.MapOneDrive();
        await app.StartAsync();

        // Enumerating endpoints forces ASP.NET minimal API parameter-binding discovery.
        // This previously threw for the non-static application method named BindAsync.
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText).ToArray();
        Assert.Contains("/local/v1/google-drive/folder", routes);
        Assert.Contains("/local/v1/onedrive/folder", routes);
        Assert.Contains("/local/v1/google-drive/import", routes);
        Assert.Contains("/local/v1/onedrive/import", routes);

        await app.StopAsync();
    }
}
