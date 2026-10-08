using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Audit.Application;
using Monergy.Services.Audit.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Access Audit ingress is Development-only.");
TenantAccessIntegration.EnsureAllowed(builder.Configuration);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65_536);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
    options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});
builder.AddMonergyPlatform("Audit Service");
builder.Services.AddSingleton<PostgresTenantAccessAudit>();
builder.Services.AddSingleton<ITenantAccessAuditRepository>(services => services.GetRequiredService<PostgresTenantAccessAudit>());
builder.Services.AddSingleton<TenantAccessAudit>();
builder.Services.AddSingleton(provider => new TenantAccessClient(provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
var token = TenantAccessIntegration.Required(builder.Configuration, "Monergy:AccessIntegration:AuditEventToken");
TenantAccessIntegration.ValidateToken(token);
var loseReceipt = builder.Configuration["Monergy:AccessIntegration:VerificationLoseReceiptOnce"] == "true" ? 1 : 0;
var app = builder.Build();
app.MapGet("/local/v1/administration/access-events", async (HttpContext http, string? after,
    TenantAccessClient access, PostgresTenantAccessAudit repository) =>
{
    try
    {
        var administrator = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
        return Results.Ok(await repository
            .ListForAdministratorAsync(administrator.TenantId, after, http.RequestAborted).ConfigureAwait(false));
    }
    catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
    catch (TenantAccessException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode); }
    catch (System.Data.Common.DbException) { return Results.StatusCode(503); }
});
app.MapGet("/internal/health/ready", async (ITenantAccessAuditRepository repository, CancellationToken ct) =>
    await repository.ReadyAsync(ct).ConfigureAwait(false) ? Results.Ok() : (IResult)Results.StatusCode(503));
app.MapPost("/reference/events/audit", async (AccessManagementEvent message, HttpContext http, TenantAccessAudit audit) =>
{
    if (!TenantAccessIntegration.TokenMatches(http.Request.Headers["X-Monergy-Reference-Transport"].ToString(), token))
        return (IResult)Results.StatusCode(403);
    try
    {
        var receipt = await audit.ConsumeAsync(http.Request.Headers["X-Monergy-Reference-Tenant"].ToString(), message, http.RequestAborted).ConfigureAwait(false);
        if (Interlocked.Exchange(ref loseReceipt, 0) == 1)
        {
            http.Abort();
            return Results.Empty;
        }
        return Results.Ok(receipt);
    }
    catch (TenantAccessException exception) { return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode); }
    catch (System.Data.Common.DbException) { return Results.StatusCode(503); }
});
await app.RunAsync();
