using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;

namespace Monergy.Services.CustomerIdentity;

public static class CustomerEmailAccessEndpoints
{
    public static bool AddCustomerEmailAccess(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration["Monergy:CustomerEmailAccess:Enabled"] != "true") return false;
        TenantAccessIntegration.EnsureAllowed(configuration);
        if (configuration["Monergy:AccessIntegration:CustomerRelationships"] != "true")
            throw new InvalidOperationException("Email access requires the verified customer ownership boundary.");
        var delivery = CustomerEmailDeliveryOptions.Read(configuration);
        var protector = new CustomerEmailCodeProtector(configuration["Monergy:CustomerEmailAccess:Secret"] ?? "");
        services.AddSingleton(delivery);
        services.AddSingleton(protector);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICustomerEmailAccessRepository, PostgresCustomerEmailAccess>();
        services.AddSingleton<ICustomerEmailSender, SmtpCustomerEmailSender>();
        services.AddSingleton<CustomerEmailAccess>();
        services.AddHostedService<CustomerEmailDelivery>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter("customer-email", limiter =>
            {
                limiter.PermitLimit = 120;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });
        });
        return true;
    }

    public static void MapCustomerEmailAccess(this WebApplication app, bool enabled)
    {
        app.MapGet("/local/v1/customer-email-access", () => Results.Ok(new { enabled }));
        if (!enabled) return;
        app.UseRateLimiter();
        var group = app.MapGroup("/local/v1/customer-email-access").RequireRateLimiting("customer-email");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.HasJsonContentType() || http.Request.ContentLength is > 2048)
                return Results.StatusCode(400);
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}")
                return Results.StatusCode(403);
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantAccessException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode); }
            catch (System.Data.Common.DbException) { return Results.Json(new { error = "EMAIL_ACCESS_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapPost("/request", (CustomerEmailRequest request, CustomerEmailAccess owner, HttpContext http) => owner.RequestAsync(request, http.RequestAborted));
        group.MapPost("/confirm", (CustomerEmailConfirmation request, CustomerEmailAccess owner, HttpContext http) =>
            owner.ConfirmAsync(request, TenantSessionEndpoints.Client(http), http.RequestAborted));
    }
}
