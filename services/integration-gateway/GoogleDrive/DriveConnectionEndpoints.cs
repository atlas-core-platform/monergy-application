using System.Text.Json;
using Monergy.Platform;
using Monergy.Services.IntegrationGateway;

namespace Monergy.Services.IntegrationGateway.GoogleDrive;

public static class DriveConnectionEndpoints
{
    public static void AddGoogleDrive(this WebApplicationBuilder builder)
    {
        if (builder.Configuration["Monergy:GoogleDrive:Enabled"] != "true") return;
        if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Google Drive adapter requires LOCAL/CI Development.");
        TenantAccessIntegration.EnsureAllowed(builder.Configuration);
        // OAuth codes arrive in a callback query. Exclude that route from traces
        // and request-start diagnostics rather than depending on redaction defaults.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Services.Configure<OpenTelemetry.Instrumentation.AspNetCore.AspNetCoreTraceInstrumentationOptions>(options =>
            options.Filter = http => http.Request.Path != "/local/v1/google-drive/callback" && http.Request.Path != "/local/v1/onedrive/callback");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16_384);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.RespectRequiredConstructorParameters = true;
            options.SerializerOptions.RespectNullableAnnotations = true;
            options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
        });
        builder.Services.AddSingleton<CustomerOwnerClient>();
        builder.Services.AddSingleton(provider => new TenantAccessClient(provider.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<DriveConnectionStore>();
        builder.Services.AddSingleton<IGoogleDriveProvider>(_ => new GoogleDriveProvider(Client()));
        builder.Services.AddSingleton<IDriveOwners>(provider => new DriveOwners(provider.GetRequiredService<CustomerOwnerClient>(),
            provider.GetRequiredService<TenantAccessClient>(), provider.GetRequiredService<IConfiguration>(), Client()));
        builder.Services.AddSingleton<DriveConnectionApplication>();
    }
    private static HttpClient Client() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 262144 };
    public static void MapGoogleDrive(this WebApplication app)
    {
        if (app.Configuration["Monergy:GoogleDrive:Enabled"] != "true") return;
        const string root = "/local/v1/google-drive";
        app.MapGet(root, (HttpContext http, DriveConnectionApplication service) => Invoke(http, () => service.StatusAsync(http)));
        app.MapPost(root + "/authorize", (DriveBegin input, HttpContext http, DriveConnectionApplication service) => Invoke(http, () => service.BeginAsync(http, input)));
        app.MapGet(root + "/folders", (string? page, HttpContext http, DriveConnectionApplication service) => Invoke(http, () => service.ListAsync(http, true, page)));
        app.MapGet(root + "/files", (string? page, HttpContext http, DriveConnectionApplication service) => Invoke(http, () => service.ListAsync(http, false, page)));
        app.MapPost(root + "/folder", (DriveSelection input, HttpContext http, DriveConnectionApplication service) => Invoke(http, () => service.SelectFolderAsync(http, input)));
        app.MapPost(root + "/import", (DriveSelection input, HttpContext http, DriveConnectionApplication service) => Invoke(http, () => service.ImportAsync(http, input)));
        app.MapPost(root + "/disconnect", (DriveRevision input, HttpContext http, DriveConnectionApplication service) => Invoke(http, () => service.DisconnectAsync(http, input)));
        app.MapGet(root + "/callback", async (HttpContext http, DriveConnectionApplication service) =>
        {
            Secure(http);
            var state = http.Request.Query["state"]; var code = http.Request.Query["code"]; var error = http.Request.Query["error"];
            try
            {
                if (state.Count != 1 || code.Count > 1 || error.Count > 1) throw new TenantBoundaryException("DRIVE_CALLBACK_INVALID", 400);
                var connected = await service.CallbackAsync(state.ToString(), code.Count == 1 ? code.ToString() : null, error.Count != 0, http.RequestAborted);
                return Results.Text(connected ? "Google permission received. Close this window and select Refresh connection in Monergy to choose your folder."
                    : "Google Drive was not connected. Close this window and return to Monergy.", "text/plain");
            }
            catch (Exception failure) when (failure is TenantBoundaryException or HttpRequestException or JsonException or TaskCanceledException)
            { return Results.Text("Google Drive could not be connected. Close this window and return to Monergy to review the connection.", "text/plain", statusCode: 400); }
        });
    }
    private static void Secure(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store"; http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    }
    private static async Task<IResult> Invoke<T>(HttpContext http, Func<Task<T>> action)
    {
        Secure(http);
        try { return Results.Ok(await action()); }
        catch (TenantBoundaryException error) { return DriveFailurePolicy.Result(http, error, "Google Drive"); }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException or FormatException or KeyNotFoundException)
        { return DriveFailurePolicy.Unavailable(http, "Google Drive"); }
    }
}
