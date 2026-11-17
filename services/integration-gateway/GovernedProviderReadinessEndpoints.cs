using System.Security.Cryptography;
using System.Text;
using Monergy.Services.IntegrationGateway.AiModel;

namespace Monergy.Services.IntegrationGateway;

public static class GovernedProviderReadinessEndpoints
{
    public static void MapGovernedProviderReadiness(this WebApplication app)
    {
        app.MapGet("/internal/local/v1/governed-providers/{code}/readiness", async (
            string code, string tenant, HttpContext http,
            GoogleDrive.DriveConnectionApplication google,
            OneDrive.DriveConnectionApplication oneDrive,
            QaModelAdapter ai) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Authorized(http, app.Configuration["Monergy:GovernedProviders:Token"] ?? ""))
                return Results.StatusCode(403);
            try
            {
                var revision = code switch
                {
                    "google-drive" => await google.ReadinessAsync(tenant, http.RequestAborted).ConfigureAwait(false),
                    "onedrive" => await oneDrive.ReadinessAsync(tenant, http.RequestAborted).ConfigureAwait(false),
                    "ai" => await AiRevision(ai, tenant, http.RequestAborted).ConfigureAwait(false),
                    _ => throw new KeyNotFoundException(),
                };
                return Results.Ok(new { owner = "Integration Gateway", provider = code, tenant, revision, state = "Ready" });
            }
            catch (Exception failure) when (failure is InvalidOperationException or HttpRequestException or KeyNotFoundException)
            { return Results.Json(new { error = "GOVERNED_PROVIDER_NOT_READY" }, statusCode: 503); }
        });
    }

    private static async Task<object> AiRevision(QaModelAdapter ai, string tenant, CancellationToken ct)
    {
        var value = await ai.ConfigurationAsync(tenant, ct).ConfigureAwait(false);
        if (!value.Available) throw new InvalidOperationException("GOVERNED_PROVIDER_NOT_READY");
        return value.Revision;
    }

    private static bool Authorized(HttpContext http, string expected)
    {
        var supplied = http.Request.Headers["X-Monergy-Provider-Configuration"].ToString();
        return expected.Length >= 32 && supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    }
}
