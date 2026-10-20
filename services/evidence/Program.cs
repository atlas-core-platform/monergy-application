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

if (referenceAdapters && builder.Configuration.GetValue<bool>("Monergy:ProductIntake:Enabled"))
{
    builder.Services.AddHealthChecks().AddCheck<Monergy.Services.Evidence.Infrastructure.EvidenceScannerHealthCheck>("upload-malware-scanner");
    builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_500_000);
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.RespectRequiredConstructorParameters = true;
        options.SerializerOptions.RespectNullableAnnotations = true;
        options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
    });
}

var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");
if (referenceAdapters || physicalPersistence)
{
    app.MapEvidenceContracts();
}
if (referenceAdapters && builder.Configuration.GetValue<bool>("Monergy:ProductIntake:Enabled"))
{
    app.MapEvidenceReferenceIntake(builder.Configuration);
}
if (physicalPersistence && string.Equals(builder.Configuration["Monergy:D11:Profile"], "persisted-reporting",
    StringComparison.Ordinal))
{
    app.MapEvidenceLocalSetup(builder.Configuration);
    app.MapD11LocalShutdown(builder.Configuration);
}

if (TenantBoundaryOptions.Selected(builder.Configuration) && (referenceAdapters || physicalPersistence)) app.MapDocumentMonitoring();

await app.RunAsync();

public partial class Program;
