using Monergy.Platform;
using Monergy.Services.DocumentIntelligence;

const string serviceName = "Document Intelligence Service";

var builder = Host.CreateApplicationBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
if (ReferenceAdapterGuard.IsSelected(builder.Configuration))
{
    builder.Services.AddDocumentIntelligenceReferenceAdapters(builder.Configuration);
}
builder.Services.AddHostedService<StartupWorker>();
builder.Services.AddHostedService<DocumentIntelligenceHttpAdapterWorker>();

await builder.Build().RunAsync();

public partial class Program;
