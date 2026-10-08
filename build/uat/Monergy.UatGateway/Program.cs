using System.Net.Http.Headers;
using Monergy.Platform;
using Monergy.UatGateway;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment() || builder.Configuration["Monergy:ExecutionZone"] is not ("LOCAL" or "CI_EPHEMERAL"))
    throw new InvalidOperationException("The Docker UAT workspace requires Development LOCAL/CI.");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 262_144);
builder.Services.AddSingleton(provider => new TenantAccessClient(provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
builder.Services.AddSingleton(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
{ Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1_048_576 });
var app = builder.Build();
app.Use(async (http, next) =>
{
    http.Response.Headers.CacheControl = "no-store";
    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    http.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}")
    { http.Response.StatusCode = 403; return; }
    try { await next(http).ConfigureAwait(false); }
    catch (TenantBoundaryException failure)
    { http.Response.StatusCode = failure.Status; await http.Response.WriteAsJsonAsync(new { error = failure.Code }, http.RequestAborted).ConfigureAwait(false); }
    catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException && !http.RequestAborted.IsCancellationRequested)
    { http.Response.StatusCode = 503; await http.Response.WriteAsJsonAsync(new { error = "OWNER_UNAVAILABLE" }, http.RequestAborted).ConfigureAwait(false); }
});
app.UseStaticFiles();
app.UseRouting();
app.MapGet("/health/live", () => Results.Ok(new { profile = "local-uat", productionAccepted = false }));
app.MapGet("/health/ready", async (HttpClient client, CancellationToken ct) =>
{
    var states = await Topology.ReadAsync(client, ct).ConfigureAwait(false);
    return states.All(state => state.Available) ? Results.Ok(new { state = "local-services-running", serviceCount = states.Length }) : Results.StatusCode(503);
});
app.MapGet("/uat-api/v1/context", async (HttpContext http, TenantAccessClient access) =>
{
    var administrator = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
    return Results.Ok(new { administrator, environment = "Local UAT", authentication = "Local reference keys", productionAccepted = false });
});
app.MapGet("/uat-api/v1/topology", async (HttpContext http, TenantAccessClient access, HttpClient client) =>
{
    var administrator = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
    return Results.Ok(new { administrator.TenantId, services = await Topology.ReadAsync(client, http.RequestAborted).ConfigureAwait(false) });
});
app.MapGet("/uat-api/v1/readiness", async (HttpContext http, TenantAccessClient access) =>
{
    var administrator = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
    return Results.Ok(new
    {
        administrator.TenantId,
        productionAccepted = false,
        gates = new[] {
        new { id = "identity", state = "Blocked", detail = "Production identity provider, MFA and invitation delivery are not configured." },
        new { id = "transport", state = "Blocked", detail = "Workload identity, TLS and the production event transport require integration." },
        new { id = "operations", state = "Blocked", detail = "Managed secrets, backups, restore drills, monitoring, HA/DR and capacity acceptance remain required." },
        new { id = "business", state = "Blocked", detail = "Consent/AI are scaffolds. Document Intelligence and Job Management lack live business consumers. Service health and permission checks do not prove those journeys." },
        new { id = "governance", state = "Blocked", detail = "Historical D01–D16 and SG release acceptance remain unchanged. Local UAT does not confer Production approval." },
    }
    });
});
app.MapPost("/uat-api/v1/services/{service}/access-check", async (string service, HttpContext http, HttpClient client) =>
{
    _ = TenantAccessClient.Headers(http);
    var target = Topology.Services.SingleOrDefault(item => item.Id == service);
    if (target is null || service == "access-management") return Results.NotFound();
    await ProxyAsync(http, client, new Uri(target.Url + "/local/v1/tenant-access/check")).ConfigureAwait(false);
    return Results.Empty;
});
app.Map("/{**path}", async (HttpContext http, HttpClient client, IWebHostEnvironment environment) =>
{
    var path = http.Request.Path.Value ?? "/";
    var target = Route(path, http.Request.Method);
    if (target is not null)
    {
        await ProxyAsync(http, client, new Uri(target + http.Request.QueryString)).ConfigureAwait(false);
        return;
    }
    if (!HttpMethods.IsGet(http.Request.Method) || path.StartsWith("/uat-api/", StringComparison.Ordinal) || path.Contains("api/", StringComparison.Ordinal) || path.StartsWith("/contracts/", StringComparison.Ordinal))
    { http.Response.StatusCode = 404; return; }
    // Static files are handled earlier; only known SPA destinations get the shell.
    if (path is not ("/" or "/access" or "/search" or "/reports" or "/vs02" or "/foundation"))
    { http.Response.StatusCode = 404; return; }
    http.Response.ContentType = "text/html; charset=utf-8";
    await http.Response.SendFileAsync(Path.Combine(environment.WebRootPath, "index.html"), http.RequestAborted).ConfigureAwait(false);
});
await app.RunAsync();

static string? Route(string path, string method)
{
    if (path.StartsWith("/access-api/v1/", StringComparison.Ordinal) && method is "GET" or "POST" or "PUT" or "DELETE")
        return "http://127.0.0.1:5088" + path[11..];
    if ((path is "/identity-api/local/v1/tenant-sessions" or "/identity-api/local/v1/tenant-sessions/revoke") && method == "POST" ||
        path == "/identity-api/local/v1/administration/sessions" && method == "GET")
        return "http://127.0.0.1:5101" + path[13..];
    if (path == "/audit-api/local/v1/administration/access-events" && method == "GET")
        return "http://127.0.0.1:5102" + path[10..];
    if (method != "POST") return null;
    var id = path.Split('/');
    if (id.Length != 4 || id[1] != "contracts" || id[3] != "v1") return null;
    var port = id[2] switch
    {
        "cid-020" or "cid-021" or "cid-022" => 5111,
        "cid-030" or "cid-031" or "cid-032" or "cid-033" => 5113,
        "cid-037" or "cid-038" or "cid-039" => 5114,
        "cid-042" or "cid-043" or "cid-044" => 5115,
        "cid-051" or "cid-052" => 5116,
        "cid-015" or "cid-016" => 5117,
        _ => 0,
    };
    return port == 0 ? null : $"http://127.0.0.1:{port}" + path;
}

static async Task ProxyAsync(HttpContext http, HttpClient client, Uri target)
{
    using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), target);
    foreach (var name in new[] { "X-Monergy-Tenant", "X-Monergy-Session", "X-Monergy-Reference-Authentication" })
    {
        var value = http.Request.Headers[name];
        if (value.Count > 1 || value.ToString().Length > 256) throw new TenantBoundaryException("INVALID_HEADER", 400);
        if (value.Count == 1) request.Headers.Add(name, value.ToString());
    }
    if (http.Request.Method is "POST" or "PUT" or "DELETE")
    {
        if (http.Request.ContentType?.Split(';')[0] != "application/json") throw new TenantBoundaryException("JSON_REQUIRED", 415);
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, http.RequestAborted).ConfigureAwait(false);
        request.Content = new ByteArrayContent(buffer.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }
    using var response = await client.SendAsync(request, http.RequestAborted).ConfigureAwait(false);
    if ((int)response.StatusCode is >= 300 and < 400) throw new TenantBoundaryException("OWNER_REDIRECT_REJECTED", 503);
    http.Response.StatusCode = (int)response.StatusCode;
    http.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
    await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted).ConfigureAwait(false);
}
