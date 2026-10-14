using Monergy.Platform;
using Monergy.Services.JobManagement;
using Monergy.Services.JobManagement.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

const string serviceName = "Job Management Service";

var builder = WebApplication.CreateBuilder(args);
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

var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");
if (ReferenceAdapterGuard.IsSelected(builder.Configuration) &&
    builder.Configuration.GetValue<bool>("Monergy:DocumentProcessing:Enabled"))
{
    if (!JobAuditDeliveryOptions.Selected(builder.Configuration))
        throw new InvalidOperationException("Document processing requires governed Job Audit delivery.");
    var registry = app.Services.GetRequiredService<ReferenceJobExecutionTargetRegistry>();
    registry.Register("Document Intelligence Service", "document-processing",
        app.Services.GetRequiredService<HttpDocumentProcessingTarget>());
    app.MapDocumentProcessingJobs(builder.Configuration);
}
await app.RunAsync();

public partial class Program;
