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
var tenantIntegration = TenantAccessIntegration.IsSelected(builder.Configuration);
if (tenantIntegration)
{
    if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Tenant integration is Development-only.");
    builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65_536);
    builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.RespectRequiredConstructorParameters = true);
    builder.Services.AddTenantSessionAuthority(builder.Configuration);
}
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

if (tenantIntegration) app.MapTenantSessionAuthority();

await app.RunAsync();

public partial class Program;

