using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monergy.Platform;
using Monergy.Services.Audit;
using Monergy.Services.Audit.Application;
using Monergy.Services.Audit.Infrastructure;
using Xunit;
namespace Monergy.Vs02.Tests;
public sealed class ProductAuditLogTests
{
 [Theory]
 [InlineData("SELECT count(*) FROM ledger")]
 [InlineData("SELECT entry::text FROM ledger")]
 [InlineData("SELECT EXISTS(SELECT 1 FROM ledger")]
 public void PostgreSqlAuditSearchSeparatesLedgerProjectionAndFilter(string projection)
 {
  var method=typeof(PostgresTenantAccessAudit).GetMethod("SearchStatement",
   System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);
  Assert.NotNull(method);
  var sql=Assert.IsType<string>(method!.Invoke(null,[projection]));
  Assert.Contains(")\n" + projection + "\nWHERE",sql.Replace("\r\n","\n"));
 }
 private static ProductAuditRecord Row(string tenant="T001",string action="API_ACCESS",string? id=null)=>new(tenant,"",id??Guid.NewGuid().ToString("N"),"REQUESTED","A900","USER","C001",action,"Product Gateway","api",null,null,DateTimeOffset.UtcNow,"REQUESTED","GET");
 private static ProductAuditRecord Begin(ProductAuditRecord r)=>r with{EventId=r.RequestId+":REQUESTED"};
 [Fact]public async Task AppendIsImmutableExactReplayAndResultRemainsSeparateFromRequest()
 {var repo=new InMemoryProductAuditLog();var r=Begin(Row());Assert.Equal(r,await repo.AppendActivityAsync(r,default));Assert.Equal(r,await repo.AppendActivityAsync(r,default));await Assert.ThrowsAsync<TenantBoundaryException>(()=>repo.AppendActivityAsync(r with{Reason="POST"},default));Assert.Single((await repo.SearchAsync("T001",new(),default)).Items);var result=r with{EventId=r.RequestId+":RESULT",Phase="RESULT",Outcome="ACCEPTED",Reason="HTTP_202"};await repo.AppendActivityAsync(result,default);Assert.Equal(2,(await repo.SearchAsync("T001",new(),default)).Matching);}
 [Theory][InlineData("tenant")][InlineData("actor")][InlineData("customer")][InlineData("resource")][InlineData("action")][InlineData("time")]
 public async Task ResultCannotChangeTheKnownRequestIdentity(string mode)
 {var repo=new InMemoryProductAuditLog();var r=Begin(Row() with{ResourceId="resource-one"});await repo.AppendActivityAsync(r,default);var result=r with{EventId=r.RequestId+":RESULT",Phase="RESULT",Outcome="HTTP_SUCCEEDED"};result=mode switch{"tenant"=>result with{TenantId="T002"},"actor"=>result with{ActorId="A901"},"customer"=>result with{CustomerId="C002"},"resource"=>result with{ResourceId="other"},"action"=>result with{Action="LOGIN"},_=>result with{OccurredAt=r.OccurredAt.AddSeconds(-1)}};await Assert.ThrowsAsync<TenantBoundaryException>(()=>repo.AppendActivityAsync(result,default));}
 [Fact]public async Task TenantScopeSearchAndInclusiveDateFiltersNeverInventLegacyContext()
 {var repo=new InMemoryProductAuditLog();var r=Begin(Row() with{OccurredAt=ProductAuditLog.Parse("2026-10-21T01:00:00.000Z"),ResourceId="literal_100"});await repo.AppendActivityAsync(r,default);await repo.AppendActivityAsync(Begin(Row("T002")),default);var p=await repo.SearchAsync("T001",new(Search:"_100",ActorId:"A900",CustomerId:"C001",From:"2026-10-21T01:00:00.000Z",To:"2026-10-21T01:00:00.000Z"),default);Assert.Single(p.Items);Assert.Null(await repo.ReadActivityAsync("T002",p.Items[0].EvidenceId,default));Assert.Empty((await repo.SearchAsync("T001",new(Search:"%"),default)).Items);}
 [Fact]public async Task CursorIsBoundToTenantAndMatchingFilterAndNoRowIsRepeated()
 {var repo=new InMemoryProductAuditLog();for(var i=0;i<105;i++)await repo.AppendActivityAsync(Begin(Row()),default);var first=await repo.SearchAsync("T001",new(),default);var second=await repo.SearchAsync("T001",new(After:first.NextCursor),default);Assert.Equal(50,first.Items.Count);Assert.Equal(50,second.Items.Count);Assert.Empty(first.Items.Select(x=>x.EvidenceId).Intersect(second.Items.Select(x=>x.EvidenceId)));Assert.Equal(105,first.Matching);await Assert.ThrowsAsync<TenantBoundaryException>(()=>repo.SearchAsync("T002",new(After:first.NextCursor),default));await Assert.ThrowsAsync<TenantBoundaryException>(()=>repo.SearchAsync("T001",new(Action:"LOGIN",After:first.NextCursor),default));}
 [Theory][InlineData("PII")][InlineData("FORCED_SUCCESS")][InlineData("")]
 public void UnknownActionsCannotBecomeLedgerEvidence(string action)=>Assert.Throws<TenantBoundaryException>(()=>ProductAuditProtocol.Validate(Begin(Row(action:action))));
 [Fact]public void ArbitraryIpDeviceOrRawReasonCannotBeRecorded()
 {var r=Begin(Row());Assert.Throws<TenantBoundaryException>(()=>ProductAuditProtocol.Validate(r with{IpAddress="1.2.3.4"}));Assert.Throws<TenantBoundaryException>(()=>ProductAuditProtocol.Validate(r with{Device="browser fingerprint"}));Assert.Throws<TenantBoundaryException>(()=>ProductAuditProtocol.Validate(r with{Reason="token=secret"}));}
 [Theory][InlineData("invalid")][InlineData("2026-10-21")][InlineData("2026-10-21T00:00:00Z")]
 public void DatesRequireExactBoundedUtcWireFormat(string date)=>Assert.Throws<TenantBoundaryException>(()=>ProductAuditLog.Query(new(From:date)));
 [Fact]public async Task ActualOwnerHttpDetailsAndExportAreMetadataOnlyAndCurrentAuthorityChecked()
 {await using var h=await Host.Start();var list=await h.Client.GetFromJsonAsync<AuditLogPage>("/local/v1/administration/audit-log");Assert.Single(list!.Items);var id=list.Items[0].EvidenceId;var detail=await h.Client.GetFromJsonAsync<AuditLogEntry>("/local/v1/administration/audit-log/"+id);Assert.Equal(id,detail!.EvidenceId);var export=await h.Client.GetAsync("/local/v1/administration/audit-log/export?action=API_ACCESS");Assert.Equal(HttpStatusCode.OK,export.StatusCode);Assert.Contains("monergy-audit-page.json",export.Content.Headers.ContentDisposition!.FileName);var body=await export.Content.ReadAsStringAsync();Assert.Contains("Current filtered page only",body);Assert.DoesNotContain("authenticationContext",body);Assert.DoesNotContain("accessToken",body);Assert.True(h.Authority.Calls>=6);}
 [Theory][InlineData("?extra=true")][InlineData("?action=LOGIN&action=API_ACCESS")][InlineData("?actorId=")][InlineData("?after=foreign")][InlineData("?from=2026-10-22T00:00:00.000Z&to=2026-10-21T00:00:00.000Z")]
 public async Task HttpIngressRejectsUnknownDuplicateEmptyAndForeignCursor(string query)
 {await using var h=await Host.Start();Assert.Equal(HttpStatusCode.BadRequest,(await h.Client.GetAsync("/local/v1/administration/audit-log"+query)).StatusCode);}
 [Theory][InlineData(401)][InlineData(403)][InlineData(503)]
 public async Task CurrentOwnerDenialOrUnavailabilityDoesNotBecomeAnEmptySuccess(int status)
 {await using var h=await Host.Start();h.Authority.Status=status;Assert.Equal((HttpStatusCode)status,(await h.Client.GetAsync("/local/v1/administration/audit-log")).StatusCode);}
 [Fact]public async Task RevocationAndAuthorityChangesAfterReadFailClosed()
 {await using var h=await Host.Start();h.Authority.DenyAfter=1;Assert.Equal(HttpStatusCode.Forbidden,(await h.Client.GetAsync("/local/v1/administration/audit-log/export")).StatusCode);}
 [Fact]public async Task SameOriginPolicyAndForeignDetailCannotLeakLedgerRows()
 {await using var h=await Host.Start();h.Client.DefaultRequestHeaders.Add("Origin","https://foreign.invalid");Assert.Equal(HttpStatusCode.Forbidden,(await h.Client.GetAsync("/local/v1/administration/audit-log")).StatusCode);h.Client.DefaultRequestHeaders.Remove("Origin");Assert.Equal(HttpStatusCode.NotFound,(await h.Client.GetAsync("/local/v1/administration/audit-log/foreign")).StatusCode);}
 private sealed class Authority:HttpMessageHandler
 {public int Status=200,Calls,DenyAfter=int.MaxValue;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;return Task.FromResult(new HttpResponseMessage((HttpStatusCode)(Calls>DenyAfter?403:Status)){Content=JsonContent.Create(new TenantAdminContext("T001","A900",1,"TenantAdmin"))});}}
 private sealed class Host(WebApplication app,HttpClient client,Authority authority):IAsyncDisposable
 {public HttpClient Client=>client;public Authority Authority=>authority;public static async Task<Host> Start(){var repo=new InMemoryProductAuditLog();await repo.AppendActivityAsync(Begin(Row()),default);var authority=new Authority();var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Services.AddSingleton<IProductAuditLog>(repo);builder.Services.AddSingleton(new TenantAccessClient(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Monergy:ExecutionZone","CI_EPHEMERAL"}}).Build(),new HttpClient(authority)));var app=builder.Build();app.MapProductAuditLog();await app.StartAsync();var client=new HttpClient{BaseAddress=new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};client.DefaultRequestHeaders.Add("X-Monergy-Tenant","T001");client.DefaultRequestHeaders.Add("X-Monergy-Session","admin");return new(app,client,authority);}public async ValueTask DisposeAsync(){client.Dispose();await app.DisposeAsync();}}
}
