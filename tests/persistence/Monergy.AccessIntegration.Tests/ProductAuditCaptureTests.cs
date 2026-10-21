using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monergy.Platform;
using Monergy.Services.Audit.Application;
using Monergy.Services.Audit.Infrastructure;
using Monergy.UatGateway;
using Xunit;
namespace Monergy.AccessIntegration.Tests;
public sealed class ProductAuditCaptureTests
{
 [Theory]
 [InlineData("/identity-api/local/v1/tenant-sessions","POST","LOGIN")]
 [InlineData("/consent-api/local/v1/consents/customers/C001/sharing","POST","CONSENT_GRANT")]
 [InlineData("/consent-api/local/v1/consents/customers/C001/grants/G001","DELETE","CONSENT_REVOKE")]
 [InlineData("/product-api/v1/evidence","POST","DOCUMENT_UPLOAD")]
 [InlineData("/product-api/v1/evidence/source/document-version-one","GET","DOCUMENT_VIEW")]
 [InlineData("/product-api/v1/evidence/document-version-one/processing","POST","DOCUMENT_UPDATE")]
 [InlineData("/product-api/v1/reports","POST","REPORT_GENERATION")]
 [InlineData("/audit-api/local/v1/administration/audit-log/export","GET","DATA_EXPORT")]
 [InlineData("/product-api/v1/context","GET","API_ACCESS")]
 public void MinimumActivityMatrixUsesRouteCategoriesWithoutLoggingRawUrls(string path,string method,string action)
 {Assert.True(ProductAuditCapture.Sensitive(path));Assert.Equal(action,ProductAuditCapture.Action(path,method));Assert.False(ProductAuditCapture.Sensitive("/health/ready"));}
 [Fact]public async Task RealHttpCaptureUsesVerifiedIdentityAndNoPayloadQueryTokensIpOrDevice()
 {await using var h=await Host.Start();var response=await h.Client.PostAsJsonAsync("/product-api/v1/evidence?secret=do-not-store",new{salary=999999,token="private"});Assert.Equal(HttpStatusCode.OK,response.StatusCode);var p=await h.Writer.Repo.SearchAsync("T001",new(),default);Assert.Equal(2,p.Matching);var result=p.Items.Single(x=>x.Phase=="RESULT");Assert.Equal("A900",result.ActorId);Assert.Equal("C001",result.CustomerId);Assert.Equal("document-one",result.DocumentId);Assert.Equal("HTTP_SUCCEEDED",result.Outcome);var serialized=JsonSerializer.Serialize(p);Assert.DoesNotContain("999999",serialized);Assert.DoesNotContain("do-not-store",serialized);Assert.DoesNotContain("private",serialized);Assert.Null(result.IpAddress);Assert.Null(result.Device);}
 [Fact]public async Task UnavailableAuditBeforeOperationPreventsExecution()
 {await using var h=await Host.Start();h.Writer.FailAt=1;Assert.Equal(HttpStatusCode.ServiceUnavailable,(await h.Client.GetAsync("/product-api/v1/evidence")).StatusCode);Assert.Equal(0,h.Executed);}
 [Fact]public async Task UnavailableResultNeverReturnsBufferedSuccessAndLeavesUnresolvedRequest()
 {await using var h=await Host.Start();h.Writer.FailAt=2;var response=await h.Client.GetAsync("/product-api/v1/evidence");Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.Equal(1,h.Executed);Assert.DoesNotContain("document-one",await response.Content.ReadAsStringAsync());var p=await h.Writer.Repo.SearchAsync("T001",new(),default);Assert.Single(p.Items);Assert.Equal("REQUESTED",p.Items[0].Outcome);}
 [Fact]public async Task LostAuditReceiptDoesNotRepeatBusinessOperationOrInventRollback()
 {await using var h=await Host.Start();h.Writer.FailAt=2;h.Writer.LoseAfterAppend=true;Assert.Equal(HttpStatusCode.ServiceUnavailable,(await h.Client.GetAsync("/product-api/v1/evidence")).StatusCode);Assert.Equal(1,h.Executed);var p=await h.Writer.Repo.SearchAsync("T001",new(),default);Assert.Equal(2,p.Matching);Assert.Contains(p.Items,x=>x.Outcome=="HTTP_SUCCEEDED");}
 [Fact]public async Task AsyncAcceptedAndOwnerDenialKeepTheirExactBoundaryMeaning()
 {await using var h=await Host.Start();h.Status=202;Assert.Equal(HttpStatusCode.Accepted,(await h.Client.GetAsync("/product-api/v1/evidence")).StatusCode);Assert.Contains((await h.Writer.Repo.SearchAsync("T001",new(),default)).Items,x=>x.Outcome=="ACCEPTED");h.Status=403;Assert.Equal(HttpStatusCode.Forbidden,(await h.Client.GetAsync("/product-api/v1/evidence")).StatusCode);Assert.Contains((await h.Writer.Repo.SearchAsync("T001",new(),default)).Items,x=>x.Outcome=="HTTP_FAILED"&&x.Reason=="HTTP_403");}
 [Fact]public async Task LoginStartsWithoutClaimedHumanThenRecordsOnlyIdentityOwnerActor()
 {await using var h=await Host.Start();h.Client.DefaultRequestHeaders.Remove("X-Monergy-Tenant");h.Client.DefaultRequestHeaders.Remove("X-Monergy-Session");var response=await h.Client.PostAsJsonAsync("/identity-api/local/v1/tenant-sessions",new{tenantId="T001",actorId="forged",password="never-record"});Assert.Equal(HttpStatusCode.OK,response.StatusCode);var p=await h.Writer.Repo.SearchAsync("T001",new(),default);Assert.Null(p.Items.Single(x=>x.Phase=="REQUESTED").ActorId);Assert.Equal("A900",p.Items.Single(x=>x.Phase=="RESULT").ActorId);Assert.DoesNotContain("forged",JsonSerializer.Serialize(p));}
 [Fact]public void ForeignResponseTenantCannotBeRecordedAsCurrentTenantSuccess()
 {var r=new ProductAuditRecord("T001","",Guid.NewGuid().ToString("N"),"RESULT",null,"UNAUTHENTICATED",null,"LOGIN","Product Gateway","login",null,null,DateTimeOffset.UtcNow,"HTTP_SUCCEEDED","HTTP_200");using var json=JsonDocument.Parse("{\"tenantId\":\"T002\",\"actorId\":\"foreign\"}");Assert.Throws<TenantBoundaryException>(()=>ProductAuditCapture.Metadata(r,json.RootElement));}
 private sealed class Writer:IProductAuditWriter
 {public InMemoryProductAuditLog Repo=new();public int Calls,FailAt=int.MaxValue;public bool LoseAfterAppend;public async Task AppendAsync(ProductAuditRecord r,CancellationToken ct){Calls++;if(Calls==FailAt&&!LoseAfterAppend)throw new TenantBoundaryException("AUDIT_OWNER_UNAVAILABLE",503);await Repo.AppendActivityAsync(r,ct);if(Calls==FailAt)throw new TenantBoundaryException("AUDIT_OWNER_UNAVAILABLE",503);}}
 private sealed class Identity:HttpMessageHandler
 {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=request.RequestUri!.AbsolutePath.EndsWith("validate",StringComparison.Ordinal)?JsonContent.Create(new CustomerOwnerSession("T001","A900","session",1,DateTimeOffset.UtcNow.AddMinutes(5))):JsonContent.Create(new CustomerOwnerWorkspace("T001","C001","Customer",null,"A900",1,true))});}
 private sealed class Host(WebApplication app,HttpClient client,Writer writer):IAsyncDisposable
 {public HttpClient Client=>client;public Writer Writer=>writer;public int Executed;public int Status=200;
 public static async Task<Host> Start(){var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{{"Monergy:ExecutionZone","CI_EPHEMERAL"},{"Monergy:AccessIntegration:Enabled","true"},{"Monergy:AccessIntegration:CustomerIdentityUrl","http://127.0.0.1:5101/"},{"Monergy:AccessIntegration:AccessManagementUrl","http://127.0.0.1:5088/"},{"Monergy:AccessIntegration:CustomerContextToken",new string('a',32)},{"Monergy:AccessIntegration:CustomerIdentityToken",new string('b',32)},{"Monergy:AccessIntegration:MembershipToken",new string('c',32)},{"Monergy:ProductAudit:Enabled","true"}});var writer=new Writer();builder.Services.AddSingleton<IProductAuditWriter>(writer);builder.Services.AddSingleton(new CustomerOwnerClient(builder.Configuration,new HttpClient(new Identity())));var app=builder.Build();Host? h=null;app.Use(async(http,next)=>{try{await next(http);}catch(TenantBoundaryException e){http.Response.StatusCode=e.Status;await http.Response.WriteAsJsonAsync(new{error=e.Code});}});app.UseProductAudit();app.Run(async http=>{h!.Executed++;http.Response.StatusCode=h.Status;await http.Response.WriteAsJsonAsync(http.Request.Path.StartsWithSegments("/identity-api")?(object)new{tenantId="T001",actorId="A900"}:new{documentId="document-one",customerId="C001"});});await app.StartAsync();var client=new HttpClient{BaseAddress=new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};client.DefaultRequestHeaders.Add("X-Monergy-Tenant","T001");client.DefaultRequestHeaders.Add("X-Monergy-Session","session");h=new(app,client,writer);return h;}
 public async ValueTask DisposeAsync(){client.Dispose();await app.DisposeAsync();}}
}
