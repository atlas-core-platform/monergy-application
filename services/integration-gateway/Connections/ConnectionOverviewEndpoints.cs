using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monergy.Platform;

namespace Monergy.Services.IntegrationGateway.Connections;

public static class ConnectionOverviewEndpoints
{
    public static void AddConnectionOverview(this WebApplicationBuilder builder)
    {
        TenantAccessIntegration.EnsureAllowed(builder.Configuration);
        builder.Services.TryAddSingleton<CustomerOwnerClient>();
        builder.Services.TryAddSingleton(provider => new TenantAccessClient(provider.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<IConnectionOverviewOwners, ConnectionOverviewOwners>();
        builder.Services.AddSingleton<IConnectionSummarySource, GoogleDriveConnectionSummarySource>();
        builder.Services.AddSingleton<IConnectionSummarySource, OneDriveConnectionSummarySource>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddTransient<ConnectionOverviewApplication>();
    }

    public static void MapConnectionOverview(this WebApplication app)
    {
        app.MapGet("/local/v1/connections", async (HttpContext http, ConnectionOverviewApplication service) =>
        {
            Secure(http);
            try { return Results.Ok(await service.GetAsync(http).ConfigureAwait(false)); }
            catch (TenantBoundaryException error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
            catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
            { return Results.Json(new { error = "CONNECTION_OVERVIEW_UNAVAILABLE" }, statusCode: 503); }
        });
    }

    private static void Secure(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    }
}
