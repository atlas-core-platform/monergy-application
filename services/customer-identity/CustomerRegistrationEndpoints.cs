using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;
namespace Monergy.Services.CustomerIdentity;

public static class CustomerRegistrationEndpoints
{
    public static bool AddCustomerRegistration(this IServiceCollection services,IConfiguration config,bool emailEnabled)
    {
        if(config["Monergy:CustomerRegistration:Enabled"]!="true")return false;
        if(!emailEnabled)throw new InvalidOperationException("Registration requires configured email delivery.");
        TenantAccessIntegration.EnsureAllowed(config);
        services.AddSingleton<CustomerPasswordPolicy>();
        services.AddSingleton<ICustomerRegistrationRepository,PostgresCustomerRegistration>();
        services.AddSingleton<ICustomerEnrollmentClient,HttpCustomerEnrollment>();
        services.AddSingleton<CustomerRegistration>();
        services.AddHostedService<CustomerRegistrationDelivery>();
        return true;
    }
    public static void MapCustomerRegistration(this WebApplication app,bool enabled)
    {
        app.MapGet("/local/v1/customer-registration",()=>Results.Ok(new{enabled}));
        if(!enabled)return;
        var group=app.MapGroup("/local/v1/customer-registration").RequireRateLimiting("customer-email");
        group.AddEndpointFilter(async(context,next)=>
        {
            var http=context.HttpContext;http.Response.Headers.CacheControl="no-store";
            if(http.Request.Headers.Origin.Count>0&&http.Request.Headers.Origin.ToString()!=$"{http.Request.Scheme}://{http.Request.Host}")return Results.StatusCode(403);
            if(http.Request.ContentLength>8192)return Results.StatusCode(413);
            try{return await next(context);}
            catch(TenantAccessException e){return Results.Json(new{error=e.Code},statusCode:e.StatusCode);}
            catch(Exception e)when(e is System.Data.Common.DbException or HttpRequestException or InvalidOperationException){return Results.Json(new{error="REGISTRATION_UNAVAILABLE"},statusCode:503);}
        });
        group.MapGet("/agreements",(string tenantId,CustomerRegistration owner)=>owner.Publication(tenantId));
        group.MapPost("/request",(RegistrationRequest r,CustomerRegistration owner,HttpContext h)=>owner.RequestAsync(r,false,h.RequestAborted));
        group.MapPost("/confirm",(RegistrationConfirmation r,CustomerRegistration owner,HttpContext h)=>owner.ConfirmAsync(r,h.RequestAborted));
        group.MapPost("/sign-in",(PasswordSignIn r,CustomerRegistration owner,HttpContext h)=>owner.SignInAsync(r,h.RequestAborted));
        group.MapPost("/recovery/request",(RegistrationRequest r,CustomerRegistration owner,HttpContext h)=>owner.RequestAsync(r,true,h.RequestAborted));
        group.MapPost("/recovery/confirm",async(PasswordRecoveryConfirmation r,CustomerRegistration owner,HttpContext h)=>{await owner.RecoverAsync(r,h.RequestAborted);return Results.Ok(new{recovered=true});});
    }
}
