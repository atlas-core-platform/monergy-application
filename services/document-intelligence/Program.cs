using Monergy.Platform;
using Monergy.Services.DocumentIntelligence;

const string serviceName = "Document Intelligence Service";

var builder = Host.CreateApplicationBuilder(args);
builder.AddMonergyPlatform(serviceName, hostWorkerTenantBoundary: false);
builder.Services.AddHealthChecks();
var referenceAdapter = ReferenceAdapterGuard.IsSelected(builder.Configuration);
var documentProcessing =
    referenceAdapter &&
    !string.IsNullOrWhiteSpace(builder.Configuration["Monergy:DocumentProcessing:ControlToken"]);
var secondaryHttp = TenantBoundaryOptions.Selected(builder.Configuration) || documentProcessing;

if (referenceAdapter)
{
    builder.Services.AddDocumentIntelligenceReferenceAdapters(builder.Configuration);
}
builder.Services.AddHostedService<StartupWorker>();
if (secondaryHttp)
    builder.Services.AddHostedService<DocumentIntelligenceHttpAdapterWorker>();

await builder.Build().RunAsync();

public partial class Program;
