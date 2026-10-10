using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Monergy.Platform;
using Monergy.Services.Consent;
using System.Text.Json.Serialization;

const string serviceName = "Consent Service";

var builder = WebApplication.CreateBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
var customerConsent = builder.Configuration["Monergy:AccessIntegration:CustomerRelationships"] == "true";
if (customerConsent)
{
    if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Customer Consent requires Development LOCAL/CI.");
    TenantAccessIntegration.EnsureAllowed(builder.Configuration);
    builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65_536);
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.RespectRequiredConstructorParameters = true;
        options.SerializerOptions.RespectNullableAnnotations = true;
        options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    });
    builder.Services.AddSingleton<PostgresCustomerConsent>();
    builder.Services.AddSingleton<CustomerOwnerClient>();
}

var app = builder.Build();
if (customerConsent) app.MapCustomerConsent();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
if (!customerConsent) app.MapHealthChecks("/health/ready");

await app.RunAsync();

public partial class Program;
