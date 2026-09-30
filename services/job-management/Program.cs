using Monergy.Platform;
using Monergy.Services.JobManagement;
using Monergy.Services.JobManagement.Infrastructure;

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
if (PhysicalPersistenceGuard.IsSelected(builder.Configuration) || ReferenceAdapterGuard.IsSelected(builder.Configuration))
{
    builder.Services.AddHostedService<DurableJobExecutionWorker>();
    builder.Services.AddHostedService<JobOutboxDispatchWorker>();
}

await builder.Build().RunAsync();
