using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Audit.Application;
using Monergy.Services.Audit.Infrastructure;
using Monergy.Services.Audit;

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
builder.Services.AddSingleton<ITenantJobAuditRepository>(services => services.GetRequiredService<PostgresTenantAccessAudit>());
builder.Services.AddSingleton<ITenantFinancialAuditRepository>(services => services.GetRequiredService<PostgresTenantAccessAudit>());
builder.Services.AddSingleton<IProductAuditLog>(services => services.GetRequiredService<PostgresTenantAccessAudit>());
builder.Services.AddSingleton<TenantAccessAudit>();
builder.Services.AddSingleton<TenantJobAudit>();
builder.Services.AddSingleton<TenantFinancialAudit>();
builder.Services.AddSingleton(provider => new TenantAccessClient(provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
var token = TenantAccessIntegration.Required(builder.Configuration, "Monergy:AccessIntegration:AuditEventToken");
TenantAccessIntegration.ValidateToken(token);
var jobToken = TenantAccessIntegration.Required(builder.Configuration, "Monergy:AccessIntegration:JobAuditEventToken");
TenantAccessIntegration.ValidateToken(jobToken);
var financialToken = TenantAccessIntegration.Required(builder.Configuration, "Monergy:AccessIntegration:FinancialAuditEventToken");
TenantAccessIntegration.ValidateToken(financialToken);
var productToken=TenantAccessIntegration.Required(builder.Configuration,"Monergy:AccessIntegration:ProductAuditEventToken");
TenantAccessIntegration.ValidateToken(productToken);
if (new[] { token, jobToken, financialToken,productToken }.Distinct(StringComparer.Ordinal).Count() != 4)
    throw new InvalidOperationException("Audit event producers require separate workload credentials.");
var loseReceipt = builder.Configuration["Monergy:AccessIntegration:VerificationLoseReceiptOnce"] == "true" ? 1 : 0;
var app = builder.Build();
app.MapProductAuditLog();
app.MapPost("/reference/events/product-activity",async(ProductAuditRecord record,HttpContext http,IProductAuditLog ledger)=>{
 if(!TenantAccessIntegration.TokenMatches(http.Request.Headers["X-Monergy-Reference-Transport"].ToString(),productToken)||http.Request.Headers["X-Monergy-Reference-Tenant"].Count!=1||http.Request.Headers["X-Monergy-Reference-Tenant"].ToString()!=record.TenantId)return (IResult)Results.StatusCode(403);
 try{return Results.Ok(await ledger.AppendActivityAsync(record,http.RequestAborted));}
 catch(TenantBoundaryException e){return Results.Json(new{error=e.Code},statusCode:e.Status);}
 catch(TenantAccessException e){return Results.Json(new{error=e.Code},statusCode:e.StatusCode);}
 catch(System.Data.Common.DbException){return Results.StatusCode(503);}
});
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
app.MapPost("/reference/events/job-audit", async (AuditableEvent message, HttpContext http, TenantJobAudit audit) =>
{
    if (!TenantAccessIntegration.TokenMatches(http.Request.Headers["X-Monergy-Reference-Transport"].ToString(), jobToken))
        return (IResult)Results.StatusCode(403);
    try
    {
        var receipt = await audit.ConsumeAsync(
            http.Request.Headers["X-Monergy-Reference-Tenant"].ToString(),
            message,
            http.RequestAborted).ConfigureAwait(false);
        return Results.Ok(receipt);
    }
    catch (ArgumentException) { return Results.BadRequest(new { error = "JOB_AUDIT_EVENT_INVALID" }); }
    catch (TenantAccessException exception) { return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode); }
    catch (System.Data.Common.DbException) { return Results.StatusCode(503); }
});
app.MapPost("/reference/events/financial-audit", async (AuditableEvent message, HttpContext http, TenantFinancialAudit audit) =>
{
    if (!TenantAccessIntegration.TokenMatches(http.Request.Headers["X-Monergy-Reference-Transport"].ToString(), financialToken))
        return (IResult)Results.StatusCode(403);
    try
    {
        var receipt = await audit.ConsumeAsync(
            http.Request.Headers["X-Monergy-Reference-Tenant"].ToString(),
            message,
            http.RequestAborted).ConfigureAwait(false);
        return Results.Ok(receipt);
    }
    catch (ArgumentException) { return Results.BadRequest(new { error = "FINANCIAL_AUDIT_EVENT_INVALID" }); }
    catch (TenantAccessException exception) { return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode); }
    catch (System.Data.Common.DbException) { return Results.StatusCode(503); }
});
await app.RunAsync();
