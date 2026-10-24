using Microsoft.Extensions.DependencyInjection.Extensions;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;

namespace Monergy.Services.CustomerIdentity;

public static class PersonaCredentialEndpoints
{
    public static bool AddPersonaCredentials(this IServiceCollection services, IConfiguration config, bool emailEnabled)
    {
        if (config["Monergy:PersonaCredentials:Enabled"] != "true") return false;
        if (!emailEnabled || config["Monergy:AccessIntegration:CustomerRelationships"] != "true")
            throw new InvalidOperationException("Persona login requires owner-backed tenant identity and authenticated SMTP.");
        TenantAccessIntegration.EnsureAllowed(config);
        services.TryAddSingleton<CustomerPasswordPolicy>();
        services.AddSingleton<IPersonaCredentialRepository, PostgresPersonaCredentials>();
        services.AddSingleton<PersonaCredentialAccess>();
        return true;
    }

    public static void MapPersonaCredentials(this WebApplication app, bool enabled)
    {
        app.MapGet("/local/v1/persona-credentials", () => Results.Ok(new { enabled }));
        if (!enabled) return;
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
    }
}
