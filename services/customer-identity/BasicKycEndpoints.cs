using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Monergy.Platform;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;

namespace Monergy.Services.CustomerIdentity;

public sealed record KycDocument(string TenantId,string CustomerId,string DocumentVersionId,string FileName,string Sha256,string ContentType,string ContentBase64);
public sealed class KycEvidenceClient : IDisposable
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    public KycEvidenceClient(IConfiguration config) : this(config,new HttpClient(new SocketsHttpHandler {AllowAutoRedirect=false,UseProxy=false})) { }
    public KycEvidenceClient(IConfiguration config,HttpClient client)
    {
        this.client=client;client.Timeout=TimeSpan.FromSeconds(15);client.MaxResponseContentBufferSize=1_500_000;
        var value=config["Monergy:BasicKyc:EvidenceBaseUrl"]??"http://127.0.0.1:5111/";
        if(!Uri.TryCreate(value,UriKind.Absolute,out endpoint!) || endpoint.Scheme!="http" || !endpoint.IsLoopback || endpoint.Query.Length!=0 || endpoint.UserInfo.Length!=0 || endpoint.AbsolutePath!="/") throw new InvalidOperationException("KYC Evidence requires the configured local owner endpoint.");
    }
    public async Task<KycDocument> ReadAsync(TenantSessionContext session,string customer,string version,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,new Uri(endpoint,"local/v1/customers/"+Uri.EscapeDataString(customer)+"/kyc-evidence/"+Uri.EscapeDataString(version)));
        request.Headers.Add("X-Monergy-Tenant",session.TenantId);request.Headers.Add("X-Monergy-Session",session.AuthenticationContextId);
        using var response=await client.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode)throw new TenantAccessException("KYC_EVIDENCE_ACCESS_UNAVAILABLE",(int)response.StatusCode is 403 or 404 or 422?(int)response.StatusCode:503);
        var doc=await response.Content.ReadFromJsonAsync<KycDocument>(cancellationToken:ct)??throw new TenantAccessException("KYC_EVIDENCE_INVALID",503);
        if(doc.TenantId!=session.TenantId||doc.CustomerId!=customer||doc.DocumentVersionId!=version||!BasicKyc.Text(doc.FileName,255)||doc.Sha256.Length!=64||doc.ContentBase64.Length>1_398_104||doc.ContentType is not("application/pdf" or "image/png" or "image/jpeg"))throw new TenantAccessException("KYC_EVIDENCE_INVALID",503);
        var bytes=Convert.FromBase64String(doc.ContentBase64);
        if(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))!=doc.Sha256)throw new TenantAccessException("KYC_EVIDENCE_INVALID",503);
        return doc;
    }
    public void Dispose()=>client.Dispose();
}
public static class BasicKycEndpoints
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=false,RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    public static void AddBasicKyc(this IServiceCollection services)
    {services.AddSingleton<BasicKycPolicy>();services.AddSingleton<IBasicKycRepository,PostgresBasicKyc>();services.AddSingleton<KycEvidenceClient>();}
    public static void MapBasicKyc(this WebApplication app)
    {
        var group=app.MapGroup("/local/v1/basic-kyc");group.AddEndpointFilter(Filter);
        group.MapGet("/reviewer-context",async(HttpContext h,TenantSessionAuthority sessions,BasicKycPolicy policies)=>{
            NoQuery(h);var session=await sessions.ValidateAsync(Lookup(h),h.RequestAborted);Reviewer(policies,session);await Confirm(h,sessions,session);
            return new {tenantId=session.TenantId,actorId=session.ActorId,authority="KycReviewer"};
        });
        group.MapGet("/me",async(HttpContext h,TenantSessionAuthority sessions,SelfCustomerProfileApplication profiles,IBasicKycRepository repo,BasicKycPolicy policies)=>{
            NoQuery(h);var lookup=Lookup(h);var session=await sessions.ValidateAsync(lookup,h.RequestAborted);var profile=await profiles.ReadAsync(lookup,h.RequestAborted);
            var current=await repo.ReadAsync(session.TenantId,profile.CustomerId,h.RequestAborted);Bound(current,session.TenantId,profile.CustomerId);
            await Confirm(h,sessions,session);await profiles.ReadAsync(lookup,h.RequestAborted);
            return new {tenantId=session.TenantId,customerId=profile.CustomerId,policy=policies.Read(session.TenantId),current};
        });
        group.MapGet("/me/history",async(HttpContext h,TenantSessionAuthority sessions,SelfCustomerProfileApplication profiles,IBasicKycRepository repo)=>{
            var before=Before(h);var lookup=Lookup(h);var session=await sessions.ValidateAsync(lookup,h.RequestAborted);var profile=await profiles.ReadAsync(lookup,h.RequestAborted);
            var rows=await repo.HistoryAsync(session.TenantId,profile.CustomerId,before,h.RequestAborted);foreach(var row in rows)Bound(row,session.TenantId,profile.CustomerId);
            await Confirm(h,sessions,session);await profiles.ReadAsync(lookup,h.RequestAborted);
            return new {items=rows.Take(50),nextBefore=rows.Length>50?(long?)rows[49].Version:null};
        });
        group.MapPost("/me",async(HttpContext h,TenantSessionAuthority sessions,SelfCustomerProfileApplication profiles,IBasicKycRepository repo,BasicKycPolicy policies,KycEvidenceClient evidence)=>{
            NoQuery(h);var change=await Input(h);if(change.Action is not("SUBMIT" or "WITHDRAW"))throw new TenantAccessException("KYC_ACTION_DENIED",403);
            var lookup=Lookup(h);var session=await sessions.ValidateAsync(lookup,h.RequestAborted);var profile=await profiles.ReadAsync(lookup,h.RequestAborted);
            foreach(var item in change.Evidence){var doc=await evidence.ReadAsync(session,profile.CustomerId,item.DocumentVersionId,h.RequestAborted);if(doc.FileName!=item.FileName||doc.Sha256!=item.Sha256)throw new TenantAccessException("KYC_EVIDENCE_CHANGED",409);}
            await Confirm(h,sessions,session);await profiles.ReadAsync(lookup,h.RequestAborted);
            var result=await repo.ChangeAsync(session.TenantId,session.ActorId,profile.CustomerId,change,current=>BasicKyc.Transition(current,session.TenantId,profile.CustomerId,profile.DisplayName,profile.ActorId,session.ActorId,change,policies.Read(session.TenantId),DateTimeOffset.UtcNow),h.RequestAborted);
            Bound(result,session.TenantId,profile.CustomerId);await Confirm(h,sessions,session);return result;
        });
        group.MapGet("/reviews",async(HttpContext h,TenantSessionAuthority sessions,IBasicKycRepository repo,BasicKycPolicy policies)=>{
            if(h.Request.Query.Any(x=>x.Key!="after"||x.Value.Count!=1))throw new TenantAccessException("KYC_QUERY_INVALID",400);
            var after=h.Request.Query["after"].ToString();if(after!=""&&!TenantAccessClient.Identifier(after,128))throw new TenantAccessException("KYC_QUERY_INVALID",400);
            var session=await sessions.ValidateAsync(Lookup(h),h.RequestAborted);Reviewer(policies,session);
            var rows=await repo.QueueAsync(session.TenantId,session.ActorId,after,h.RequestAborted);
            foreach(var row in rows){Bound(row,session.TenantId,row.CustomerId);if(row.ReviewerId!=session.ActorId)throw new TenantAccessException("KYC_BINDING_INVALID",503);}
            await Confirm(h,sessions,session);Reviewer(policies,session);
            // Queue contains only explicitly submitted case metadata; document access is independently checked below.
            return new KycPage(rows.Take(50).ToArray(),rows.Length>50?rows[49].CustomerId:null);
        });
        group.MapGet("/reviews/{customer}/evidence/{version}",async(string customer,string version,HttpContext h,TenantSessionAuthority sessions,IBasicKycRepository repo,BasicKycPolicy policies,KycEvidenceClient evidence,ISelfCustomerProfileRepository profiles)=>{
            NoQuery(h);Id(customer);Id(version);var session=await sessions.ValidateAsync(Lookup(h),h.RequestAborted);Reviewer(policies,session);
            var current=await Assigned(repo,profiles,session,customer,h.RequestAborted);
            var entry=current.Evidence.SingleOrDefault(x=>x.DocumentVersionId==version)??throw new TenantAccessException("KYC_EVIDENCE_NOT_FOUND",404);
            var doc=await evidence.ReadAsync(session,customer,version,h.RequestAborted);if(doc.Sha256!=entry.Sha256)throw new TenantAccessException("KYC_EVIDENCE_CHANGED",409);
            await Confirm(h,sessions,session);Reviewer(policies,session);var fresh=await Assigned(repo,profiles,session,customer,h.RequestAborted);if(fresh.Version!=current.Version)throw new TenantAccessException("KYC_VERSION_CONFLICT",409);return doc;
        });
        group.MapPost("/reviews/{customer}",async(string customer,HttpContext h,TenantSessionAuthority sessions,IBasicKycRepository repo,BasicKycPolicy policies,KycEvidenceClient evidence,ISelfCustomerProfileRepository profiles)=>{
            NoQuery(h);Id(customer);var change=await Input(h);if(change.Action is not("ACCEPT" or "REQUEST_CHANGES" or "REJECT"))throw new TenantAccessException("KYC_ACTION_DENIED",403);
            var session=await sessions.ValidateAsync(Lookup(h),h.RequestAborted);Reviewer(policies,session);
            var current=await repo.ReadAsync(session.TenantId,customer,h.RequestAborted)??throw new TenantAccessException("KYC_NOT_FOUND",404);Bound(current,session.TenantId,customer);
            if(current.ReviewerId!=session.ActorId||session.ActorId==current.OwnerActorId||!await profiles.OwnsAsync(session.TenantId,current.OwnerActorId,customer,h.RequestAborted))throw new TenantAccessException("KYC_REVIEW_DENIED",403);
            foreach(var item in current.Evidence){var doc=await evidence.ReadAsync(session,customer,item.DocumentVersionId,h.RequestAborted);if(doc.Sha256!=item.Sha256)throw new TenantAccessException("KYC_EVIDENCE_CHANGED",409);}
            await Confirm(h,sessions,session);Reviewer(policies,session);
            var result=await repo.ChangeAsync(session.TenantId,session.ActorId,customer,change,row=>BasicKyc.Transition(row,session.TenantId,customer,current.CustomerName,current.OwnerActorId,session.ActorId,change,policies.Read(session.TenantId),DateTimeOffset.UtcNow),h.RequestAborted);
            Bound(result,session.TenantId,customer);await Confirm(h,sessions,session);return result;
        });
    }
    private static async Task<KycCase> Assigned(IBasicKycRepository repo,ISelfCustomerProfileRepository profiles,TenantSessionContext session,string customer,CancellationToken ct)
    {var row=await repo.ReadAsync(session.TenantId,customer,ct)??throw new TenantAccessException("KYC_NOT_FOUND",404);Bound(row,session.TenantId,customer);if(row.ReviewerId!=session.ActorId||row.OwnerActorId==session.ActorId||row.State!="SUBMITTED"||!await profiles.OwnsAsync(session.TenantId,row.OwnerActorId,customer,ct))throw new TenantAccessException("KYC_REVIEW_DENIED",403);return row;}
    private static void Reviewer(BasicKycPolicy policy,TenantSessionContext session){if(policy.Read(session.TenantId)?.Reviewers.Any(x=>x.ActorId==session.ActorId)!=true)throw new TenantAccessException("KYC_REVIEW_DENIED",403);}
    private static void Bound(KycCase? row,string tenant,string customer){if(row is not null&&(row.TenantId!=tenant||row.CustomerId!=customer))throw new TenantAccessException("KYC_BINDING_INVALID",503);}
    private static TenantSessionLookup Lookup(HttpContext h){var(t,s)=TenantAccessClient.Headers(h);return new(t,s);}
    private static async Task Confirm(HttpContext h,TenantSessionAuthority sessions,TenantSessionContext initial){if(await sessions.ValidateAsync(Lookup(h),h.RequestAborted)!=initial)throw new TenantAccessException("KYC_SESSION_CHANGED",409);}
    private static void Id(string value){if(!TenantAccessClient.Identifier(value,160))throw new TenantAccessException("KYC_ID_INVALID",400);}
    private static void NoQuery(HttpContext h){if(h.Request.Query.Count!=0)throw new TenantAccessException("KYC_QUERY_INVALID",400);}
    private static long Before(HttpContext h){if(h.Request.Query.Count==0)return long.MaxValue;if(h.Request.Query.Count!=1||h.Request.Query["before"].Count!=1||!long.TryParse(h.Request.Query["before"],out var v)||v<1)throw new TenantAccessException("KYC_QUERY_INVALID",400);return v;}
    private static async Task<KycChange> Input(HttpContext h)
    {if(h.Request.ContentType?.Split(';')[0].Trim()!="application/json")throw new TenantAccessException("KYC_CHANGE_INVALID",400);using var doc=await JsonDocument.ParseAsync(h.Request.Body,cancellationToken:h.RequestAborted);if(doc.RootElement.ValueKind!=JsonValueKind.Object||doc.RootElement.EnumerateObject().Count()!=doc.RootElement.EnumerateObject().Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count())throw new JsonException();var c=doc.RootElement.Deserialize<KycChange>(Json)??throw new JsonException();BasicKyc.Validate(c);return c;}
    private static async ValueTask<object?> Filter(EndpointFilterInvocationContext context,EndpointFilterDelegate next)
    {var h=context.HttpContext;h.Response.Headers.CacheControl="no-store";if(h.Request.Headers.Origin.Count>0&&h.Request.Headers.Origin.ToString()!=$"{h.Request.Scheme}://{h.Request.Host}")return Results.StatusCode(403);try{return await next(context);}catch(TenantAccessException e){return Results.Json(new{error=e.Code},statusCode:e.StatusCode);}catch(TenantBoundaryException e){return Results.Json(new{error=e.Code},statusCode:e.Status);}catch(Exception e)when(e is JsonException or FormatException){return Results.Json(new{error="KYC_INPUT_INVALID"},statusCode:400);}catch(Exception e)when(e is System.Data.Common.DbException or HttpRequestException or TaskCanceledException){return Results.Json(new{error="KYC_UNAVAILABLE"},statusCode:503);}}
}
