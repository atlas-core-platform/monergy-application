using Monergy.Platform;
using Monergy.Services.JobManagement;

const string serviceName = "Job Management Service";

var builder = Host.CreateApplicationBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
if (PhysicalPersistenceGuard.IsSelected(builder.Configuration))
{
    builder.Services.AddJobManagementPhysicalPersistence(builder.Configuration);
}
else if (ReferenceAdapterGuard.IsSelected(builder.Configuration))
{
    builder.Services.AddJobManagementReferenceAdapters(builder.Configuration);
}
builder.Services.AddHostedService<StartupWorker>();

await builder.Build().RunAsync();
