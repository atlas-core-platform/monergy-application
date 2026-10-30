using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Monergy.UatGateway;
using Xunit;
namespace Monergy.FinancialProfile.Tests;

public sealed class CustomerFinancialGoalTests
{
    private const string Customer=FinancialProfileTestContext.CustomerId;
    private static CreateFinancialGoal Input()=>new(new string('a',32),new("Education fund",100000m,"INR",new(2030,6,30)));
    private static FinancialGoalsApplication Owner(InMemoryFinancialGoals? repository=null)=>new(repository??new(),TimeProvider.System);
    private static ReviseFinancialGoal Change(CustomerFinancialGoal goal)=>new(Guid.NewGuid().ToString("N"),goal.Revision,goal.Goal with {TargetAmount=125000m,TargetDate=new(2031,1,1)});
    private static ReportFinancialGoalLifecycle Lifecycle(CustomerFinancialGoal goal,string status="PAUSED")=>new(Guid.NewGuid().ToString("N"),goal.Revision,status,DateOnly.FromDateTime(DateTime.UtcNow));
    [Theory,InlineData("operation"),InlineData("uppercase"),InlineData("name"),InlineData("long"),InlineData("control"),InlineData("zero"),InlineData("negative"),InlineData("precision"),InlineData("large"),InlineData("currency"),InlineData("lower-currency"),InlineData("date-early"),InlineData("date-late")]
    public async Task InvalidExplicitTermsNeverWrite(string field)
    {
        var input=Input();var g=input.Goal;
        input=field switch {"operation"=>input with {OperationId="bad"},"uppercase"=>input with {OperationId=new('A',32)},
            "name"=>input with {Goal=g with {Name=" "}},"long"=>input with {Goal=g with {Name=new('x',121)}},"control"=>input with {Goal=g with {Name="a\nb"}},
            "zero"=>input with {Goal=g with {TargetAmount=0}},"negative"=>input with {Goal=g with {TargetAmount=-1}},"precision"=>input with {Goal=g with {TargetAmount=1.001m}},"large"=>input with {Goal=g with {TargetAmount=1000000000000m}},
            "currency"=>input with {Goal=g with {Currency="RUPEE"}},"lower-currency"=>input with {Goal=g with {Currency="inr"}},"date-early"=>input with {Goal=g with {TargetDate=new(1899,12,31)}},_=>input with {Goal=g with {TargetDate=new(2200,1,1)}}};
        var app=Owner();Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input,default))).Status);
        Assert.Empty((await app.ListAsync("T001",Customer,default)).Items);Assert.False(FinancialGoalsApplication.Valid(null));
    }
    [Fact]
    public async Task CreationReplayChangesAndExactHistoryKeepCustomerTargetsSeparateFromFinancialFacts()
    {
        var app=Owner();var input=Input();input=input with {Goal=input.Goal with {Name=" Education fund "}};
        var first=await app.CreateAsync("T001",Customer,"actor-001",input,default);Assert.Equal("Education fund",first.Goal.Name);Assert.Equal("CUSTOMER_DEFINED",first.DeclarationStatus);
        var change=Change(first);var second=await app.ReviseAsync("T001",Customer,"actor-001",first.FinancialGoalId,change,default);
        var third=await app.ReviseAsync("T001",Customer,"actor-001",first.FinancialGoalId,Change(second) with {Goal=second.Goal with {Currency="USD",TargetAmount=3000}},default);
        Assert.Equal(3,third.Revision);Assert.Equal(third,Assert.Single((await app.ListAsync("T001",Customer,default)).Items));
        Assert.Equal(first,await app.CreateAsync("T001",Customer,"actor-001",input with {Goal=first.Goal},default));
        Assert.Equal(second,await app.ReviseAsync("T001",Customer,"actor-001",first.FinancialGoalId,change,default));
        Assert.Equal(first,await app.ReadAsync("T001",Customer,first.FinancialGoalId,default,1));
        var history=await app.HistoryAsync("T001",Customer,first.FinancialGoalId,null,default);Assert.Equal([third,second,first],history.Items);Assert.False(history.HasMore);Assert.Null(history.NextBeforeRevision);
        Assert.All(history.Items,x=>Assert.Equal("CUSTOMER_DEFINED",x.DeclarationStatus));
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input with {Goal=input.Goal with {TargetAmount=2}},default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.FinancialGoalId,change with {Goal=change.Goal with {TargetAmount=2}},default))).Status);
    }
    [Fact]
    public async Task ConcurrentEditsCannotOverwriteAndOperationsCannotCrossCreationAndRevision()
    {
        var app=Owner();var first=await app.CreateAsync("T001",Customer,"actor-001",Input(),default);
        async Task<bool> Attempt(decimal amount) {try {await app.ReviseAsync("T001",Customer,"actor-001",first.FinancialGoalId,Change(first) with {Goal=first.Goal with {TargetAmount=amount}},default);return true;}catch(TenantBoundaryException e) when(e.Status==409){return false;}}
        Assert.Equal(1,(await Task.WhenAll(Task.Run(()=>Attempt(2)),Task.Run(()=>Attempt(3)))).Count(x=>x));
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.FinancialGoalId,Change(first) with {OperationId=first.OperationId},default))).Status);
        var second=(await app.ReadAsync("T001",Customer,first.FinancialGoalId,default))!;
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",Input() with {OperationId=second.OperationId},default))).Status);
    }
    [Fact]
    public async Task CustomerReportedLifecycleIsVersionedIdempotentAndPreservedByGoalCorrections()
    {
        var app=Owner();var first=await app.CreateAsync("T001",Customer,"actor-001",Input(),default);
        Assert.Equal("IN_PROGRESS",first.LifecycleStatus);Assert.Equal("CUSTOMER_REPORTED",first.LifecycleBasis);Assert.NotNull(first.LifecycleAsOfDate);
        var pause=Lifecycle(first);var paused=await app.ReportLifecycleAsync("T001",Customer,"actor-001",first.FinancialGoalId,pause,default);
        Assert.Equal("PAUSED",paused.LifecycleStatus);Assert.Equal(pause.AsOfDate,paused.LifecycleAsOfDate);Assert.Equal(2,paused.Revision);
        Assert.Equal(paused,await app.ReportLifecycleAsync("T001",Customer,"actor-001",first.FinancialGoalId,pause,default));
        var achieved=await app.ReportLifecycleAsync("T001",Customer,"actor-001",first.FinancialGoalId,Lifecycle(paused,"REPORTED_ACHIEVED"),default);
        Assert.Equal("REPORTED_ACHIEVED",achieved.LifecycleStatus);
        var corrected=await app.ReviseAsync("T001",Customer,"actor-001",first.FinancialGoalId,Change(achieved),default);
        Assert.Equal("REPORTED_ACHIEVED",corrected.LifecycleStatus);Assert.Equal(achieved.LifecycleAsOfDate,corrected.LifecycleAsOfDate);
        var reopened=await app.ReportLifecycleAsync("T001",Customer,"actor-001",first.FinancialGoalId,Lifecycle(corrected,"IN_PROGRESS"),default);
        Assert.Equal(["IN_PROGRESS","REPORTED_ACHIEVED","REPORTED_ACHIEVED","PAUSED","IN_PROGRESS"],(await app.HistoryAsync("T001",Customer,first.FinancialGoalId,null,default)).Items.Select(x=>x.LifecycleStatus));
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReportLifecycleAsync("T001",Customer,"actor-001",first.FinancialGoalId,Lifecycle(first,"STOPPED"),default))).Status);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReportLifecycleAsync("T001",Customer,"actor-001",first.FinancialGoalId,Lifecycle(reopened,"IN_PROGRESS"),default))).Status);
    }
    [Theory,InlineData("status"),InlineData("operation"),InlineData("date"),InlineData("same"),InlineData("stale-date")]
    public async Task InvalidLifecycleReportsNeverMutateCurrentGoal(string kind)
    {
        var app=Owner();var first=await app.CreateAsync("T001",Customer,"actor-001",Input(),default);var input=Lifecycle(first);
        input=kind switch {"status"=>input with{Status="ACHIEVED"},"operation"=>input with{OperationId="bad"},"date"=>input with{AsOfDate=new(2200,1,1)},"same"=>input with{Status="IN_PROGRESS"},_=>input with{AsOfDate=new(1900,1,1)}};
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReportLifecycleAsync("T001",Customer,"actor-001",first.FinancialGoalId,input,default))).Status);
        Assert.Equal(first,await app.ReadAsync("T001",Customer,first.FinancialGoalId,default));
    }
    [Fact]
    public void HistoricalGoalPayloadsRestoreAsCustomerReportedInProgressWithoutInventingAnEffectiveDate()
    {
        var value=new CustomerFinancialGoal("goal-legacy",new string('a',32),Customer,Input().Goal,"actor-001",DateTimeOffset.UtcNow,"CUSTOMER_DEFINED");
        var json=JsonNode.Parse(JsonSerializer.Serialize(value,ContractJson.Options))!.AsObject();json.Remove("lifecycleStatus");json.Remove("lifecycleAsOfDate");json.Remove("lifecycleBasis");
        var restored=json.Deserialize<CustomerFinancialGoal>(ContractJson.Options);Assert.NotNull(restored);Assert.Equal("IN_PROGRESS",restored.LifecycleStatus);Assert.Null(restored.LifecycleAsOfDate);Assert.Equal("CUSTOMER_REPORTED",restored.LifecycleBasis);
    }
    [Fact]
    public async Task TenantCustomerActorAndListBoundsRemainExplicit()
    {
        var app=Owner();var first=await app.CreateAsync("T001",Customer,"actor-001",Input(),default);
        Assert.NotEqual(first.FinancialGoalId,(await app.CreateAsync("T001",Customer,"actor-002",Input(),default)).FinancialGoalId);
        Assert.Empty((await app.ListAsync("T002",Customer,default)).Items);Assert.Empty((await app.ListAsync("T001","other",default)).Items);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReadAsync("T002",Customer,first.FinancialGoalId,default))).Status);
        for(var i=0;i<199;i++) await app.CreateAsync("T001",Customer,"actor-001",Input() with {OperationId=Guid.NewGuid().ToString("N")},default);
        var list=await app.ListAsync("T001",Customer,default);Assert.Equal(200,list.Items.Count);Assert.True(list.HasMore);
    }
    [Fact]
    public async Task HistoryIsContiguousAndBoundedWithoutInferringAchievementFromPastDates()
    {
        var app=Owner();var first=await app.CreateAsync("T001",Customer,"actor-001",Input() with {Goal=Input().Goal with {TargetDate=new(1900,1,1),TargetAmount=999999999999.99m}},default);var current=first;
        for(var i=0;i<53;i++)current=await app.ReviseAsync("T001",Customer,"actor-001",first.FinancialGoalId,Change(current),default);
        var page=await app.HistoryAsync("T001",Customer,first.FinancialGoalId,null,default);Assert.Equal(50,page.Items.Count);Assert.True(page.HasMore);Assert.Equal(5,page.NextBeforeRevision);
        var tail=await app.HistoryAsync("T001",Customer,first.FinancialGoalId,page.NextBeforeRevision,default);Assert.Equal([4,3,2,1],tail.Items.Select(x=>x.Revision));Assert.False(tail.HasMore);Assert.Equal("CUSTOMER_DEFINED",first.DeclarationStatus);
    }
    [Fact]
    public async Task RealOwnerAndProductRoutesSaveReadChangeReplayAndInspectVersionsWithoutDocumentAccess()
    {
        await using var h=await Harness.Create();using var client=h.Client();h.Authority.Failure="evidence";
        var response=await client.PostAsJsonAsync("/product-api/v1/financial-goals",Input(),ContractJson.Options);Assert.True(response.StatusCode==HttpStatusCode.OK,await response.Content.ReadAsStringAsync());Assert.Equal("no-store",response.Headers.CacheControl?.ToString());
        var first=(await response.Content.ReadFromJsonAsync<CustomerFinancialGoal>(ContractJson.Options))!;var path="/product-api/v1/financial-goals/"+first.FinancialGoalId;var change=Change(first);
        var second=await (await client.PostAsJsonAsync(path+"/corrections",change,ContractJson.Options)).Content.ReadFromJsonAsync<CustomerFinancialGoal>(ContractJson.Options);Assert.NotNull(second);Assert.Equal(2,second.Revision);
        Assert.Equal(first,await client.GetFromJsonAsync<CustomerFinancialGoal>(path+"?revision=1",ContractJson.Options));
        Assert.Equal(second,await (await client.PostAsJsonAsync(path+"/corrections",change,ContractJson.Options)).Content.ReadFromJsonAsync<CustomerFinancialGoal>(ContractJson.Options));
        Assert.Equal([second,first],(await client.GetFromJsonAsync<FinancialGoalHistory>(path+"/history",ContractJson.Options))!.Items);
        Assert.Equal(first,await (await client.PostAsJsonAsync("/product-api/v1/financial-goals",Input(),ContractJson.Options)).Content.ReadFromJsonAsync<CustomerFinancialGoal>(ContractJson.Options));
    }
    [Fact]
    public async Task ProductLifecycleRouteBindsCurrentCustomerActorTermsAndHistory()
    {
        await using var h=await Harness.Create();using var client=h.Client();var first=(await (await client.PostAsJsonAsync("/product-api/v1/financial-goals",Input(),ContractJson.Options)).Content.ReadFromJsonAsync<CustomerFinancialGoal>(ContractJson.Options))!;
        var input=Lifecycle(first);var path="/product-api/v1/financial-goals/"+first.FinancialGoalId;
        var paused=(await (await client.PostAsJsonAsync(path+"/lifecycle",input,ContractJson.Options)).Content.ReadFromJsonAsync<CustomerFinancialGoal>(ContractJson.Options))!;
        Assert.Equal("PAUSED",paused.LifecycleStatus);Assert.Equal(input.AsOfDate,paused.LifecycleAsOfDate);Assert.Equal("CUSTOMER_REPORTED",paused.LifecycleBasis);Assert.Equal(first.Goal,paused.Goal);
        Assert.Equal(paused,await (await client.PostAsJsonAsync(path+"/lifecycle",input,ContractJson.Options)).Content.ReadFromJsonAsync<CustomerFinancialGoal>(ContractJson.Options));
        Assert.Equal([paused,first],(await client.GetFromJsonAsync<FinancialGoalHistory>(path+"/history",ContractJson.Options))!.Items);
        h.Authority.Failure="update";Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path+"/lifecycle",Lifecycle(paused,"STOPPED"),ContractJson.Options)).StatusCode);
    }
    [Theory,InlineData("deny",403),InlineData("advisor",403),InlineData("unavailable",503),InlineData("update",403),InlineData("read",403),InlineData("missing",401),InlineData("tenant",401)]
    public async Task CurrentSelfAuthorityIsRequiredBeforeWrite(string failure,int status)
    {
        await using var h=await Harness.Create();h.Authority.Failure=failure;using var client=h.Client(failure=="missing"?null:failure=="tenant"?"T002":"T001");
        Assert.Equal(status,(int)(await client.PostAsJsonAsync("/product-api/v1/financial-goals",Input(),ContractJson.Options)).StatusCode);Assert.Empty((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    [Fact]
    public async Task CurrentReadOnlyAccessCanInspectButCannotEditAndBindingRejectsForeignTenant()
    {
        await using var h=await Harness.Create();using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/financial-goals",Input(),ContractJson.Options);var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
        h.Authority.Failure="update";var path="/product-api/v1/financial-goals/"+first.FinancialGoalId;Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(path+"/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path+"/corrections",Change(first),ContractJson.Options)).StatusCode);
        h.Authority.Failure="";h.Authority.AllowForeignTenant=true;using var foreign=h.OwnerClient("T002");Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync("/local/v1/customers/"+Customer+"/financial-goals/"+first.FinancialGoalId)).StatusCode);
    }
    [Theory,InlineData(true,"unknown"),InlineData(false,"unknown"),InlineData(true,"nested"),InlineData(false,"nested"),InlineData(true,"duplicate"),InlineData(false,"duplicate"),InlineData(true,"missing"),InlineData(false,"missing"),InlineData(true,"null"),InlineData(false,"null"),InlineData(true,"date"),InlineData(false,"date"),InlineData(true,"large"),InlineData(false,"large")]
    public async Task StrictIngressRejectsAmbiguousBodiesWithoutMutation(bool product,string kind)
    {
        await using var h=await Harness.Create();using var client=product?h.Client():h.OwnerClient();var body=JsonNode.Parse(JsonSerializer.Serialize(Input(),ContractJson.Options))!.AsObject();
        switch(kind){case "unknown":body["customerId"]="foreign";break;case "nested":body["goal"]!["sourceFinancialFactId"]="ff";break;case "missing":body.Remove("operationId");break;case "null":body["goal"]=null;break;case "date":body["goal"]!["targetDate"]="bad";break;case "large":body["goal"]!["name"]=new string('x',40000);break;}
        var json=body.ToJsonString();if(kind=="duplicate")json=json.Insert(1,"\"OPERATIONID\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",");
        var response=await client.PostAsync(product?"/product-api/v1/financial-goals":"/local/v1/customers/"+Customer+"/financial-goals",new StringContent(json,Encoding.UTF8,"application/json"));Assert.Equal(kind=="large"?413:400,(int)response.StatusCode);Assert.Empty((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    [Theory,InlineData("amount"),InlineData("currency"),InlineData("date"),InlineData("actor"),InlineData("customer"),InlineData("operation"),InlineData("status"),InlineData("revision"),InlineData("null"),InlineData("readback")]
    public async Task ForgedCreationReceiptOrFreshReadbackCannotBeConfirmed(string kind)
    {
        await using var h=await Harness.Create("create-"+kind);using var client=h.Client();Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.PostAsJsonAsync("/product-api/v1/financial-goals",Input(),ContractJson.Options)).StatusCode);Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    [Theory,InlineData("amount"),InlineData("currency"),InlineData("date"),InlineData("actor"),InlineData("customer"),InlineData("operation"),InlineData("status"),InlineData("revision"),InlineData("null"),InlineData("readback")]
    public async Task ForgedCorrectionReceiptOrFreshReadbackCannotBeConfirmed(string kind)
    {
        await using var h=await Harness.Create("correction-"+kind);using var client=h.Client();Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/product-api/v1/financial-goals",Input(),ContractJson.Options)).StatusCode);var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.PostAsJsonAsync("/product-api/v1/financial-goals/"+first.FinancialGoalId+"/corrections",Change(first),ContractJson.Options)).StatusCode);Assert.Equal(2,(await h.Records.ReadAsync("T001",Customer,first.FinancialGoalId,default))!.Revision);
    }
    [Theory,InlineData("null"),InlineData("duplicate"),InlineData("customer"),InlineData("hasMore"),InlineData("large")]
    public async Task ForgedListBoundariesFailClosed(string kind)
    {
        await using var h=await Harness.Create("list-"+kind);using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/financial-goals",Input(),ContractJson.Options);Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.GetAsync("/product-api/v1/financial-goals")).StatusCode);
    }
    [Theory,InlineData("null"),InlineData("customer"),InlineData("status"),InlineData("gap"),InlineData("empty"),InlineData("cursor"),InlineData("tail")]
    public async Task ForgedHistoryCannotSubstituteCustomerOrVersionWindow(string kind)
    {
        await using var h=await Harness.Create("history-"+kind);using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/financial-goals",Input(),ContractJson.Options);var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);var path="/product-api/v1/financial-goals/"+first.FinancialGoalId;
        await client.PostAsJsonAsync(path+"/corrections",Change(first),ContractJson.Options);Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.GetAsync(path+"/history")).StatusCode);
    }
    [Fact]
    public async Task LostConfirmationCanReplayTheExactSavedVersionAfterLaterChanges()
    {
        await using var h=await Harness.Create();using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/financial-goals",Input(),ContractJson.Options);var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);var path="/product-api/v1/financial-goals/"+first.FinancialGoalId+"/corrections";var change=Change(first);
        h.Authority.ResetEvaluations();h.Authority.DenyAfterEvaluations=5;Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path,change,ContractJson.Options)).StatusCode);var second=(await h.Records.ReadAsync("T001",Customer,first.FinancialGoalId,default))!;Assert.Equal(2,second.Revision);
        h.Authority.DenyAfterEvaluations=0;await client.PostAsJsonAsync(path,Change(second),ContractJson.Options);Assert.Equal(second,await (await client.PostAsJsonAsync(path,change,ContractJson.Options)).Content.ReadFromJsonAsync<CustomerFinancialGoal>(ContractJson.Options));Assert.Equal(3,(await h.Records.ReadAsync("T001",Customer,first.FinancialGoalId,default))!.Revision);
    }
    private sealed class TamperHandler(string scenario):DelegatingHandler(new SocketsHttpHandler {AllowAutoRedirect=false,UseProxy=false})
    {
        private bool corrected;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var response=await base.SendAsync(request,ct);if(!response.IsSuccessStatusCode)return response;
            var path=request.RequestUri!.AbsolutePath;var kind=scenario[(scenario.IndexOf('-')+1)..];
            var json=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
            if(scenario.StartsWith("list-",StringComparison.Ordinal))
            {
                if(request.Method!=HttpMethod.Get||!path.EndsWith("/financial-goals",StringComparison.Ordinal))return response;var items=json["items"]!.AsArray();
                switch(kind){case "null":json["items"]=null;break;case "duplicate":items.Add(items[0]!.DeepClone());break;case "customer":items[0]!["customerId"]="foreign";break;case "hasMore":json["hasMore"]=true;break;case "large":while(items.Count<201)items.Add(items[0]!.DeepClone());break;}
            }
            else if(scenario.StartsWith("history-",StringComparison.Ordinal))
            {
                if(!path.EndsWith("/history",StringComparison.Ordinal))return response;var items=json["items"]!.AsArray();
                switch(kind){case "null":json["items"]=null;break;case "customer":items[0]!["customerId"]="foreign";break;case "status":items[0]!["declarationStatus"]="ACHIEVED";break;case "gap":items[0]!["revision"]=99;break;case "empty":json["items"]=new JsonArray();break;case "cursor":json["nextBeforeRevision"]=2;break;case "tail":items.RemoveAt(items.Count-1);break;}
            }
            else
            {
                if(scenario.StartsWith("correction-",StringComparison.Ordinal))
                {if(request.Method==HttpMethod.Post&&path.EndsWith("/corrections",StringComparison.Ordinal))corrected=true;else if(!corrected||kind!="readback")return response;}
                if((kind=="readback")!=(request.Method==HttpMethod.Get))return response;
                switch(kind){case "currency":json["goal"]!["currency"]="USD";break;case "date":json["goal"]!["targetDate"]="2035-01-01";break;case "actor":json["recordedBy"]="foreign";break;case "customer":json["customerId"]="foreign";break;case "operation":json["operationId"]=new string('b',32);break;case "status":json["declarationStatus"]="ACHIEVED";break;case "revision":json["revision"]=99;break;case "null":json["goal"]=null;break;default:json["goal"]!["targetAmount"]=1;break;}
            }
            response.Content.Dispose();response.Content=new StringContent(json.ToJsonString(),Encoding.UTF8,"application/json");return response;
        }
    }
    private sealed class Authority : HttpMessageHandler
    {
        public string Failure { get; set; } = "";
        public int DenyAfterEvaluations { get; set; }
        public bool AllowForeignTenant { get; set; }
        private int evaluations;
        public void ResetEvaluations() => evaluations = 0;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Failure == "unavailable") return new(HttpStatusCode.ServiceUnavailable);
            if (request.RequestUri!.AbsolutePath == "/v1/authorization/customer-evaluations")
            {
                var input = await request.Content!.ReadFromJsonAsync<CustomerAccessRequest>(cancellationToken:ct);
                var tenant = request.Headers.GetValues("X-Monergy-Tenant").Single();
                var denied = DenyAfterEvaluations > 0 && ++evaluations > DenyAfterEvaluations || Failure == "deny" || !AllowForeignTenant && tenant != "T001" || input!.CustomerId != Customer ||
                    Failure == "update" && input.CapabilityId.EndsWith("update", StringComparison.Ordinal) ||
                    Failure == "evidence" && input.CapabilityId == "evidence.document.read" || Failure == "read" && input.CapabilityId == "financial-profile.profile.read";
                return Response(new CustomerAccessDecision("decision-001", denied ? "Deny" : "Allow", tenant, "actor-001", input!.CustomerId,
                    input.CapabilityId, input.Purpose, denied ? "Denied" : "Granted", Failure == "advisor" ? "Advisor" : "Self", 1,2,1,1,DateTimeOffset.UtcNow));
            }
            if (request.RequestUri.AbsolutePath == "/internal/v1/tenant-sessions/validate")
                return Response(new CustomerOwnerSession("T001","actor-001","session-001",2,DateTimeOffset.UtcNow.AddHours(1)));
            if (request.RequestUri.AbsolutePath == "/internal/v1/customer-owner/actor-001")
                return Response(new CustomerOwnerWorkspace("T001",Customer,"Customer",null,"actor-001",1,true));
            throw new InvalidOperationException("Unexpected authority route.");
        }
        private static HttpResponseMessage Response<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
    private sealed class Harness(WebApplication owner, WebApplication gateway, Authority authority, InMemoryFinancialGoals records) : IAsyncDisposable
    {
        public Authority Authority => authority; public InMemoryFinancialGoals Records => records;
        public static async Task<Harness> Create(string? tamper=null)
        {
            var records = new InMemoryFinancialGoals(); var authority = new Authority();
            var values = new Dictionary<string,string?> { ["Monergy:ExecutionZone"]="CI_EPHEMERAL", ["Monergy:AccessIntegration:Enabled"]="true", ["Monergy:Product:Enabled"]="true",
                ["Monergy:AccessIntegration:CustomerIdentityUrl"]="http://127.0.0.1:5001/", ["Monergy:AccessIntegration:AccessManagementUrl"]="http://127.0.0.1:5002/",
                ["Monergy:AccessIntegration:CustomerContextToken"]=new('a',32), ["Monergy:AccessIntegration:CustomerIdentityToken"]=new('b',32), ["Monergy:AccessIntegration:MembershipToken"]=new('c',32) };
            var ob = WebApplication.CreateSlimBuilder(); ob.WebHost.UseUrls("http://127.0.0.1:0"); ob.Configuration.AddInMemoryCollection(values);
            ob.Services.AddSingleton(new TenantAccessClient(ob.Configuration, new HttpClient(authority,false)));
            ob.Services.AddSingleton(new TenantBoundaryOptions { Service="financial-profile", CapabilityPrefix="financial-profile.", CustomerTenants=new Dictionary<string,string> { [Customer]="T001" }, ProbeUrl="http://127.0.0.1:5199/" });
            ob.Services.AddSingleton(new FinancialGoalsApplication(records,TimeProvider.System)); var owner=ob.Build(); owner.MapFinancialGoals(); await owner.StartAsync();
            values["Monergy:Product:FinancialProfileUrl"]=owner.Urls.Single()+"/";
            var gb=WebApplication.CreateSlimBuilder(); gb.WebHost.UseUrls("http://127.0.0.1:0"); gb.Configuration.AddInMemoryCollection(values);
            gb.Services.AddSingleton(new CustomerOwnerClient(gb.Configuration,new HttpClient(authority,false)));
            gb.Services.AddSingleton(new TenantAccessClient(gb.Configuration,new HttpClient(authority,false))); gb.AddProductFinancialGoals();
            if(tamper is not null) gb.Services.AddSingleton(new ProductFinancialGoalClient(gb.Configuration,new HttpClient(new TamperHandler(tamper))));
            var gateway=gb.Build(); gateway.MapProductFinancialGoals(); await gateway.StartAsync(); return new(owner,gateway,authority,records);
        }
        public HttpClient Client(string? tenant="T001") => ClientFor(gateway,tenant);
        public HttpClient OwnerClient(string tenant="T001") => ClientFor(owner,tenant);
        private static HttpClient ClientFor(WebApplication app,string? tenant)
        {
            var client=new HttpClient { BaseAddress=new Uri(app.Urls.Single()) };
            if(tenant is not null) { client.DefaultRequestHeaders.Add("X-Monergy-Tenant",tenant);client.DefaultRequestHeaders.Add("X-Monergy-Session","session-001"); }
            return client;
        }
        public async ValueTask DisposeAsync() { await gateway.DisposeAsync();await owner.DisposeAsync();authority.Dispose(); }
    }
}
