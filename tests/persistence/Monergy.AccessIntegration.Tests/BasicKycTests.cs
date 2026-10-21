using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity;
using Monergy.Services.CustomerIdentity.Application;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class BasicKycTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static readonly KycEvidence Evidence=new("version-1","IDENTITY","identity.pdf",new string('a',64));
    private static readonly KycPolicy Policy=new(new string('b',64),"Published manual review notice",[new("A300","Named reviewer")]);
    private static KycChange Submit=>new(Guid.NewGuid().ToString("N"),0,"SUBMIT","A300",Policy.Revision,true,"Please review",[Evidence]);
    private static KycCase First(KycChange? c=null)=>BasicKyc.Transition(null,"T001","C001","Customer","A100","A100",c??Submit,Policy,Now);
    [Theory]
    [InlineData("ACCEPT","MANUAL_REVIEW_ACCEPTED")]
    [InlineData("REQUEST_CHANGES","CHANGES_REQUESTED")]
    [InlineData("REJECT","REJECTED")]
    [InlineData("WITHDRAW","WITHDRAWN")]
    public void ReviewTransitionsRetainExactSubmissionAndCannotClaimProviderVerification(string action,string state)
    {var first=First();var c=new KycChange(Guid.NewGuid().ToString("N"),1,action,null,null,false,"Review outcome",[]);var next=BasicKyc.Transition(first,"T001","C001","Customer","A100",action=="WITHDRAW"?"A100":"A300",c,Policy,Now);Assert.Equal(state,next.State);Assert.Equal(first.Evidence,next.Evidence);Assert.Equal(first.Notice,next.Notice);Assert.Equal(2,next.Version);Assert.Equal("SUBMITTED",first.State);}
    [Theory]
    [InlineData("ACCEPT","A100")][InlineData("ACCEPT","A900")][InlineData("WITHDRAW","A300")]
    public void CustomerCannotApproveAndUnassignedAdministratorCannotReview(string action,string actor)
    {var c=new KycChange(Guid.NewGuid().ToString("N"),1,action,null,null,false,"Outcome",[]);Assert.Throws<TenantAccessException>(()=>BasicKyc.Transition(First(),"T001","C001","Customer","A100",actor,c,Policy,Now));}
    [Fact] public void StaleVersionAndChangedPolicyFailClosed()
    {var first=First();var c=new KycChange(Guid.NewGuid().ToString("N"),0,"ACCEPT",null,null,false,"Outcome",[]);Assert.Equal("KYC_VERSION_CONFLICT",Assert.Throws<TenantAccessException>(()=>BasicKyc.Transition(first,"T001","C001","Customer","A100","A300",c,Policy,Now)).Code);Assert.Throws<TenantAccessException>(()=>BasicKyc.Transition(first,"T001","C001","Customer","A100","A300",c with{ExpectedVersion=1},Policy with{Revision=new string('c',64)},Now));}
    [Fact] public void ExplicitConsentIdentityAndConfiguredDifferentReviewerRequired()
    {foreach(var c in new[]{Submit with{Consent=false},Submit with{Evidence=[]},Submit with{Evidence=[Evidence with{Purpose="ADDRESS"}]},Submit with{Evidence=[Evidence,Evidence]},Submit with{ReviewerId="A100"},Submit with{PolicyRevision=new string('d',64)}})Assert.Throws<TenantAccessException>(()=>First(c));}
    [Fact] public void CorrectionsCreateNewVersionWithoutRewritingPriorEvidence()
    {var first=First();var correction=new KycChange(Guid.NewGuid().ToString("N"),1,"REQUEST_CHANGES",null,null,false,"Please replace the unreadable image",[]);var changed=BasicKyc.Transition(first,"T001","C001","Customer","A100","A300",correction,Policy,Now);var resubmitted=BasicKyc.Transition(changed,"T001","C001","Customer","A100","A100",Submit with{ExpectedVersion=2,Evidence=[Evidence with{DocumentVersionId="version-2"}]},Policy,Now);Assert.Equal(3,resubmitted.Version);Assert.Equal("version-1",first.Evidence[0].DocumentVersionId);Assert.Equal("version-2",resubmitted.Evidence[0].DocumentVersionId);}
    [Fact] public void MissingConfigurationIsDisabledAndNoticeChangesItsRevision()
    {var config=new ConfigurationBuilder().AddInMemoryCollection().Build();var owner=new BasicKycPolicy(config);Assert.Null(owner.Read("T001"));config["Monergy:BasicKyc:Tenants:T001:Enabled"]="true";Assert.Throws<TenantAccessException>(()=>owner.Read("T001"));config["Monergy:BasicKyc:Tenants:T001:Notice"]=Policy.Notice;config["Monergy:BasicKyc:Tenants:T001:Reviewers:0:ActorId"]="A300";config["Monergy:BasicKyc:Tenants:T001:Reviewers:0:Name"]="Named reviewer";var initial=owner.Read("T001")!;config["Monergy:BasicKyc:Tenants:T001:Notice"]+=" Updated";Assert.NotEqual(initial.Revision,owner.Read("T001")!.Revision);Assert.Null(owner.Read("T002"));}
    [Fact] public async Task EvidenceClientBindsTenantCustomerVersionAndHashAndPropagatesSession()
    {var bytes=new byte[]{1,2,3};var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));var handler=new SourceHandler(new("T001","C001","version-1","identity.pdf",hash,"application/pdf",Convert.ToBase64String(bytes)));using var owner=new KycEvidenceClient(new ConfigurationBuilder().Build(),new HttpClient(handler));var s=new TenantSessionContext("T001","A300","review-session",3,Now.AddHours(1));Assert.Equal(hash,(await owner.ReadAsync(s,"C001","version-1",default)).Sha256);Assert.Equal("review-session",handler.Session);handler.Value=handler.Value with{CustomerId="C002"};await Assert.ThrowsAsync<TenantAccessException>(()=>owner.ReadAsync(s,"C001","version-1",default));}
    [Theory] [InlineData("T002","owner-session",403)] [InlineData("T001","wrong-session",401)] [InlineData("T001","owner-session",403)]
    public async Task ActualHttpReviewerContextRejectsForeignOrUnassignedIdentity(string tenant,string session,int status)
    {await using var f=await Host.Start();using var request=new HttpRequestMessage(HttpMethod.Get,"/local/v1/basic-kyc/reviewer-context");request.Headers.Add("X-Monergy-Tenant",tenant);request.Headers.Add("X-Monergy-Session",session);var response=await f.Client.SendAsync(request);Assert.Equal(status,(int)response.StatusCode);}
    [Fact] public async Task ActualHttpSelfReadSubmissionWithdrawalHistoryAndForgeryBoundaries()
    {await using var f=await Host.Start();f.Client.DefaultRequestHeaders.Add("X-Monergy-Tenant","T001");f.Client.DefaultRequestHeaders.Add("X-Monergy-Session","owner-session");var view=await f.Client.GetFromJsonAsync<JsonElement>("/local/v1/basic-kyc/me");Assert.Equal("C001",view.GetProperty("customerId").GetString());var revision=view.GetProperty("policy").GetProperty("revision").GetString();var change=Submit with{PolicyRevision=revision,Evidence=[Evidence with{Sha256=f.Hash}]};Assert.Equal(HttpStatusCode.OK,(await f.Client.PostAsJsonAsync("/local/v1/basic-kyc/me",change)).StatusCode);Assert.Equal("SUBMITTED",f.Repo.Rows.Last().State);Assert.Equal(HttpStatusCode.OK,(await f.Client.PostAsJsonAsync("/local/v1/basic-kyc/me",change)).StatusCode);Assert.Single(f.Repo.Rows);
      Assert.Equal(HttpStatusCode.Forbidden,(await f.Client.PostAsJsonAsync("/local/v1/basic-kyc/me",new KycChange(Guid.NewGuid().ToString("N"),1,"ACCEPT",null,null,false,"Forgery",[]))).StatusCode);
      var withdraw=new KycChange(Guid.NewGuid().ToString("N"),1,"WITHDRAW",null,null,false,"Withdraw review",[]);Assert.Equal(HttpStatusCode.OK,(await f.Client.PostAsJsonAsync("/local/v1/basic-kyc/me",withdraw)).StatusCode);Assert.Equal("WITHDRAWN",f.Repo.Rows.Last().State);Assert.Equal(2,(await f.Client.GetFromJsonAsync<JsonElement>("/local/v1/basic-kyc/me/history")).GetProperty("items").GetArrayLength());
      Assert.Equal(HttpStatusCode.BadRequest,(await f.Client.GetAsync("/local/v1/basic-kyc/me?customer=C002")).StatusCode);
      f.Client.DefaultRequestHeaders.Add("Origin","https://foreign.example");Assert.Equal(HttpStatusCode.Forbidden,(await f.Client.GetAsync("/local/v1/basic-kyc/me")).StatusCode);
    }
    [Fact] public async Task ActualHttpAssignedReviewerDecidesOnceAndReplaysExactReceipt()
    {
        await using var f=await Host.Start();f.Client.DefaultRequestHeaders.Add("X-Monergy-Tenant","T001");f.Client.DefaultRequestHeaders.Add("X-Monergy-Session","owner-session");
        var view=await f.Client.GetFromJsonAsync<JsonElement>("/local/v1/basic-kyc/me");var submit=Submit with {PolicyRevision=view.GetProperty("policy").GetProperty("revision").GetString(),Evidence=[Evidence with{Sha256=f.Hash}]};
        Assert.Equal(HttpStatusCode.OK,(await f.Client.PostAsJsonAsync("/local/v1/basic-kyc/me",submit)).StatusCode);
        f.Client.DefaultRequestHeaders.Remove("X-Monergy-Session");f.Client.DefaultRequestHeaders.Add("X-Monergy-Session","review-session");
        Assert.Equal(HttpStatusCode.OK,(await f.Client.GetAsync("/local/v1/basic-kyc/reviewer-context")).StatusCode);
        Assert.Equal(1,(await f.Client.GetFromJsonAsync<JsonElement>("/local/v1/basic-kyc/reviews")).GetProperty("items").GetArrayLength());
        var decision=new KycChange(Guid.NewGuid().ToString("N"),1,"ACCEPT",null,null,false,"Original evidence reviewed manually",[]);
        Assert.Equal(HttpStatusCode.OK,(await f.Client.GetAsync("/local/v1/basic-kyc/reviews/C001/evidence/version-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await f.Client.PostAsJsonAsync("/local/v1/basic-kyc/reviews/C001",decision)).StatusCode);Assert.Equal("MANUAL_REVIEW_ACCEPTED",f.Repo.Rows.Last().State);
        Assert.Equal(HttpStatusCode.OK,(await f.Client.PostAsJsonAsync("/local/v1/basic-kyc/reviews/C001",decision)).StatusCode);Assert.Equal(2,f.Repo.Rows.Count);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Client.PostAsJsonAsync("/local/v1/basic-kyc/reviews/C001",decision with{Reason="Changed payload"})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await f.Client.GetAsync("/local/v1/basic-kyc/reviews/C001/evidence/version-1")).StatusCode);
    }
    private sealed class SourceHandler(KycDocument value):HttpMessageHandler
    {public KycDocument Value=value;public string? Session;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Session=r.Headers.GetValues("X-Monergy-Session").Single();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(Value)});}}
    private sealed class Memory:IBasicKycRepository
    {public List<KycCase> Rows=[];private Dictionary<string,(string Input,KycCase Row)> Operations=[];
      public Task<KycCase?> ReadAsync(string t,string c,CancellationToken ct)=>Task.FromResult(Rows.LastOrDefault(x=>x.TenantId==t&&x.CustomerId==c));
      public Task<KycCase[]> HistoryAsync(string t,string c,long b,CancellationToken ct)=>Task.FromResult(Rows.Where(x=>x.TenantId==t&&x.CustomerId==c&&x.Version<b).OrderByDescending(x=>x.Version).Take(51).ToArray());
      public Task<KycCase[]> QueueAsync(string t,string a,string after,CancellationToken ct)=>Task.FromResult(Rows.GroupBy(x=>x.CustomerId).Select(x=>x.Last()).Where(x=>x.TenantId==t&&x.ReviewerId==a&&x.State=="SUBMITTED"&&string.CompareOrdinal(x.CustomerId,after)>0).Take(51).ToArray());
      public async Task<KycCase> ChangeAsync(string t,string a,string c,KycChange v,Func<KycCase?,KycCase> fn,CancellationToken ct){var input=JsonSerializer.Serialize(new{t,a,c,v});if(Operations.TryGetValue(v.OperationId,out var old)){if(old.Input!=input)throw new TenantAccessException("KYC_OPERATION_CONFLICT",409);return old.Row;}var row=fn(await ReadAsync(t,c,ct));Rows.Add(row);Operations[v.OperationId]=(input,row);return row;}}
    private sealed class Profiles:ISelfCustomerProfileRepository
    {public Task<SelfCustomerProfile?> ReadAsync(string t,string a,CancellationToken ct)=>Task.FromResult<SelfCustomerProfile?>(a=="A100"?new(t,a,"C001","Customer","customer@example.test",null,0,null,null):null);public Task<bool> OwnsAsync(string t,string a,string c,CancellationToken ct)=>Task.FromResult(t=="T001"&&a=="A100"&&c=="C001");public Task<SelfCustomerProfileReceipt> SaveAsync(string t,string a,string c,SelfCustomerProfileChange change,DateTimeOffset now,CancellationToken ct)=>throw new NotSupportedException();}
    private sealed class Membership:ITenantMembershipClient{public Task<TenantMembershipState> ReadAsync(string t,string a,CancellationToken ct)=>Task.FromResult(new TenantMembershipState(t,a,true,3,8));}
    private sealed class Sessions:ITenantSessionRepository
    {public Task<TenantSessionContext?> ReadCurrentAsync(string t,string s,CancellationToken ct)=>Task.FromResult<TenantSessionContext?>(s is "owner-session" or "review-session"?new(t,s=="owner-session"?"A100":"A300",s,3,Now.AddHours(1)):null);public Task RevokeAsync(string t,string s,CancellationToken ct)=>Task.CompletedTask;public Task<TenantSessionContext> CreateAsync(string t,string a,string s,long v,CancellationToken ct)=>throw new NotSupportedException();public Task<AccessManagementReceipt> ConsumeAsync(string t,string d,AccessManagementEvent e,CancellationToken ct)=>throw new NotSupportedException();public Task<bool> ReadyAsync(CancellationToken ct)=>Task.FromResult(true);}
    private sealed class Host: IAsyncDisposable
    {public required WebApplication App;public required HttpClient Client;public required Memory Repo;public required string Hash;
      public static async Task<Host> Start(){var b=WebApplication.CreateBuilder();b.WebHost.UseUrls("http://127.0.0.1:0");b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["Monergy:ExecutionZone"]="CI_EPHEMERAL",["Monergy:AccessIntegration:Enabled"]="true",["Monergy:AccessIntegration:Identities:0:TenantId"]="T001",["Monergy:AccessIntegration:Identities:0:ActorId"]="A100",["Monergy:AccessIntegration:Identities:0:Token"]=new string('a',64),["Monergy:BasicKyc:Tenants:T001:Enabled"]="true",["Monergy:BasicKyc:Tenants:T001:Notice"]=Policy.Notice,["Monergy:BasicKyc:Tenants:T001:Reviewers:0:ActorId"]="A300",["Monergy:BasicKyc:Tenants:T001:Reviewers:0:Name"]="Named reviewer"});var repo=new Memory();var profiles=new Profiles();var sessions=new TenantSessionAuthority(new Sessions(),new Membership(),new(b.Configuration));var bytes=new byte[]{1,2,3};var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));b.Services.AddSingleton(sessions);b.Services.AddSingleton<ISelfCustomerProfileRepository>(profiles);b.Services.AddSingleton(new SelfCustomerProfileApplication(profiles,sessions,TimeProvider.System));b.Services.AddSingleton<IBasicKycRepository>(repo);b.Services.AddSingleton(new BasicKycPolicy(b.Configuration));b.Services.AddSingleton(new KycEvidenceClient(b.Configuration,new HttpClient(new SourceHandler(new("T001","C001","version-1","identity.pdf",hash,"application/pdf",Convert.ToBase64String(bytes))))));var app=b.Build();app.MapBasicKyc();await app.StartAsync();return new(){App=app,Repo=repo,Hash=hash,Client=new HttpClient{BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())}};}
      public async ValueTask DisposeAsync(){Client.Dispose();await App.StopAsync();await App.DisposeAsync();}}
}
