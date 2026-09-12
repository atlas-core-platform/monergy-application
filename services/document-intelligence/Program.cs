using Monergy.Platform;
using Monergy.Services.DocumentIntelligence;

const string serviceName = "Document Intelligence Service";

var builder = Host.CreateApplicationBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
builder.Services.AddHostedService<StartupWorker>();

await builder.Build().RunAsync();
