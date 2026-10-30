using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Monergy.Platform;
using Monergy.Services.Audit.Application;
namespace Monergy.Services.Audit;
public static class ProductAuditEndpoints
{
 public static void MapProductAuditLog(this WebApplication app)
 {
  var group=app.MapGroup("/local/v1/administration/audit-log");group.AddEndpointFilter(async(context,next)=>{
   var http=context.HttpContext;http.Response.Headers.CacheControl="no-store";if(http.Request.Headers.Origin.Count>0&&http.Request.Headers.Origin.ToString()!=$"{http.Request.Scheme}://{http.Request.Host}")return Results.StatusCode(403);
   try{return await next(context);}catch(TenantBoundaryException e){return Results.Json(new{error=e.Code},statusCode:e.Status);}catch(Monergy.Contracts.TenantAccessException e){return Results.Json(new{error=e.Code},statusCode:e.StatusCode);}catch(Exception e)when(e is HttpRequestException or System.Data.Common.DbException or InvalidOperationException){return Results.StatusCode(503);}catch(OperationCanceledException)when(!http.RequestAborted.IsCancellationRequested){return Results.StatusCode(503);}
  });
  group.MapGet("",async(HttpContext http,TenantAccessClient access,IProductAuditLog repo)=>await Page(http,access,repo));
  group.MapGet("/sign-in-attention",async(HttpContext http,TenantAccessClient access,IProductAuditLog repo)=>{
   var admin=await access.RequireAdministratorAsync(http);if(http.Request.Query.Count!=0)throw Bad();var summary=await repo.ReviewSignInSecurityAsync(admin.TenantId,DateTimeOffset.UtcNow,http.RequestAborted);ProductAuditLog.Bound(summary,admin.TenantId);await Confirm(http,access,admin);return summary;
  });
  group.MapGet("/export",async(HttpContext http,TenantAccessClient access,IProductAuditLog repo)=>{
   var page=await Page(http,access,repo);http.Response.Headers.ContentDisposition="attachment; filename=monergy-audit-page.json";
   return Results.Json(new{schemaVersion="1.0",exportedAt=DateTimeOffset.UtcNow,scope="Current filtered page only; use nextCursor to continue. No unrecorded fields are inferred.",page});
  });
  group.MapGet("/{id}",async(string id,HttpContext http,TenantAccessClient access,IProductAuditLog repo)=>{
   var admin=await access.RequireAdministratorAsync(http);if(http.Request.Query.Count!=0||!TenantAccessClient.Identifier(id,240))throw Bad();var row=await repo.ReadActivityAsync(admin.TenantId,id,http.RequestAborted)??throw new TenantBoundaryException("AUDIT_NOT_FOUND",404);ProductAuditLog.Bound(row,admin.TenantId);if(row.EvidenceId!=id)throw Bad();await Confirm(http,access,admin);return row;
  });
 }
 private static async Task<AuditLogPage> Page(HttpContext http,TenantAccessClient access,IProductAuditLog repo)
 {
  var admin=await access.RequireAdministratorAsync(http);var keys=new[]{"search","action","outcome","actorId","customerId","from","to","after"};if(http.Request.Query.Any(x=>!keys.Contains(x.Key,StringComparer.Ordinal)||x.Value.Count!=1||string.IsNullOrEmpty(x.Value[0])))throw Bad();string? Q(string key)=>http.Request.Query.ContainsKey(key)?http.Request.Query[key].ToString():null;
  var q=new AuditLogQuery(Q("search")??"",Q("action")??"all",Q("outcome")??"all",Q("actorId"),Q("customerId"),Q("from"),Q("to"),Q("after"));ProductAuditLog.Query(q);
  var page=await repo.SearchAsync(admin.TenantId,q,http.RequestAborted);if(page.TenantId!=admin.TenantId||page.Items.Count>50||page.Matching<page.Items.Count||page.Coverage!=ProductAuditLog.Coverage||page.Items.Select(x=>x.EvidenceId).Distinct().Count()!=page.Items.Count||page.NextCursor is not null&&(page.Items.Count!=50||page.NextCursor!=page.Items[^1].EvidenceId))throw new TenantBoundaryException("AUDIT_RESULT_INVALID",503);
  foreach(var r in page.Items){ProductAuditLog.Bound(r,admin.TenantId);if(!ProductAuditLog.Matches(r,q))throw new TenantBoundaryException("AUDIT_RESULT_INVALID",503);}await Confirm(http,access,admin);return page;
 }
 private static async Task Confirm(HttpContext http,TenantAccessClient access,TenantAdminContext a){if(await access.RequireAdministratorAsync(http)!=a)throw new TenantBoundaryException("ADMINISTRATOR_CONTEXT_CHANGED",409);}
 private static TenantBoundaryException Bad()=>new("AUDIT_QUERY_INVALID",400);
}
