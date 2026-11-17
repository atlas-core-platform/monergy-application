using System.Security.Cryptography;
using System.Text;
using Monergy.Services.CustomerIdentity.Infrastructure;

namespace Monergy.Services.CustomerIdentity;

public static class GovernedSmtpReadinessEndpoints
{
    public static void MapGovernedSmtpReadiness(this WebApplication app)
    {
        app.MapGet("/internal/local/v1/governed-providers/email/readiness", async (
            string tenant, HttpContext http, GovernedSmtpConfigurationOwner owner) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Authorized(http, app.Configuration["Monergy:GovernedProviders:Token"] ?? ""))
                return Results.StatusCode(403);
            try
            {
                var value = await owner.ResolveAsync(tenant, http.RequestAborted).ConfigureAwait(false);
                return Results.Ok(new { owner = "Customer & Identity", provider = "email", tenant,
                    host = value.Host, port = value.Port, sender = value.From, state = "Ready" });
            }
            catch (Exception failure) when (failure is InvalidOperationException or HttpRequestException)
            { return Results.Json(new { error = "GOVERNED_PROVIDER_NOT_READY" }, statusCode: 503); }
        });
    }

    private static bool Authorized(HttpContext http, string expected)
    {
        var supplied = http.Request.Headers["X-Monergy-Provider-Configuration"].ToString();
        return expected.Length >= 32 && supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    }
}
