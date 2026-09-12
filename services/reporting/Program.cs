using Monergy.Platform;
using Monergy.Services.Reporting;

const string serviceName = "Reporting Service";

var builder = Host.CreateApplicationBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
builder.Services.AddHostedService<StartupWorker>();

await builder.Build().RunAsync();
