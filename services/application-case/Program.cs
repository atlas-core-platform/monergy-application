using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Monergy.Platform;
using Monergy.Services.ApplicationCase;
using Monergy.Services.ApplicationCase.Application;

const string serviceName = "Application & Case Service";

var builder = WebApplication.CreateBuilder(args);
builder.AddMonergyPlatform(serviceName);
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
    options.SerializerOptions.RespectNullableAnnotations = true;
    options.SerializerOptions.UnmappedMemberHandling =
        System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
});
builder.Services.AddApplicationCaseReferenceAdapters(builder.Configuration);

var app = builder.Build();
app.Use(async (http, next) =>
{
    http.Response.Headers.CacheControl = "no-store";
    try { await next(http).ConfigureAwait(false); }
    catch (ApplicationCaseException failure)
    {
        http.Response.StatusCode = failure.Status;
        await http.Response.WriteAsJsonAsync(new { error = failure.Code }, http.RequestAborted)
            .ConfigureAwait(false);
    }
    catch (TenantBoundaryException failure)
    {
        http.Response.StatusCode = failure.Status;
        await http.Response.WriteAsJsonAsync(new { error = failure.Code }, http.RequestAborted)
            .ConfigureAwait(false);
    }
});
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");
app.MapApplicationCases();
await app.RunAsync();

public partial class Program;
