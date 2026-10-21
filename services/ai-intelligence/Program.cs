using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Monergy.Platform;
using Monergy.Services.AiIntelligence.Qa;

const string serviceName = "AI Intelligence Service";

var builder = WebApplication.CreateBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
builder.AddFinancialQa();

var app = builder.Build();
app.MapFinancialQa();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");

await app.RunAsync();

public partial class Program;
