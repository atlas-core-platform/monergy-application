using System.Net.Http.Headers;
using Monergy.Platform;
using Monergy.UatGateway;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment() || builder.Configuration["Monergy:ExecutionZone"] is not ("LOCAL" or "CI_EPHEMERAL"))
    throw new InvalidOperationException("The Docker UAT workspace requires Development LOCAL/CI.");
// OAuth callback queries must not enter request-start diagnostics.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_500_000);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
    options.SerializerOptions.RespectNullableAnnotations = true;
    options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
});
builder.AddTenantSetup();
builder.AddProductWorkspace();
builder.AddProductFinancialProfile();
builder.AddProductNetWorth(); builder.AddProductMonthlyCashflow();
builder.AddProductLoanRecords();
builder.AddProductInvestmentRecords();
builder.AddProductPropertyRecords();
builder.AddProductVehicleRecords();
builder.AddProductInsuranceRecords();
builder.AddProductTaxRecords();
builder.AddProductFinancialGoals();
builder.AddProductGoalContributions();
builder.AddProductAssetSnapshot();
builder.AddProductIncomeRecords();
builder.AddProductExpenseRecords();
builder.AddProductLiabilityRecords();
builder.AddProductReporting();
builder.AddProductSearch();
builder.Services.AddSingleton<GoogleDriveProxyClient>();
builder.Services.AddSingleton(provider => new TenantAccessClient(provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
builder.Services.AddSingleton(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
{ Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1_048_576 });
if(builder.Configuration.GetValue<bool>("Monergy:ProductAudit:Enabled"))builder.Services.AddSingleton<IProductAuditWriter,ProductAuditWriter>();
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
app.UseProductAudit();
app.UseStaticFiles();
app.UseRouting();
app.MapTenantSetup();
app.MapProductWorkspace();
app.MapProductDocumentVault();
app.MapProductFinancialProfile();
app.MapProductNetWorth(); app.MapProductMonthlyCashflow();
app.MapProductLoanRecords();
app.MapProductInvestmentRecords();
app.MapProductPropertyRecords();
app.MapProductVehicleRecords();
app.MapProductInsuranceRecords();
app.MapProductTaxRecords();
app.MapProductFinancialGoals();
app.MapProductGoalContributions();
app.MapProductAssetSnapshot();
app.MapProductIncomeRecords();
app.MapProductExpenseRecords();
app.MapProductLiabilityRecords();
app.MapProductReporting();
app.MapProductSearch();
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
    return Results.Ok(new
    {
        administrator.TenantId,
        services = await Topology.ReadAsync(client, http.RequestAborted,
        app.Configuration.GetValue<bool>("Monergy:Onboarding:Enabled")).ConfigureAwait(false)
    });
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
        new { id = "business", state = "Blocked", detail = "The LOCAL structured-text processor is connected through Evidence, Job Management and Document Intelligence. Production OCR/AI providers, durable business persistence and financial-profile adoption remain unselected; this journey does not constitute Production acceptance." },
        new { id = "governance", state = "Blocked", detail = "Historical D01–D16 and SG release acceptance remain unchanged. Local UAT does not confer Production approval." },
    }
    });
});
app.MapGet("/uat-api/v1/resource-directory/{resourceType}", async (
    string resourceType, HttpContext http, TenantAccessClient access, HttpClient client) =>
{
    _ = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
    if (resourceType != "customer")
        return Results.Json(new { error = "RESOURCE_DIRECTORY_UNAVAILABLE" }, statusCode: 422);
    await ProxyAsync(http, client, new Uri("http://127.0.0.1:5101/local/v1/administration/resource-directory/customers")).ConfigureAwait(false);
    return Results.Empty;
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
        if (path.StartsWith("/contracts/", StringComparison.Ordinal) && app.Configuration.GetValue<bool>("Monergy:Product:Enabled"))
            await CustomerSetupAdmission.RequireAsync(http, http.RequestServices.GetRequiredService<CustomerOwnerClient>()).ConfigureAwait(false);
        await ProxyAsync(http, (path.StartsWith("/connections-api/", StringComparison.Ordinal) || path.StartsWith("/product-api/v1/financial-qa/", StringComparison.Ordinal))
            ? http.RequestServices.GetRequiredService<GoogleDriveProxyClient>().Client : client, new Uri(target + http.Request.QueryString)).ConfigureAwait(false);
        return;
    }
    if (!HttpMethods.IsGet(http.Request.Method) || path.StartsWith("/uat-api/", StringComparison.Ordinal) || path.StartsWith("/product-api/", StringComparison.Ordinal) || path.Contains("api/", StringComparison.Ordinal) || path.StartsWith("/contracts/", StringComparison.Ordinal))
    { http.Response.StatusCode = 404; return; }
    // Static files are handled earlier; only known SPA destinations get the shell.
    if (!SpaRouteAllowlist.Contains(path))
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
    const string documents="/evidence-api/local/v1/administration/documents";
    if(method=="GET"&&(path==documents||path.StartsWith(documents+"/",StringComparison.Ordinal)&&TenantAccessClient.Identifier(path[(documents.Length+1)..],160)))return "http://127.0.0.1:5111"+path[13..];
    if(method=="GET"&&path=="/classification-api/local/v1/administration/document-classifications")return "http://127.0.0.1:5112"+path[19..];
    const string classificationReviews="/classification-api/local/v1/administration/classification-reviews";
    const string ocr="/ocr-api/local/v1/customers/";
    if(method=="GET"&&path.StartsWith(ocr,StringComparison.Ordinal))
    {
        var ids=path[ocr.Length..].Split('/');
        if(ids.Length==4&&TenantAccessClient.Identifier(ids[0],128)&&ids[1]=="documents"&&TenantAccessClient.Identifier(ids[2],160)&&ids[3]=="ocr")return "http://127.0.0.1:5112"+path[8..];
    }
    if(path==classificationReviews&&method=="GET")return "http://127.0.0.1:5112"+path[19..];
    if(path.StartsWith(classificationReviews+"/",StringComparison.Ordinal)&&method is "GET" or "POST")
    {
        var ids=path[(classificationReviews.Length+1)..].Split('/');
        if(ids.Length==2&&TenantAccessClient.Identifier(ids[0],128)&&TenantAccessClient.Identifier(ids[1],160))return "http://127.0.0.1:5112"+path[19..];
    }
    const string processing="/jobs-api/local/v1/administration/processing";
    if(path==processing&&method=="GET")return "http://127.0.0.1:5118"+path[9..];
    if(path.StartsWith(processing+"/",StringComparison.Ordinal))
    {
        var parts=path[(processing.Length+1)..].Split('/');
        if(parts.Length>0&&TenantAccessClient.Identifier(parts[0],128)&&
          (method=="GET"&&(parts.Length==1||parts.Length==2&&parts[1] is "attempts" or "history"||parts.Length==3&&parts[1]=="changes"&&Guid.TryParseExact(parts[2],"N",out _))||
           method=="POST"&&parts.Length==2&&parts[1]=="changes"))return "http://127.0.0.1:5118"+path[9..];
    }
    const string customers="/identity-api/local/v1/administration/customers";
    if((path==customers&&method=="GET"||path==customers+"/labels"&&method=="POST"))return "http://127.0.0.1:5101"+path[13..];
    if(path.StartsWith(customers+"/",StringComparison.Ordinal))
    {
        var parts=path[(customers.Length+1)..].Split('/');
        if(parts.Length>0&&TenantAccessClient.Identifier(parts[0],128)&&
           (method=="GET"&&(parts.Length==1||parts.Length==2&&parts[1]=="history"||parts.Length==3&&parts[1]=="changes"&&Guid.TryParseExact(parts[2],"N",out _))||
            method=="POST"&&parts.Length==2&&parts[1]=="status"))return "http://127.0.0.1:5101"+path[13..];
    }
    if (method=="GET" && path is "/identity-api/local/v1/customer-registration" or "/identity-api/local/v1/customer-registration/agreements" ||
        method=="POST" && path is "/identity-api/local/v1/customer-registration/request" or "/identity-api/local/v1/customer-registration/confirm" or "/identity-api/local/v1/customer-registration/sign-in" or "/identity-api/local/v1/customer-registration/recovery/request" or "/identity-api/local/v1/customer-registration/recovery/confirm")
        return "http://127.0.0.1:5101" + path[13..];
    if (path == "/identity-api/local/v1/customer-email-access" && method == "GET" ||
        path is "/identity-api/local/v1/customer-email-access/request" or "/identity-api/local/v1/customer-email-access/confirm" && method == "POST")
        return "http://127.0.0.1:5101" + path[13..];
    if (path == "/identity-api/local/v1/me/workspace-setup" && method == "GET")
        return "http://127.0.0.1:5101" + path[13..];
    if (path == "/identity-api/local/v1/me/account-agreements" && method is "GET" or "POST")
        return "http://127.0.0.1:5101" + path[13..];
    if (path == "/identity-api/local/v1/me/pan" && method is "GET" or "PUT")
        return "http://127.0.0.1:5101" + path[13..];
    if (path.StartsWith("/identity-api/local/v1/basic-kyc/",StringComparison.Ordinal) && method is "GET" or "POST")
        return "http://127.0.0.1:5101" + path[13..];
    if (path == "/identity-api/local/v1/me/customer-profile" && method is "GET" or "PUT")
        return "http://127.0.0.1:5101" + path[13..];
    if (path == "/identity-api/local/v1/me/sessions" && method == "GET" ||
        path == "/identity-api/local/v1/me/sessions/revoke" && method == "POST")
        return "http://127.0.0.1:5101" + path[13..];
    if (method=="GET"&&(path=="/audit-api/local/v1/administration/audit-log"||path=="/audit-api/local/v1/administration/audit-log/export"||path.StartsWith("/audit-api/local/v1/administration/audit-log/",StringComparison.Ordinal)&&path["/audit-api/local/v1/administration/audit-log/".Length..].Length>0&&!path["/audit-api/local/v1/administration/audit-log/".Length..].Contains('/')))
        return "http://127.0.0.1:5102" + path[10..];
    if (path == "/audit-api/local/v1/administration/access-events" && method == "GET")
        return "http://127.0.0.1:5102" + path[10..];
    const string qa = "/product-api/v1/financial-qa/";
    if (method == "GET" && (path == qa + "configuration" || path == qa + "history") || method == "POST" && path == qa + "questions")
        return "http://127.0.0.1:5119/local/v1/financial-qa/" + path[qa.Length..];
    const string drive = "/connections-api/local/v1/google-drive";
    if (method == "GET" && (path == drive || path == drive + "/folders" || path == drive + "/files" || path == drive + "/callback") ||
        method == "POST" && (path == drive + "/authorize" || path == drive + "/folder" || path == drive + "/import" || path == drive + "/disconnect"))
        return "http://127.0.0.1:5117" + path[16..];
    const string oneDrive = "/connections-api/local/v1/onedrive";
    if (method == "GET" && (path == oneDrive || path == oneDrive + "/folders" || path == oneDrive + "/files" || path == oneDrive + "/callback") ||
        method == "POST" && (path == oneDrive + "/authorize" || path == oneDrive + "/folder" || path == oneDrive + "/import" || path == oneDrive + "/disconnect"))
        return "http://127.0.0.1:5117" + path[16..];
    if (path.StartsWith("/consent-api/local/v1/consents/customers/", StringComparison.Ordinal) && method is "GET" or "POST" or "DELETE")
        return "http://127.0.0.1:5110" + path[12..];
    if (method != "POST") return null;
    var id = path.Split('/');
    if (id.Length != 4 || id[1] != "contracts" || id[3] != "v1") return null;
    var port = id[2] switch
    {
        "cid-020" or "cid-021" or "cid-022" => 5111,
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
