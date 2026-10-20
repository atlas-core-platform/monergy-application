using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
namespace Monergy.Services.DocumentIntelligence;
public sealed record ClassificationMonitorItem(string ProcessingId,string State,string? FailureCode,bool Retryable,int Attempt,DateTimeOffset UpdatedAt,IReadOnlyList<string> DetectedCategories);
public sealed record ClassificationMonitorPage(string TenantId,string CustomerId,string DocumentVersionId,IReadOnlyList<ClassificationMonitorItem> Items,string? NextCursor);
public interface IClassificationMonitoringRepository
{Task<ClassificationMonitorPage> MonitorAsync(string tenant,string customer,string version,string? after,CancellationToken ct);}
public static class DocumentClassificationMonitoring
{
 public static readonly string[] Categories=["INCOME","EXPENSE","LOAN","INVESTMENT","INSURANCE","PROPERTY","VEHICLE","TAX","ASSET","LIABILITY"];
 public static void MapClassificationMonitoring(this WebApplication app)
 {
  var group=app.MapGroup("/local/v1/administration/document-classifications");group.AddEndpointFilter(async(context,next)=>{
   var http=context.HttpContext;http.Response.Headers.CacheControl="no-store";if(http.Request.Headers.Origin.Count>0&&http.Request.Headers.Origin.ToString()!=$"{http.Request.Scheme}://{http.Request.Host}")return Results.StatusCode(403);
   try{return await next(context);}catch(TenantBoundaryException e){return Results.Json(new{error=e.Code},statusCode:e.Status);}catch(Exception e)when(e is HttpRequestException or InvalidOperationException){return Results.StatusCode(503);}
  });
  group.MapGet("",async(HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,IClassificationMonitoringRepository repo)=>{
   var admin=await access.RequireAdministratorAsync(http);var q=http.Request.Query;if(q.Any(x=>x.Key is not ("customerId" or "versionId" or "after")||x.Value.Count!=1)||!TenantAccessClient.Identifier(q["customerId"],128)||!TenantAccessClient.Identifier(q["versionId"],160)||q.ContainsKey("after")&&!TenantAccessClient.Identifier(q["after"],160))throw new TenantBoundaryException("CLASSIFICATION_QUERY_INVALID",400);
   string customer=q["customerId"]!,version=q["versionId"]!;if(bindings.CustomerTenants.TryGetValue(customer,out var tenant)&&tenant!=admin.TenantId)throw new TenantBoundaryException("CLASSIFICATION_NOT_FOUND",404);
   var page=await repo.MonitorAsync(admin.TenantId,customer,version,q.ContainsKey("after")?q["after"].ToString():null,http.RequestAborted);
   if(page.TenantId!=admin.TenantId||page.CustomerId!=customer||page.DocumentVersionId!=version||page.Items.Count>50||page.Items.Select(x=>x.ProcessingId).Distinct().Count()!=page.Items.Count||page.NextCursor is not null&&(page.Items.Count!=50||page.NextCursor!=page.Items[^1].ProcessingId)||page.Items.Any(x=>!TenantAccessClient.Identifier(x.ProcessingId,160)||x.State is not ("Accepted" or "Running" or "Completed" or "Failed")||x.Attempt<0||x.UpdatedAt<DateTimeOffset.UnixEpoch||x.FailureCode is not null&&!TenantAccessClient.Identifier(x.FailureCode,160)||x.DetectedCategories.Any(c=>!Categories.Contains(c,StringComparer.Ordinal))))throw new TenantBoundaryException("CLASSIFICATION_RESULT_INVALID",503);
   if(await access.RequireAdministratorAsync(http)!=admin)throw new TenantBoundaryException("ADMINISTRATOR_CONTEXT_CHANGED",409);return page;
  });
 }
}
