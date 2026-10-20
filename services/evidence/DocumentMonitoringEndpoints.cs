using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Monergy.Platform;
using Monergy.Services.Evidence.Application;
namespace Monergy.Services.Evidence;
public static class DocumentMonitoringEndpoints
{
 public static void MapDocumentMonitoring(this WebApplication app)
 {
  var group=app.MapGroup("/local/v1/administration/documents");group.AddEndpointFilter(Filter);
  group.MapGet("",async(HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,IDocumentMonitoringRepository repo)=>{
   var admin=await access.RequireAdministratorAsync(http);var scope=Scope(admin,bindings);var q=Query(http.Request,"search","scan","customerId","after","documentId");var query=new DocumentMonitorQuery(q.GetValueOrDefault("search")??"",q.GetValueOrDefault("scan")??"all",q.GetValueOrDefault("customerId"),q.GetValueOrDefault("after"),q.GetValueOrDefault("documentId"));DocumentMonitoring.Query(query);
   var page=await repo.MonitorAsync(scope,query,http.RequestAborted);if(page.TenantId!=scope.TenantId||page.Items.Count>50||page.Matching<page.Items.Count||page.Total<page.Matching||page.Total!=page.VerifiedAtIntake+page.Unrecorded||page.VerifiedAtIntake<0||page.Unrecorded<0||page.Items.Select(x=>x.DocumentVersionId).Distinct().Count()!=page.Items.Count||page.NextCursor is not null&&(page.Items.Count!=50||page.NextCursor!=page.Items[^1].DocumentVersionId))throw Invalid();MonitoredDocument? last=null;
   foreach(var row in page.Items){Bound(row,scope);if(query.CustomerId is not null&&row.CustomerId!=query.CustomerId||query.DocumentId is not null&&row.DocumentId!=query.DocumentId||query.Scan!="all"&&row.ScanStatus!=query.Scan||last is not null&&(row.RegisteredAt>last.RegisteredAt||row.RegisteredAt==last.RegisteredAt&&StringComparer.Ordinal.Compare(row.DocumentVersionId,last.DocumentVersionId)>=0))throw Invalid();last=row;}
   await Confirm(http,access,admin);return page;
  });
  group.MapGet("/{versionId}",async(string versionId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,IDocumentMonitoringRepository repo)=>{
   var admin=await access.RequireAdministratorAsync(http);Query(http.Request);if(!TenantAccessClient.Identifier(versionId,160))throw Bad();var scope=Scope(admin,bindings);var row=await repo.MonitorReadAsync(scope,versionId,http.RequestAborted)??throw new TenantBoundaryException("DOCUMENT_MONITOR_NOT_FOUND",404);Bound(row,scope);if(row.DocumentVersionId!=versionId)throw Invalid();await Confirm(http,access,admin);return row;
  });
 }
 private static DocumentMonitorScope Scope(TenantAdminContext admin,TenantBoundaryOptions bindings)=>new(admin.TenantId,bindings.CustomerTenants.Where(x=>x.Value!=admin.TenantId).Select(x=>x.Key).ToArray());
 private static async Task Confirm(HttpContext http,TenantAccessClient access,TenantAdminContext admin){if(await access.RequireAdministratorAsync(http)!=admin)throw new TenantBoundaryException("ADMINISTRATOR_CONTEXT_CHANGED",409);}
 private static void Bound(MonitoredDocument row,DocumentMonitorScope scope)
 {if(row.TenantId!=scope.TenantId||scope.ForeignCustomers.Contains(row.CustomerId,StringComparer.Ordinal)||!TenantAccessClient.Identifier(row.DocumentId,160)||!TenantAccessClient.Identifier(row.DocumentVersionId,160)||!TenantAccessClient.Identifier(row.CustomerId,128)||row.Version<1||row.ContentLength<0||row.ContentLength>9007199254740991||row.RegisteredAt<DateTimeOffset.UnixEpoch||row.ReceivedAt<DateTimeOffset.UnixEpoch||row.FileName.Length>255||row.FileName.Any(char.IsControl)||row.ContentType.Length>255||row.ContentType.Any(char.IsControl)||row.ScanStatus is not ("VERIFIED_AT_INTAKE" or "UNRECORDED")||row.ScanStatus=="VERIFIED_AT_INTAKE"&&(row.ScanEvidence is null||!EvidenceMalwarePolicy.Verified(row.ScanEvidence,row.ScanEvidence.ContentSha256,row.RegisteredAt))||row.ScanStatus=="UNRECORDED"&&row.ScanEvidence is not null)throw Invalid();}
 private static Dictionary<string,string> Query(HttpRequest request,params string[] allowed){var q=new Dictionary<string,string>();foreach(var x in request.Query){if(!allowed.Contains(x.Key,StringComparer.Ordinal)||x.Value.Count!=1||string.IsNullOrEmpty(x.Value[0]))throw Bad();q.Add(x.Key,x.Value[0]!);}return q;}
 private static TenantBoundaryException Bad()=>new("DOCUMENT_MONITOR_QUERY_INVALID",400);
 private static TenantBoundaryException Invalid()=>new("DOCUMENT_MONITOR_RESULT_INVALID",503);
 private static async ValueTask<object?> Filter(EndpointFilterInvocationContext context,EndpointFilterDelegate next)
 {var http=context.HttpContext;http.Response.Headers.CacheControl="no-store";if(http.Request.Headers.Origin.Count>0&&http.Request.Headers.Origin.ToString()!=$"{http.Request.Scheme}://{http.Request.Host}")return Results.StatusCode(403);try{return await next(context);}catch(TenantBoundaryException e){return Results.Json(new{error=e.Code},statusCode:e.Status);}catch(Exception e)when(e is HttpRequestException or System.Data.Common.DbException or InvalidOperationException){return Results.Json(new{error="DOCUMENT_MONITOR_UNAVAILABLE"},statusCode:503);}catch(OperationCanceledException)when(!http.RequestAborted.IsCancellationRequested){return Results.StatusCode(503);}}
}
