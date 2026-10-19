using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.UatGateway;


public static class ProductAssetSnapshotEndpoints
{
    public static void AddProductAssetSnapshot(this WebApplicationBuilder builder)
    {
        if (builder.Configuration.GetValue<bool>("Monergy:Product:Enabled")) builder.Services.AddSingleton<ProductAssetSnapshotClient>();
    }
    public static void MapProductAssetSnapshot(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;
        var group = app.MapGroup("/product-api/v1/asset-records");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext; http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}") return Results.StatusCode(403);
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantBoundaryException error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
            catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
            { return Results.Json(new { error = "ASSET_SNAPSHOT_UNAVAILABLE" }, statusCode: 503); }
            catch (IOException) { return Results.Json(new { error = "ASSET_SNAPSHOT_TOO_LARGE" },statusCode:413); }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            { return Results.Json(new { error = "ASSET_SNAPSHOT_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapGet("/snapshot",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductAssetSnapshotClient client)=>
        {
            var(session,customer)=await ResolveAsync(http,owners,access,false).ConfigureAwait(false);
            var(date,currency)=SnapshotSelection(http.Request);
            var value=await client.ReadAsync(session,customer.CustomerId,date,currency,http.RequestAborted).ConfigureAwait(false);
            if(value.AsOfDate!=date || value.Currency!=currency ||
               !Valid(value.Investments?.Items,value.Investments?.HasMore, x=>ProductInvestmentRecordEndpoints.Bound(x,customer.CustomerId) && x.Currency==currency && x.Investment.ValueAsOfDate==date,x=>x.InvestmentRecordId) ||
               !Valid(value.Properties?.Items,value.Properties?.HasMore, x=>ProductPropertyRecordEndpoints.Bound(x,customer.CustomerId) && x.Currency==currency && x.Property.ValueAsOfDate==date,x=>x.PropertyRecordId) ||
               !Valid(value.Vehicles?.Items,value.Vehicles?.HasMore, x=>ProductVehicleRecordEndpoints.Bound(x,customer.CustomerId) && x.Currency==currency && x.Vehicle.ValueAsOfDate==date,x=>x.VehicleRecordId))
                throw new TenantBoundaryException("ASSET_SNAPSHOT_RESPONSE_INVALID",503);
            await ConfirmAsync(http,owners,access,session,customer,false).ConfigureAwait(false);
            return value;
        });
    }
    private static bool Valid<T>(IReadOnlyList<T>? items,bool? hasMore,Func<T,bool> bound,Func<T,string> id) =>
        items is not null && hasMore is not null && items.Count<=200 && (!hasMore.Value || items.Count==200) &&
        items.All(bound) && items.Select(id).Distinct(StringComparer.Ordinal).Count()==items.Count;
    private static (DateOnly Date,string Currency) SnapshotSelection(HttpRequest request)
    {
        if (request.Query.Count!=2 || request.Query.Keys.Any(key=>key is not ("asOfDate" or "currency")) ||
            request.Query["asOfDate"].Count!=1 || request.Query["currency"].Count!=1 ||
            !DateOnly.TryParseExact(request.Query["asOfDate"].ToString(),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var date) ||
            date.Year<1900 || date>DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime.AddHours(14)))
            throw new TenantBoundaryException("ASSET_SNAPSHOT_INVALID",400);
        var currency=request.Query["currency"].ToString();
        if(currency.Length!=3 || !currency.All(char.IsAsciiLetterUpper)) throw new TenantBoundaryException("ASSET_SNAPSHOT_INVALID",400);
        return(date,currency);
    }
    private static async Task<(CustomerOwnerSession Session, CustomerOwnerWorkspace Customer)> ResolveAsync(
        HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, bool write)
    {
        var session = await owners.SessionAsync(http).ConfigureAwait(false);
        var customer = await owners.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
        var read = await access.EvaluateCustomerAsync(http, "financial-profile.profile.read", customer.CustomerId).ConfigureAwait(false);
        var evidence = await access.EvaluateCustomerAsync(http, "evidence.document.read", customer.CustomerId).ConfigureAwait(false);
        if (read.Outcome != "Allow" || read.AccessBasis != "Self" || read.ActorId != session.ActorId ||
            read.SubjectVersion != session.SubjectVersion || evidence.Outcome != "Allow" || evidence.AccessBasis != "Self" || evidence.ActorId != read.ActorId ||
            evidence.SubjectVersion != read.SubjectVersion || evidence.CustomerVersion != read.CustomerVersion)
            throw new TenantBoundaryException("ACCESS_DENIED", 403);
        if (write)
        {
            var update = await access.EvaluateCustomerAsync(http, "financial-profile.profile.update", customer.CustomerId).ConfigureAwait(false);
            if (update.Outcome != "Allow" || update.AccessBasis != "Self" || update.ActorId != session.ActorId ||
                update.SubjectVersion != read.SubjectVersion || update.CustomerVersion != read.CustomerVersion)
                throw new TenantBoundaryException("ACCESS_DENIED", 403);
        }
        return (session, customer);
    }
    private static async Task ConfirmAsync(HttpContext http, CustomerOwnerClient owners, TenantAccessClient access,
        CustomerOwnerSession original, CustomerOwnerWorkspace originalCustomer, bool write)
    {
        var (current, customer) = await ResolveAsync(http, owners, access, write).ConfigureAwait(false);
        if (current.TenantId != original.TenantId || current.ActorId != original.ActorId ||
            current.SubjectVersion != original.SubjectVersion || customer.CustomerId != originalCustomer.CustomerId || customer.Version != originalCustomer.Version)
            throw new TenantBoundaryException("ASSET_SNAPSHOT_CONTEXT_CHANGED", 409);
    }
}

public sealed class ProductAssetSnapshotClient(IConfiguration config,HttpClient? injected=null)
{
    private readonly HttpClient client=injected ?? new HttpClient {Timeout=TimeSpan.FromSeconds(15)};
    private static readonly JsonSerializerOptions Json=new(ContractJson.Options) {RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true};
    public async Task<AssetRecordSnapshot> ReadAsync(CustomerOwnerSession session,string customer,DateOnly date,string currency,CancellationToken ct)
    {
        var root=config["Monergy:Product:FinancialProfileUrl"] ?? throw new TenantBoundaryException("ASSET_SNAPSHOT_UNAVAILABLE",503);
        using var request=new HttpRequestMessage(HttpMethod.Get,root.TrimEnd('/')+"/local/v1/customers/"+Uri.EscapeDataString(customer)+"/asset-records/snapshot?asOfDate="+date.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture)+"&currency="+Uri.EscapeDataString(currency));
        request.Headers.Add("X-Monergy-Tenant",session.TenantId);request.Headers.Add("X-Monergy-Session",session.AuthenticationContextId);
        using var response=await client.SendAsync(request,ct).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode) {var status=(int)response.StatusCode;throw new TenantBoundaryException("ASSET_SNAPSHOT_UNAVAILABLE",status is 400 or 401 or 403 ? status:503);}
        return await response.Content.ReadFromJsonAsync<AssetRecordSnapshot>(Json,ct).ConfigureAwait(false) ?? throw new TenantBoundaryException("ASSET_SNAPSHOT_RESPONSE_INVALID",503);
    }
}
