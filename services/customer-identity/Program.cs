using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity;

const string serviceName = "Customer & Identity Service";

var builder = WebApplication.CreateBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = ContractJson.Options.PropertyNameCaseInsensitive;
    options.SerializerOptions.UnmappedMemberHandling = ContractJson.Options.UnmappedMemberHandling;
    options.SerializerOptions.WriteIndented = ContractJson.Options.WriteIndented;
});
var referenceAdapters = ReferenceAdapterGuard.IsSelected(builder.Configuration);
if (referenceAdapters)
{
    builder.Services.AddCustomerIdentityReferenceAdapters(builder.Configuration);
}

var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");
if (referenceAdapters)
{
    app.MapCustomerIdentityContracts();
}

await app.RunAsync();

public partial class Program;
