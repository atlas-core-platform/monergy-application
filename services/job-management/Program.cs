using Monergy.Platform;
using Monergy.Services.JobManagement;

const string serviceName = "Job Management Service";

var builder = Host.CreateApplicationBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
builder.Services.AddHostedService<StartupWorker>();

await builder.Build().RunAsync();
