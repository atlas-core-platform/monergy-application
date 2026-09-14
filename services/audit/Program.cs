using Monergy.Platform;
using Monergy.Services.Audit;

const string serviceName = "Audit Service";

var builder = Host.CreateApplicationBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
if (ReferenceAdapterGuard.IsSelected(builder.Configuration))
{
    builder.Services.AddAuditReferenceAdapters(builder.Configuration);
}
builder.Services.AddHostedService<StartupWorker>();

await builder.Build().RunAsync();
