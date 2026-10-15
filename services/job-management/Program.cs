using Monergy.Platform;
using Monergy.Services.JobManagement;
using Monergy.Services.JobManagement.Infrastructure;

const string serviceName = "Job Management Service";

var builder = Host.CreateApplicationBuilder(args);
builder.AddMonergyPlatform(serviceName, hostWorkerTenantBoundary: false);
builder.Services.AddHealthChecks();

var physicalPersistence = PhysicalPersistenceGuard.IsSelected(builder.Configuration);
var referenceAdapter = ReferenceAdapterGuard.IsSelected(builder.Configuration);
var documentProcessing =
    referenceAdapter &&
    builder.Configuration.GetValue<bool>("Monergy:DocumentProcessing:Enabled");

if (physicalPersistence)
{
    builder.Services.AddJobManagementPhysicalPersistence(builder.Configuration);
}
else if (referenceAdapter)
{
    builder.Services.AddJobManagementReferenceAdapters(builder.Configuration);
}

builder.Services.AddHostedService<StartupWorker>();
if (physicalPersistence || referenceAdapter)
{
    builder.Services.AddHostedService<DurableJobExecutionWorker>();
    builder.Services.AddHostedService<JobOutboxDispatchWorker>();
}
builder.Services.AddHostedService<JobManagementHttpAdapterWorker>();

if (documentProcessing && !JobAuditDeliveryOptions.Selected(builder.Configuration))
    throw new InvalidOperationException("Document processing requires governed Job Audit delivery.");

var host = builder.Build();
if (documentProcessing)
{
    var registry = host.Services.GetRequiredService<ReferenceJobExecutionTargetRegistry>();
    registry.Register("Document Intelligence Service", "document-processing",
        host.Services.GetRequiredService<HttpDocumentProcessingTarget>());
}

await host.RunAsync();

public partial class Program;
