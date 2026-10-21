using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
namespace Monergy.Platform;

// Shared ingress/current-authority mechanics; no financial data or arithmetic is owned here.
public static class CustomerReviewHttp
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    public static async Task<T> ReadAsync<T>(HttpContext http) where T:class
    {
        http.Request.EnableBuffering(32768,262144);
        try
        {
            using var json=await JsonDocument.ParseAsync(http.Request.Body,cancellationToken:http.RequestAborted).ConfigureAwait(false);
            Duplicates(json.RootElement);
            return json.RootElement.Deserialize<T>(Json)??throw new TenantBoundaryException("NET_WORTH_INVALID",400);
        }
        catch(JsonException){throw new TenantBoundaryException("NET_WORTH_INVALID",400);}
    }
    public static async Task<CustomerAccessDecision> AuthorizeAsync(HttpContext http,string customer,TenantAccessClient access,
        TenantBoundaryOptions? bindings=null,bool write=false,bool calculate=false,bool documents=false)
    {
        var (tenant,_)=TenantAccessClient.Headers(http);
        if(bindings is not null&&!bindings.AllowsCustomerLookup(customer,tenant))throw new TenantBoundaryException("CUSTOMER_TENANT_MISMATCH",403);
        var read=await access.EvaluateCustomerAsync(http,"financial-profile.profile.read",customer).ConfigureAwait(false);
        if(read.Outcome!="Allow"||read.AccessBasis!="Self")throw new TenantBoundaryException("ACCESS_DENIED",403);
        var capabilities=new List<string>();
        if(write)capabilities.Add("financial-profile.profile.update");
        if(calculate)capabilities.Add("financial-rules.calculation.execute");
        if(documents)capabilities.Add("evidence.document.read");
        foreach(var capability in capabilities)
        {
            var decision=await access.EvaluateCustomerAsync(http,capability,customer).ConfigureAwait(false);
            if(decision.Outcome!="Allow"||decision.AccessBasis!="Self"||decision.ActorId!=read.ActorId||decision.SubjectVersion!=read.SubjectVersion||decision.CustomerVersion!=read.CustomerVersion)
                throw new TenantBoundaryException("ACCESS_DENIED",403);
        }
        return read;
    }
    public static void Confirm(CustomerAccessDecision before,CustomerAccessDecision after)
    {if(before.ActorId!=after.ActorId||before.SubjectVersion!=after.SubjectVersion||before.CustomerVersion!=after.CustomerVersion)throw new TenantBoundaryException("NET_WORTH_CONTEXT_CHANGED",409);}
    public static string? Cursor(HttpRequest request)
    {
        if(request.Query.Count==0)return null;
        if(request.Query.Count!=1||request.Query["beforeId"].Count!=1||!TenantAccessClient.Identifier(request.Query["beforeId"].ToString(),80))throw new TenantBoundaryException("NET_WORTH_INVALID",400);
        return request.Query["beforeId"].ToString();
    }
    public static void NoQuery(HttpRequest request)
    {if(request.Query.Count>0)throw new TenantBoundaryException("NET_WORTH_INVALID",400);}
    public static async ValueTask<object?> Filter(EndpointFilterInvocationContext context,EndpointFilterDelegate next)
    {
        var http=context.HttpContext;http.Response.Headers.CacheControl="no-store";
        if(http.Request.Headers.Origin.Count>0&&http.Request.Headers.Origin.ToString()!=$"{http.Request.Scheme}://{http.Request.Host}")return Results.StatusCode(403);
        try{return await next(context).ConfigureAwait(false);}
        catch(TenantBoundaryException error){return Results.Json(new{error=error.Code},statusCode:error.Status);}
        catch(IOException){return Results.Json(new{error="NET_WORTH_TOO_LARGE"},statusCode:413);}
        catch(Exception error) when(error is HttpRequestException or JsonException or NotSupportedException or System.Data.Common.DbException)
        {return Results.Json(new{error="NET_WORTH_UNAVAILABLE"},statusCode:503);}
        catch(OperationCanceledException) when(!http.RequestAborted.IsCancellationRequested)
        {return Results.Json(new{error="NET_WORTH_UNAVAILABLE"},statusCode:503);}
    }
    private static void Duplicates(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var item in element.EnumerateObject()){if(!names.Add(item.Name))throw new JsonException();Duplicates(item.Value);}}
        else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())Duplicates(item);
    }
}
