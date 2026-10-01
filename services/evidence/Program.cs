using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Monergy.Platform;
using Monergy.Services.Evidence;

const string serviceName = "Evidence Service";

var builder = WebApplication.CreateBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
var referenceAdapters = ReferenceAdapterGuard.IsSelected(builder.Configuration);
if (referenceAdapters)
{
    builder.Services.AddEvidenceReferenceAdapters(builder.Configuration);
}
var physicalPersistence = PhysicalPersistenceGuard.IsSelected(builder.Configuration);
if (physicalPersistence)
{
    builder.Services.AddEvidencePhysicalPersistence(builder.Configuration);
}

var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");
if (referenceAdapters || physicalPersistence)
{
    app.MapEvidenceContracts();
}
if (physicalPersistence && string.Equals(builder.Configuration["Monergy:D11:Profile"], "persisted-reporting",
    StringComparison.Ordinal))
{
    app.MapEvidenceLocalSetup(builder.Configuration);
    app.MapD11LocalShutdown(builder.Configuration);
}

await app.RunAsync();

public partial class Program;
