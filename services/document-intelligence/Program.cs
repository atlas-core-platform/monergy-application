using Monergy.Platform;
using Monergy.Services.DocumentIntelligence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

const string serviceName = "Document Intelligence Service";

var builder = WebApplication.CreateBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
if (ReferenceAdapterGuard.IsSelected(builder.Configuration))
{
    builder.Services.AddDocumentIntelligenceReferenceAdapters(builder.Configuration);
}
builder.Services.AddHostedService<StartupWorker>();

var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");
if (ReferenceAdapterGuard.IsSelected(builder.Configuration))
    app.MapDocumentProcessing(builder.Configuration);
await app.RunAsync();

public partial class Program;
