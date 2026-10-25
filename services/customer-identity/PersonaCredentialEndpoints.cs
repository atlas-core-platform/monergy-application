using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.RateLimiting;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;

namespace Monergy.Services.CustomerIdentity;

public static class PersonaCredentialEndpoints
{
    public static bool AddPersonaCredentials(this IServiceCollection services, IConfiguration config,
        bool emailEnabled, bool isDevelopment)
    {
        if (config["Monergy:PersonaCredentials:Enabled"] != "true") return false;
        var testerEnabled = config["Monergy:PersonaCredentials:TesterActivation:Enabled"] == "true";
        if (config["Monergy:AccessIntegration:CustomerRelationships"] != "true")
            throw new InvalidOperationException("Persona login requires owner-backed tenant identity.");
        if (!emailEnabled && !testerEnabled)
            throw new InvalidOperationException("Persona login requires email activation or explicit Development tester activation.");
        TenantAccessIntegration.EnsureAllowed(config);
        if (testerEnabled)
        {
            var options = PersonaTesterActivationOptions.Read(config, isDevelopment);
            services.AddSingleton(options);
            services.AddSingleton<PersonaTesterActivation>();
            if (!emailEnabled)
            {
                services.TryAddSingleton(new CustomerEmailCodeProtector(
                    config["Monergy:PersonaCredentials:TesterActivation:ChallengeSecret"] ?? ""));
                services.TryAddSingleton(TimeProvider.System);
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
            }
        }
        services.TryAddSingleton<CustomerPasswordPolicy>();
        services.AddSingleton<IPersonaCredentialRepository, PostgresPersonaCredentials>();
        services.AddSingleton<PersonaCredentialAccess>();
        return true;
    }

    public static void MapPersonaCredentials(this WebApplication app, bool enabled, bool emailEnabled)
    {
        var testerEnabled = enabled &&
            app.Configuration["Monergy:PersonaCredentials:TesterActivation:Enabled"] == "true";
        app.MapGet("/local/v1/persona-credentials", () => testerEnabled
            ? Results.Ok(new { enabled, activation = "tester-link" })
            : Results.Ok(new { enabled }));
        if (!enabled) return;
        if (!emailEnabled) app.UseRateLimiter();
        var routes = app.MapGroup("/local/v1/persona-credentials").RequireRateLimiting("customer-email");
        routes.AddEndpointFilter(async (context, next) =>
        {
            var h = context.HttpContext;
            h.Response.Headers.CacheControl = "no-store";
            if (!h.Request.HasJsonContentType() || h.Request.ContentLength is > 4096)
                return Results.StatusCode(400);
            if (h.Request.Headers.Origin.Count > 0 &&
                h.Request.Headers.Origin.ToString() != $"{h.Request.Scheme}://{h.Request.Host}")
                return Results.StatusCode(403);
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantAccessException ex) { return Results.Json(new { error = ex.Code }, statusCode: ex.StatusCode); }
            catch (Exception ex) when (ex is System.Data.Common.DbException or HttpRequestException)
            { return Results.Json(new { error = "PERSONA_IDENTITY_UNAVAILABLE" }, statusCode: 503); }
        });
        routes.MapPost("/activation/request", (PersonaActivationRequest r, PersonaCredentialAccess owner, HttpContext h) =>
            owner.RequestAsync(r, false, h.RequestAborted));
        routes.MapPost("/activation/confirm", (PersonaActivationConfirm r, PersonaCredentialAccess owner, HttpContext h) =>
            owner.ActivateAsync(r, h.RequestAborted));
        routes.MapPost("/sign-in", (PasswordSignIn r, PersonaCredentialAccess owner, HttpContext h) =>
            owner.SignInAsync(r, h.RequestAborted));
        routes.MapPost("/recovery/request", (PersonaActivationRequest r, PersonaCredentialAccess owner, HttpContext h) =>
            owner.RequestAsync(r, true, h.RequestAborted));
        routes.MapPost("/recovery/confirm", (PersonaRecoveryConfirm r, PersonaCredentialAccess owner, HttpContext h) =>
            owner.RecoverAsync(r, h.RequestAborted));
        if (testerEnabled)
        {
            routes.MapPost("/tester/welcome-link", (PersonaTesterWelcomeRequest r,
                PersonaTesterActivation owner, PersonaTesterActivationOptions options, HttpContext h) =>
            {
                if (!h.Request.Headers.TryGetValue("X-Monergy-Tester-Activation", out var supplied) ||
                    supplied.Count != 1 || !options.Authorize(supplied.ToString()))
                    return Task.FromResult<object>(Results.StatusCode(403));
                return owner.CreateWelcomeLinkAsync(r, h.RequestAborted);
            });
            routes.MapPost("/tester/activation/confirm", (PersonaTesterActivationConfirm r,
                PersonaTesterActivation owner, HttpContext h) => owner.ActivateAsync(r, h.RequestAborted));
        }
    }
}
