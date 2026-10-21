using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Monergy.Platform;
using Monergy.Services.IntegrationGateway;
using Monergy.Services.IntegrationGateway.GoogleDrive;

const string serviceName = "Integration Gateway Service";

var builder = WebApplication.CreateBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
builder.AddGoogleDrive();
Monergy.Services.IntegrationGateway.OneDrive.DriveConnectionEndpoints.AddOneDrive(builder);
var referenceAdapters = ReferenceAdapterGuard.IsSelected(builder.Configuration);
if (referenceAdapters)
{
    builder.Services.AddIntegrationGatewayReferenceAdapters(builder.Configuration);
}

var app = builder.Build();
app.MapGoogleDrive();
Monergy.Services.IntegrationGateway.OneDrive.DriveConnectionEndpoints.MapOneDrive(app);
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");
if (referenceAdapters)
{
    app.MapIntegrationGatewayContracts();
}

await app.RunAsync();

public partial class Program;
