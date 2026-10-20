using System.Text.Json;
using System.Text.Json.Serialization;
using Monergy.Platform;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
namespace Monergy.Services.CustomerIdentity;

public static class CustomerAdministrationEndpoints
{
 private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=false,RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
 public static void MapCustomerAdministration(this WebApplication app)
 {
  var group=app.MapGroup("/local/v1/administration/customers");group.AddEndpointFilter(Filter);
  group.MapGet("",async(HttpContext http,TenantAccessClient access,ICustomerAdministrationRepository repo)=>{
   var admin=await access.RequireAdministratorAsync(http);var q=Query(http.Request,"search","status","after");var query=new AdminCustomerQuery(q.GetValueOrDefault("search")??"",q.GetValueOrDefault("status")??"all",q.GetValueOrDefault("after"));CustomerAdministration.Query(query);
   var page=await repo.ListAsync(admin.TenantId,query,http.RequestAborted);if(page.TenantId!=admin.TenantId||page.Items.Count>50||page.Items.Select(x=>x.CustomerId).Distinct().Count()!=page.Items.Count||page.NextCursor is not null&&(page.Items.Count!=50||page.NextCursor!=page.Items[^1].CustomerId)||page.Total<0||page.Active<0||page.Inactive<0||page.Active+page.Inactive!=page.Total||page.Matching<page.Items.Count||page.Matching>page.Total)throw Invalid();
   foreach(var row in page.Items)Bound(row,admin.TenantId,row.CustomerId);
   await Confirm(http,access,admin);return page;
  });
  group.MapGet("/{customerId}",async(string customerId,HttpContext http,TenantAccessClient access,ICustomerAdministrationRepository repo)=>{
   var admin=await access.RequireAdministratorAsync(http);Id(customerId);Query(http.Request);var row=await repo.ReadAsync(admin.TenantId,customerId,http.RequestAborted)??throw new TenantBoundaryException("CUSTOMER_NOT_FOUND",404);Bound(row,admin.TenantId,customerId);await Confirm(http,access,admin);return row;
  });
  group.MapGet("/{customerId}/history",async(string customerId,HttpContext http,TenantAccessClient access,ICustomerAdministrationRepository repo)=>{
   var admin=await access.RequireAdministratorAsync(http);Id(customerId);var q=Query(http.Request,"beforeVersion");long? before=null;if(q.TryGetValue("beforeVersion",out var value)){if(!long.TryParse(value,out var v)||v<1||v>9007199254740991||value!=v.ToString(System.Globalization.CultureInfo.InvariantCulture))throw new TenantBoundaryException("CUSTOMER_QUERY_INVALID",400);before=v;}
   var page=await repo.HistoryAsync(admin.TenantId,customerId,before,http.RequestAborted);if(page.TenantId!=admin.TenantId||page.CustomerId!=customerId||page.Items.Count>50||page.Items.Select(x=>x.After.Version).Distinct().Count()!=page.Items.Count||page.NextBeforeVersion is not null&&(page.Items.Count!=50||page.NextBeforeVersion!=page.Items[^1].After.Version))throw Invalid();
   long last=before??long.MaxValue;foreach(var r in page.Items){Bound(r,admin.TenantId,customerId);if(r.After.Version>=last)throw Invalid();last=r.After.Version;}
   await Confirm(http,access,admin);return page;
  });
  group.MapGet("/{customerId}/changes/{operationId}",async(string customerId,string operationId,HttpContext http,TenantAccessClient access,ICustomerAdministrationRepository repo)=>{
   var admin=await access.RequireAdministratorAsync(http);Id(customerId);Operation(operationId);Query(http.Request);var r=await repo.ReceiptAsync(admin.TenantId,customerId,operationId,http.RequestAborted)??throw new TenantBoundaryException("CUSTOMER_CHANGE_NOT_FOUND",404);Bound(r,admin.TenantId,customerId);if(r.OperationId!=operationId)throw Invalid();await Confirm(http,access,admin);return r;
  });
  group.MapPost("/{customerId}/status",async(string customerId,HttpContext http,TenantAccessClient access,ICustomerAdministrationRepository repo)=>{
   var admin=await access.RequireAdministratorAsync(http);Id(customerId);Query(http.Request);var change=await Read(http);CustomerAdministration.Change(change);await Confirm(http,access,admin);
   var r=await repo.ChangeAsync(admin.TenantId,admin.ActorId,customerId,change,DateTimeOffset.UtcNow,http.RequestAborted);CustomerAdministration.Receipt(r,admin.TenantId,admin.ActorId,customerId,change);Bound(r,admin.TenantId,customerId);
   var saved=await repo.ReceiptAsync(admin.TenantId,customerId,change.OperationId,http.RequestAborted);if(saved!=r)throw Invalid();await Confirm(http,access,admin);return r;
  });
 }
 private static Task Confirm(HttpContext http,TenantAccessClient access,TenantAdminContext admin)=>ConfirmCore(http,access,admin);
 private static async Task ConfirmCore(HttpContext http,TenantAccessClient access,TenantAdminContext admin)=>CustomerAdministration.Confirm(admin,await access.RequireAdministratorAsync(http));
 private static void Id(string value){if(!TenantAccessClient.Identifier(value,128))throw new TenantBoundaryException("CUSTOMER_ID_INVALID",400);}
 private static void Operation(string value){if(!Guid.TryParseExact(value,"N",out _)||value.Any(c=>c is >='A' and <='F'))throw new TenantBoundaryException("CUSTOMER_CHANGE_INVALID",400);}
 private static TenantBoundaryException Invalid()=>new("CUSTOMER_RESPONSE_INVALID",503);
 private static void Bound(AdminCustomer c,string tenant,string id)
 {if(c.TenantId!=tenant||c.CustomerId!=id||!TenantAccessClient.Identifier(c.CustomerId,128)||string.IsNullOrWhiteSpace(c.DisplayName)||c.DisplayName.Length>120||c.DisplayName.Any(char.IsControl)||c.Version is <1 or >9007199254740991||c.Email is not null&&(c.Email.Length>320||!c.Email.Contains('@')||c.Email.Any(char.IsControl))||c.SecondaryLabel is not null&&(c.SecondaryLabel.Length>120||c.SecondaryLabel.Any(char.IsControl)))throw Invalid();}
 private static void Bound(CustomerStatusReceipt r,string tenant,string id)
 {Bound(r.Before,tenant,id);Bound(r.After,tenant,id);var c=new CustomerStatusChange(r.OperationId,r.Before.Version,r.After.Active,r.Reason);CustomerAdministration.Change(c);CustomerAdministration.Receipt(r,tenant,r.RecordedBy,id,c);if(!TenantAccessClient.Identifier(r.RecordedBy,128)||string.IsNullOrWhiteSpace(r.RecordedByLabel)||r.RecordedByLabel.Length>320||r.RecordedByLabel.Any(char.IsControl)||r.RecordedAt<DateTimeOffset.UnixEpoch)throw Invalid();}
 private static Dictionary<string,string> Query(HttpRequest request,params string[] names)
 {var q=new Dictionary<string,string>();foreach(var pair in request.Query){if(!names.Contains(pair.Key,StringComparer.Ordinal)||pair.Value.Count!=1||string.IsNullOrEmpty(pair.Value[0]))throw new TenantBoundaryException("CUSTOMER_QUERY_INVALID",400);q.Add(pair.Key,pair.Value[0]!);}return q;}
 private static async Task<CustomerStatusChange> Read(HttpContext http)
 {
  if(http.Request.ContentType?.Split(';')[0].Trim()!="application/json")throw new TenantBoundaryException("CUSTOMER_CHANGE_INVALID",400);
  http.Request.EnableBuffering(4096,16384);using var doc=await JsonDocument.ParseAsync(http.Request.Body,cancellationToken:http.RequestAborted);if(doc.RootElement.ValueKind!=JsonValueKind.Object||doc.RootElement.EnumerateObject().Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=doc.RootElement.EnumerateObject().Count())throw new JsonException();return doc.RootElement.Deserialize<CustomerStatusChange>(Json)??throw new JsonException();
 }
 private static async ValueTask<object?> Filter(EndpointFilterInvocationContext context,EndpointFilterDelegate next)
 {
  var http=context.HttpContext;http.Response.Headers.CacheControl="no-store";if(http.Request.Headers.Origin.Count>0&&http.Request.Headers.Origin.ToString()!=$"{http.Request.Scheme}://{http.Request.Host}")return Results.StatusCode(403);
  try{return await next(context);}catch(TenantBoundaryException e){return Results.Json(new{error=e.Code},statusCode:e.Status);}catch(TenantAccessException e){return Results.Json(new{error=e.Code},statusCode:e.StatusCode);}catch(JsonException){return Results.Json(new{error="CUSTOMER_CHANGE_INVALID"},statusCode:400);}catch(IOException){return Results.Json(new{error="CUSTOMER_CHANGE_TOO_LARGE"},statusCode:413);}catch(Exception e)when(e is HttpRequestException or System.Data.Common.DbException){return Results.Json(new{error="CUSTOMER_MANAGEMENT_UNAVAILABLE"},statusCode:503);}catch(OperationCanceledException)when(!http.RequestAborted.IsCancellationRequested){return Results.Json(new{error="CUSTOMER_MANAGEMENT_UNAVAILABLE"},statusCode:503);}
 }
}
