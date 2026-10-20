using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;
namespace Monergy.Services.JobManagement;

public static class JobAdministrationEndpoints
{
 private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=false,RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
 public static void MapJobAdministration(this WebApplication app,bool allowRetry=true)
 {
  var group=app.MapGroup("/local/v1/administration/processing");group.AddEndpointFilter(Filter);
  group.MapGet("",async(HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,IJobAdministrationRepository repo)=>{
   var admin=await access.RequireAdministratorAsync(http);var scope=Scope(admin,bindings,allowRetry);var q=Query(http.Request,"search","state","customerId","after","documentVersionId");var query=new AdminJobQuery(q.GetValueOrDefault("search")??"",q.GetValueOrDefault("state")??"all",q.GetValueOrDefault("customerId"),q.GetValueOrDefault("after"),q.GetValueOrDefault("documentVersionId"));JobAdministration.Query(query);
   var page=await repo.ListAdminAsync(scope,query,http.RequestAborted);if(page.TenantId!=scope.TenantId||page.Items.Count>50||page.Items.Select(x=>x.JobId).Distinct().Count()!=page.Items.Count||page.NextCursor is not null&&(page.Items.Count!=50||page.NextCursor!=page.Items[^1].JobId)||page.Matching<page.Items.Count||page.Matching>page.Counts.Queued+page.Counts.Processing+page.Counts.Completed+page.Counts.Failed+page.Counts.Retrying+page.Counts.Cancelled||new[]{page.Counts.Queued,page.Counts.Processing,page.Counts.Completed,page.Counts.Failed,page.Counts.Retrying,page.Counts.Cancelled}.Any(x=>x<0))throw Invalid();
   AdminJobSummary? last=null;foreach(var row in page.Items){Bound(row,scope,row.JobId);if(query.DocumentVersionId is not null&&row.DocumentVersionId!=query.DocumentVersionId||query.State!="all"&&row.State!=query.State||query.CustomerId is not null&&row.CustomerId!=query.CustomerId||!row.JobId.Contains(query.Search,StringComparison.OrdinalIgnoreCase)||last is not null&&(row.RequestedAt>last.RequestedAt||row.RequestedAt==last.RequestedAt&&StringComparer.Ordinal.Compare(row.JobId,last.JobId)>=0))throw Invalid();last=row;}
   await Confirm(http,access,admin);return page;
  });
  group.MapGet("/{jobId}",async(string jobId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,IJobAdministrationRepository repo)=>{
   var admin=await access.RequireAdministratorAsync(http);var scope=Scope(admin,bindings,allowRetry);Id(jobId);Query(http.Request);var row=await repo.ReadAdminAsync(scope,jobId,http.RequestAborted)??throw Missing();Bound(row,scope,jobId);await Confirm(http,access,admin);return row;
  });
  group.MapGet("/{jobId}/attempts",async(string jobId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,IJobAdministrationRepository repo)=>{
   var admin=await access.RequireAdministratorAsync(http);var scope=Scope(admin,bindings,allowRetry);Id(jobId);var q=Query(http.Request,"beforeAttempt");int? before=null;if(q.TryGetValue("beforeAttempt",out var value)){if(!int.TryParse(value,out var number)||number<1||value!=number.ToString(CultureInfo.InvariantCulture))throw BadQuery();before=number;}
   var page=await repo.AttemptsAdminAsync(scope,jobId,before,http.RequestAborted);if(page.TenantId!=scope.TenantId||page.JobId!=jobId||page.Items.Count>50||page.NextBeforeAttempt is not null&&(page.Items.Count!=50||page.NextBeforeAttempt!=page.Items[^1].Attempt))throw Invalid();var last=before??int.MaxValue;
   foreach(var row in page.Items){if(row.Attempt<1||row.Attempt>=last||row.StartedAt<DateTimeOffset.UnixEpoch||row.EndedAt<row.StartedAt||row.Outcome is not null&&!TenantAccessClient.Identifier(row.Outcome,160)||row.FailureCode is not null&&!TenantAccessClient.Identifier(row.FailureCode,160))throw Invalid();last=row.Attempt;}
   await Confirm(http,access,admin);return page;
  });
  group.MapGet("/{jobId}/history",async(string jobId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,IJobAdministrationRepository repo)=>{
   var admin=await access.RequireAdministratorAsync(http);var scope=Scope(admin,bindings,allowRetry);Id(jobId);var q=Query(http.Request,"beforeId");var before=q.GetValueOrDefault("beforeId");if(before is not null)Operation(before);
   var page=await repo.HistoryAdminAsync(scope,jobId,before,http.RequestAborted);if(page.TenantId!=scope.TenantId||page.JobId!=jobId||page.Items.Count>50||page.Items.Select(x=>x.OperationId).Distinct().Count()!=page.Items.Count||page.NextBeforeId is not null&&(page.Items.Count!=50||page.NextBeforeId!=page.Items[^1].OperationId))throw Invalid();AdminJobReceipt? last=null;
   foreach(var row in page.Items){Bound(row,scope,jobId);if(last is not null&&(row.RecordedAt>last.RecordedAt||row.RecordedAt==last.RecordedAt&&StringComparer.Ordinal.Compare(row.OperationId,last.OperationId)>=0))throw Invalid();last=row;}await Confirm(http,access,admin);return page;
  });
  group.MapGet("/{jobId}/changes/{operationId}",async(string jobId,string operationId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,IJobAdministrationRepository repo)=>{
   var admin=await access.RequireAdministratorAsync(http);var scope=Scope(admin,bindings,allowRetry);Id(jobId);Operation(operationId);Query(http.Request);var receipt=await repo.ReceiptAdminAsync(scope,jobId,operationId,http.RequestAborted)??throw new TenantBoundaryException("PROCESSING_CHANGE_NOT_FOUND",404);Bound(receipt,scope,jobId);if(receipt.OperationId!=operationId)throw Invalid();await Confirm(http,access,admin);return receipt;
  });
  group.MapPost("/{jobId}/changes",async(string jobId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,IJobAdministrationRepository repo,IJobRepository jobs,IJobAuthorizationPolicy authorization,IJobConsentDecisionPort consent)=>{
   var admin=await access.RequireAdministratorAsync(http);var scope=Scope(admin,bindings,allowRetry);Id(jobId);Query(http.Request);var change=await Read(http);JobAdministration.Change(change);
   var current=await jobs.GetAsync(jobId,http.RequestAborted);if(current is null||!scope.Owns(current))throw Missing();
   var prior=await repo.ReceiptAdminAsync(scope,jobId,change.OperationId,http.RequestAborted);
   if(prior is not null){JobAdministration.Match(prior,scope,admin.ActorId,jobId,change);Bound(prior,scope,jobId);await Confirm(http,access,admin);return prior;}
   if(change.Action=="retry")
   {
    if(!scope.AllowRetry||!JobAdministration.CanRetry(current))throw new TenantBoundaryException("PROCESSING_RETRY_UNAVAILABLE",409);
    var denied=await authorization.AuthorizeAsync(current.Security,current,current.CorrelationId,http.RequestAborted)??await consent.EvaluateAsync(current.Security,current,current.CorrelationId,http.RequestAborted);
    if(denied is not null)throw new TenantBoundaryException(denied.Category==Monergy.Contracts.ContractErrorCategory.DependencyFailure?"PROCESSING_PERMISSION_UNAVAILABLE":"PROCESSING_CUSTOMER_PERMISSION_REQUIRED",denied.Category==Monergy.Contracts.ContractErrorCategory.DependencyFailure?503:409);
   }
   await Confirm(http,access,admin);var receipt=await repo.ChangeAdminAsync(scope,admin.ActorId,jobId,change,DateTimeOffset.UtcNow,http.RequestAborted);MatchResult(receipt,scope,admin.ActorId,jobId,change);Bound(receipt,scope,jobId);
   var saved=await repo.ReceiptAdminAsync(scope,jobId,change.OperationId,http.RequestAborted);if(saved!=receipt)throw Invalid();await Confirm(http,access,admin);return receipt;
  });
 }
 private static AdminJobScope Scope(TenantAdminContext admin,TenantBoundaryOptions bindings,bool allowRetry)=>new(admin.TenantId,Array.AsReadOnly(bindings.CustomerTenants.Where(x=>x.Value!=admin.TenantId).Select(x=>x.Key).ToArray()),allowRetry);
 private static async Task Confirm(HttpContext http,TenantAccessClient access,TenantAdminContext admin){if(await access.RequireAdministratorAsync(http)!=admin)throw new TenantBoundaryException("ADMINISTRATOR_CONTEXT_CHANGED",409);}
 private static void Bound(AdminJobSummary row,AdminJobScope scope,string jobId)
 {if(row.TenantId!=scope.TenantId||row.JobId!=jobId||!TenantAccessClient.Identifier(row.JobId,128)||!TenantAccessClient.Identifier(row.CustomerId,128)||scope.ForeignCustomers.Contains(row.CustomerId,StringComparer.Ordinal)||row.Task is not ("Document processing" or "Background task")||!JobAdministration.States.Contains(row.State,StringComparer.Ordinal)||row.DocumentVersionId is not null&&!TenantAccessClient.Identifier(row.DocumentVersionId,160)||row.Attempt<0||row.RetryCount<0||row.ReplayCount<0||row.RequestedAt<DateTimeOffset.UnixEpoch||row.UpdatedAt<DateTimeOffset.UnixEpoch||row.Revision.Length!=64||row.Revision.Any(x=>!char.IsAsciiHexDigit(x)||x is >='A' and <='F')||row.FailureCode is not null&&!TenantAccessClient.Identifier(row.FailureCode,160)||row.CancellationPending&&(row.State!="PROCESSING"||row.CanRetry||row.CanCancel)||row.CanRetry&&(row.Task!="Document processing"||row.State is not ("FAILED" or "RETRYING"))||row.CanCancel&&row.State is "COMPLETED" or "CANCELLED"||row.NeedsOwnerReview&&(row.CanRetry||row.CanCancel))throw Invalid();}
 private static void Bound(AdminJobReceipt row,AdminJobScope scope,string jobId)
 {
  Bound(row.Before,scope,jobId);Bound(row.After,scope,jobId);var change=new AdminJobChange(row.OperationId,row.Before.Revision,row.Action,row.Reason);JobAdministration.Change(change);MatchResult(row,scope,row.RecordedBy,jobId,change);
  if(!TenantAccessClient.Identifier(row.RecordedBy,128)||row.RecordedAt<DateTimeOffset.UnixEpoch||row.RecordedAt>DateTimeOffset.UtcNow.AddSeconds(5)||row.Before.DocumentVersionId!=row.After.DocumentVersionId||row.Before.Task!=row.After.Task||row.Before.RequestedAt!=row.After.RequestedAt||row.Before.Attempt!=row.After.Attempt||row.Before.RetryCount!=row.After.RetryCount||row.After.Revision==row.Before.Revision||row.After.NeedsOwnerReview||row.After.CanRetry||row.After.CanCancel&&row.Action=="cancel")throw Invalid();
  if(row.Action=="retry"&&(!row.Before.CanRetry||row.After.ReplayCount!=row.Before.ReplayCount+1||row.After.State!="RETRYING"||row.After.CancellationPending||row.After.TerminalAt is not null))throw Invalid();
  if(row.Action=="cancel"&&(!row.Before.CanCancel||row.After.ReplayCount!=row.Before.ReplayCount||row.After.State!=(row.Before.State=="PROCESSING"?"PROCESSING":"CANCELLED")||row.After.CancellationPending!=(row.After.State=="PROCESSING")||row.After.NextAttemptAt is not null))throw Invalid();
 }
 private static void MatchResult(AdminJobReceipt row,AdminJobScope scope,string actor,string jobId,AdminJobChange change)
 {try{JobAdministration.Match(row,scope,actor,jobId,change);}catch(TenantBoundaryException){throw Invalid();}}
 private static void Id(string value){if(!TenantAccessClient.Identifier(value,128))throw BadQuery();}
 private static void Operation(string value){if(!Guid.TryParseExact(value,"N",out _)||value.Any(x=>x is >='A' and <='F'))throw BadQuery();}
 private static TenantBoundaryException BadQuery()=>new("PROCESSING_QUERY_INVALID",400);
 private static TenantBoundaryException Missing()=>new("PROCESSING_JOB_NOT_FOUND",404);
 private static TenantBoundaryException Invalid()=>new("PROCESSING_RESULT_INVALID",503);
 private static Dictionary<string,string> Query(HttpRequest request,params string[] names)
 {var result=new Dictionary<string,string>();foreach(var pair in request.Query){if(!names.Contains(pair.Key,StringComparer.Ordinal)||pair.Value.Count!=1||string.IsNullOrEmpty(pair.Value[0]))throw BadQuery();result.Add(pair.Key,pair.Value[0]!);}return result;}
 private static async Task<AdminJobChange> Read(HttpContext http)
 {if(http.Request.ContentType?.Split(';')[0].Trim()!="application/json")throw new TenantBoundaryException("PROCESSING_CHANGE_INVALID",400);http.Request.EnableBuffering(4096,16384);using var doc=await JsonDocument.ParseAsync(http.Request.Body,cancellationToken:http.RequestAborted);if(doc.RootElement.ValueKind!=JsonValueKind.Object||doc.RootElement.EnumerateObject().Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=doc.RootElement.EnumerateObject().Count())throw new JsonException();return doc.RootElement.Deserialize<AdminJobChange>(Json)??throw new JsonException();}
 private static async ValueTask<object?> Filter(EndpointFilterInvocationContext context,EndpointFilterDelegate next)
 {var http=context.HttpContext;http.Response.Headers.CacheControl="no-store";if(http.Request.Headers.Origin.Count>0&&http.Request.Headers.Origin.ToString()!=$"{http.Request.Scheme}://{http.Request.Host}")return Results.StatusCode(403);try{return await next(context);}catch(TenantBoundaryException error){return Results.Json(new{error=error.Code},statusCode:error.Status);}catch(JsonException){return Results.Json(new{error="PROCESSING_CHANGE_INVALID"},statusCode:400);}catch(IOException){return Results.Json(new{error="PROCESSING_CHANGE_TOO_LARGE"},statusCode:413);}catch(Exception error)when(error is HttpRequestException or System.Data.Common.DbException or InvalidOperationException or ArgumentException){return Results.Json(new{error="PROCESSING_UNAVAILABLE"},statusCode:503);}catch(OperationCanceledException)when(!http.RequestAborted.IsCancellationRequested){return Results.Json(new{error="PROCESSING_UNAVAILABLE"},statusCode:503);}}
}
