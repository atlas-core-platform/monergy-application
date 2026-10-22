using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
namespace Monergy.UatGateway;
public interface IProductAuditWriter { Task AppendAsync(ProductAuditRecord r,CancellationToken ct); }
public sealed class ProductAuditWriter:IProductAuditWriter,IDisposable
{
 private readonly HttpClient client=new(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false}){Timeout=TimeSpan.FromSeconds(3),MaxResponseContentBufferSize=16384};
 private readonly string token;
 public ProductAuditWriter(IConfiguration config){client.BaseAddress=TenantAccessIntegration.LoopbackRoot(TenantAccessIntegration.Required(config,"Monergy:ProductAudit:Url"));token=TenantAccessIntegration.Required(config,"Monergy:ProductAudit:Token");TenantAccessIntegration.ValidateToken(token);}
 public async Task AppendAsync(ProductAuditRecord r,CancellationToken ct)
 {ProductAuditProtocol.Validate(r);using var request=new HttpRequestMessage(HttpMethod.Post,"reference/events/product-activity"){Content=JsonContent.Create(r)};request.Headers.Add("X-Monergy-Reference-Transport",token);request.Headers.Add("X-Monergy-Reference-Tenant",r.TenantId);try{using var response=await client.SendAsync(request,ct);if(!response.IsSuccessStatusCode||await response.Content.ReadFromJsonAsync<ProductAuditRecord>(cancellationToken:ct)!=r)throw new TenantBoundaryException("AUDIT_OWNER_UNAVAILABLE",503);}catch(Exception e)when(e is HttpRequestException or JsonException or OperationCanceledException){throw new TenantBoundaryException("AUDIT_OWNER_UNAVAILABLE",503);}}
 public void Dispose()=>client.Dispose();
}
public static class ProductAuditCapture
{
 private static readonly string[] Prefixes=["/connections-api","/product-api","/identity-api","/access-api","/consent-api","/evidence-api","/classification-api","/ocr-api","/jobs-api","/audit-api","/contracts","/uat-api"];
 public static bool Sensitive(PathString path)=>Prefixes.Any(p=>path.StartsWithSegments(p));
 public static string Action(string path,string method)
 {
  if(path.EndsWith("/customer-registration/sign-in",StringComparison.Ordinal)||path.Contains("tenant-sessions",StringComparison.Ordinal)||path.Contains("customer-email-access",StringComparison.Ordinal))return "LOGIN";
  if(path.Contains("/export",StringComparison.Ordinal))return "DATA_EXPORT";
  if(path.StartsWith("/consent-api/",StringComparison.Ordinal)&&method!="GET")return method=="DELETE"?"CONSENT_REVOKE":"CONSENT_GRANT";
  if(path.StartsWith("/product-api/v1/document-vault",StringComparison.Ordinal))return method=="GET"?"DOCUMENT_VIEW":"DOCUMENT_UPDATE";
  if(path.StartsWith("/product-api/v1/evidence",StringComparison.Ordinal))return method=="GET"?"DOCUMENT_VIEW":method=="POST"&&!path.Contains("/processing",StringComparison.Ordinal)?"DOCUMENT_UPLOAD":"DOCUMENT_UPDATE";
  if(path.StartsWith("/connections-api/",StringComparison.Ordinal))return path.EndsWith("/import",StringComparison.Ordinal)?"DOCUMENT_UPLOAD":"API_ACCESS";
  if(path.Contains("/reports",StringComparison.Ordinal)&&method=="POST")return "REPORT_GENERATION";
  return "API_ACCESS";
 }
 public static void UseProductAudit(this WebApplication app)
 {
  if(!app.Configuration.GetValue<bool>("Monergy:ProductAudit:Enabled"))return;
  app.Use(async(http,next)=>{
   if(!Sensitive(http.Request.Path)){await next(http);return;}
   var tenant=http.Request.Headers["X-Monergy-Tenant"].Count==1?http.Request.Headers["X-Monergy-Tenant"].ToString():null;
   // Login has no established session. Read only its bounded tenant field, never retain credentials/body.
   if(tenant is null&&http.Request.Path.StartsWithSegments("/identity-api")&&http.Request.ContentLength is null or >0 and <=8192)
   {http.Request.EnableBuffering(8192,8192);try{using var body=await JsonDocument.ParseAsync(http.Request.Body,cancellationToken:http.RequestAborted);if(body.RootElement.ValueKind==JsonValueKind.Object&&body.RootElement.TryGetProperty("tenantId",out var t)&&t.ValueKind==JsonValueKind.String)tenant=t.GetString();}catch(JsonException){}catch(IOException){http.Response.StatusCode=413;return;}finally{http.Request.Body.Position=0;}}
   // Tenantless invalid ingress has no legitimate tenant audience. It cannot select a database.
   if(!TenantAccessClient.Identifier(tenant,64)){await next(http);return;}
   string? actor=null,customer=null;var identityUnavailable=false;var owners=http.RequestServices.GetRequiredService<CustomerOwnerClient>();
   if(http.Request.Headers.ContainsKey("X-Monergy-Session"))
   {try{var session=await owners.SessionAsync(http);actor=session.ActorId;customer=(await owners.OwnedCustomerForSetupAsync(tenant!,actor,http.RequestAborted))?.CustomerId;}catch(TenantBoundaryException e){identityUnavailable=e.Status==503;}}
   var path=http.Request.Path.Value??"";var action=Action(path,http.Request.Method);
   // A route template/category is evidence; caller path/query text and arbitrary target IDs are not.
   var parts=path.Split('/',StringSplitOptions.RemoveEmptyEntries);var requestedResource=action.StartsWith("DOCUMENT_",StringComparison.Ordinal)&&parts.Length>3&&TenantAccessClient.Identifier(parts[3],160)?parts[3]:null;
   var r=new ProductAuditRecord(tenant!,"",Guid.NewGuid().ToString("N"),"REQUESTED",actor,actor is not null?"USER":identityUnavailable?"UNRECORDED":"UNAUTHENTICATED",customer,action,"Product Gateway",action.ToLowerInvariant(),requestedResource,null,DateTimeOffset.UtcNow,"REQUESTED",http.Request.Method);
   r=r with{EventId=r.RequestId+":REQUESTED"};var writer=http.RequestServices.GetRequiredService<IProductAuditWriter>();await writer.AppendAsync(r,http.RequestAborted);
   var original=http.Response.Body;await using var buffer=new AuditResponseBuffer();http.Response.Body=buffer;
   try
   {
    await next(http);var status=http.Response.StatusCode;
    var outcome=status==202?"ACCEPTED":status>=200&&status<400?"HTTP_SUCCEEDED":"HTTP_FAILED";
    // Preserve distinction between transport response and domain completion, especially asynchronous work.
    var result=r with{EventId=r.RequestId+":RESULT",Phase="RESULT",OccurredAt=DateTimeOffset.UtcNow,Outcome=outcome,Reason="HTTP_"+status};
    if(status is >=200 and <300&&http.Response.ContentType?.StartsWith("application/json",StringComparison.OrdinalIgnoreCase)==true&&buffer.Length<=1_500_000)
    {buffer.Position=0;try{using var json=await JsonDocument.ParseAsync(buffer);result=Metadata(result,json.RootElement);}catch(JsonException){}}
    await writer.AppendAsync(result,CancellationToken.None);
    http.Response.Body=original;buffer.Position=0;await buffer.CopyToAsync(original,http.RequestAborted);
   }
   catch(Exception e)
   {
    http.Response.Body=original;http.Response.ContentLength=null;
    // An owner may already have committed. Do not invent rollback or auto-retry the business request.
    if(e is not TenantBoundaryException{Code:"AUDIT_OWNER_UNAVAILABLE"})
    {try{var denial=e as TenantBoundaryException;await writer.AppendAsync(r with{EventId=r.RequestId+":RESULT",Phase="RESULT",OccurredAt=DateTimeOffset.UtcNow,Outcome=denial?.Status is 400 or 401 or 403 or 404 or 409 or 422?"HTTP_FAILED":"UNKNOWN",Reason=denial?.Status is 400 or 401 or 403 or 404 or 409 or 422?"HTTP_"+denial.Status:"OWNER_RESULT_UNCONFIRMED"},CancellationToken.None);}catch(TenantBoundaryException){}}
    throw;
   }
   finally{http.Response.Body=original;}
  });
 }
 public static ProductAuditRecord Metadata(ProductAuditRecord r,JsonElement json)
 {
  if(json.ValueKind!=JsonValueKind.Object)return r;
  string? Field(string name,int max)=>json.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String&&TenantAccessClient.Identifier(v.GetString(),max)?v.GetString():null;
  if(Field("tenantId",64) is string tenant&&tenant!=r.TenantId)throw new TenantBoundaryException("AUDIT_RESPONSE_TENANT_INVALID",503);
  var actor=r.Action=="LOGIN"?Field("actorId",128):null;
  return r with{ActorId=actor??r.ActorId,ActorType=actor is null?r.ActorType:"USER",CustomerId=r.CustomerId??Field("customerId",128),DocumentId=r.Action.StartsWith("DOCUMENT_",StringComparison.Ordinal)?Field("documentId",160):null};
 }
 private sealed class AuditResponseBuffer:MemoryStream
 {
  private void Bound(int n){if(Length+n>8_388_608)throw new TenantBoundaryException("AUDIT_RESPONSE_TOO_LARGE",503);}
  public override void Write(byte[] buffer,int offset,int count){Bound(count);base.Write(buffer,offset,count);}
  public override void Write(ReadOnlySpan<byte> buffer){Bound(buffer.Length);base.Write(buffer);}
  public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default){Bound(buffer.Length);return base.WriteAsync(buffer,ct);}
  public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken ct){Bound(count);return base.WriteAsync(buffer,offset,count,ct);}
 }
}
