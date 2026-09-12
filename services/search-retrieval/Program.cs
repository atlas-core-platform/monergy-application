using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Monergy.Platform;

const string serviceName = "Search & Retrieval Service";

var builder = WebApplication.CreateBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");

await app.RunAsync();

public partial class Program;
