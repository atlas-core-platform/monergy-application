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

public sealed partial class CustomerGoalContributionTests
{
    private const string Customer=FinancialProfileTestContext.CustomerId;
    private static readonly string GoalId="goal-"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("T001|"+Customer+"|actor-001|"+new string('c',32)))).ToLowerInvariant();
    private static CreateGoalContribution Input()=>new(new string('a',32),new(GoalId,1,1000m,new(2026,10,1)));
    private static async Task<CustomerFinancialGoal> Seed(InMemoryFinancialGoals goals)=>await new FinancialGoalsApplication(goals,TimeProvider.System).CreateAsync("T001",Customer,"actor-001",new(new string('c',32),new("Education fund",100000m,"INR",new(2030,6,30))),default);
    private static GoalContributionsApplication Owner(InMemoryGoalContributions? records=null)
    {var goals=new InMemoryFinancialGoals();Seed(goals).GetAwaiter().GetResult();return new(goals,records??new(),TimeProvider.System);}
    private static ReviseGoalContribution Change(ReportedGoalContribution record)=>new(Guid.NewGuid().ToString("N"),record.Revision,record.Contribution with {Amount=1250m,ContributionDate=new(2026,10,2)});
    [Theory,InlineData("operation"),InlineData("uppercase"),InlineData("goal"),InlineData("revision"),InlineData("zero"),InlineData("negative"),InlineData("precision"),InlineData("large"),InlineData("date-early"),InlineData("date-future")]
    public async Task InvalidReportedAmountsDatesAndGoalReferencesDoNotWrite(string field)
    {
        var input=Input();var c=input.Contribution;
        input=field switch {"operation"=>input with {OperationId="bad"},"uppercase"=>input with {OperationId=new('A',32)},"goal"=>input with {Contribution=c with {FinancialGoalId="bad id"}},"revision"=>input with {Contribution=c with {GoalRevision=0}},"zero"=>input with {Contribution=c with {Amount=0}},"negative"=>input with {Contribution=c with {Amount=-1}},"precision"=>input with {Contribution=c with {Amount=1.001m}},"large"=>input with {Contribution=c with {Amount=1000000000000m}},"date-early"=>input with {Contribution=c with {ContributionDate=new(1899,12,31)}},_=>input with {Contribution=c with {ContributionDate=new(2099,1,1)}}};
        var app=Owner();Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input,default))).Status);Assert.Empty((await app.ListAsync("T001",Customer,GoalId,default)).Items);Assert.False(GoalContributionsApplication.Valid(null,DateTimeOffset.UtcNow));
    }
    [Fact]
    public async Task GoalRevisionsRemainExactAfterTargetOrCurrencyChangesAndCorrectionsReplayOriginalReceipts()
    {
        var goals=new InMemoryFinancialGoals();var goal=await Seed(goals);var app=new GoalContributionsApplication(goals,new InMemoryGoalContributions(),TimeProvider.System);
        var input=Input();var first=await app.CreateAsync("T001",Customer,"actor-001",input,default);
        var currentGoal=await new FinancialGoalsApplication(goals,TimeProvider.System).ReviseAsync("T001",Customer,"actor-001",GoalId,new(Guid.NewGuid().ToString("N"),1,goal.Goal with {Name="Updated goal",Currency="USD",TargetAmount=200000}),default);
        var change=Change(first);var second=await app.ReviseAsync("T001",Customer,"actor-001",first.GoalContributionId,change,default);var third=await app.ReviseAsync("T001",Customer,"actor-001",first.GoalContributionId,Change(second) with {Contribution=second.Contribution with {Amount=1300}},default);
        Assert.Equal(goal.Goal,first.SourceGoal);Assert.Equal(goal.Goal,second.SourceGoal);Assert.Equal("INR",second.SourceGoal.Currency);Assert.Equal("CUSTOMER_REPORTED",third.DeclarationStatus);Assert.Equal(first,await app.CreateAsync("T001",Customer,"actor-001",input,default));Assert.Equal(second,await app.ReviseAsync("T001",Customer,"actor-001",first.GoalContributionId,change,default));
        Assert.Equal([third,second,first],(await app.HistoryAsync("T001",Customer,first.GoalContributionId,null,default)).Items);Assert.Equal(first,await app.ReadAsync("T001",Customer,first.GoalContributionId,default,1));Assert.Equal(third,Assert.Single((await app.ListAsync("T001",Customer,GoalId,default)).Items));
        var newInput=input with {OperationId=Guid.NewGuid().ToString("N"),Contribution=input.Contribution with {GoalRevision=2}};var next=await app.CreateAsync("T001",Customer,"actor-001",newInput,default);Assert.Equal(currentGoal.Goal,next.SourceGoal);Assert.Equal("USD",next.SourceGoal.Currency);Assert.Equal(input.Contribution.Amount,next.Contribution.Amount);
    }
    [Theory,InlineData("tenant"),InlineData("customer"),InlineData("missing"),InlineData("revision")]
    public async Task OnlyAnOwnedExactSavedGoalVersionCanBeReferenced(string field)
    {
        var app=Owner();var input=Input();if(field=="missing")input=input with {Contribution=input.Contribution with {FinancialGoalId="goal-missing"}};if(field=="revision")input=input with {Contribution=input.Contribution with {GoalRevision=2}};
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync(field=="tenant"?"T002":"T001",field=="customer"?"other":Customer,"actor-001",input,default))).Status);Assert.Empty((await app.ListAsync("T001",Customer,GoalId,default)).Items);
    }
    [Fact]
    public async Task OneOperationCannotCreateDuplicateChangedOrCrossGoalContributions()
    {
        var goals=new InMemoryFinancialGoals();await Seed(goals);var goal=await new FinancialGoalsApplication(goals,TimeProvider.System).CreateAsync("T001",Customer,"actor-001",new(new string('d',32),new("Other goal",2,"USD",new(2030,1,1))),default);var app=new GoalContributionsApplication(goals,new InMemoryGoalContributions(),TimeProvider.System);var first=await app.CreateAsync("T001",Customer,"actor-001",Input(),default);
        foreach(var input in new[]{Input() with {Contribution=Input().Contribution with {Amount=2}},Input() with {Contribution=Input().Contribution with {FinancialGoalId=goal.FinancialGoalId}}})Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input,default))).Status);
        Assert.Equal(first,Assert.Single((await app.ListAsync("T001",Customer,GoalId,default)).Items));Assert.Empty((await app.ListAsync("T001",Customer,goal.FinancialGoalId,default)).Items);
    }
    [Fact]
    public async Task CompetingEditsAndCrossOperationReuseCannotOverwriteSavedVersions()
    {
        var app=Owner();var first=await app.CreateAsync("T001",Customer,"actor-001",Input(),default);
        async Task<bool> Attempt(decimal amount){try{await app.ReviseAsync("T001",Customer,"actor-001",first.GoalContributionId,Change(first) with {Contribution=first.Contribution with {Amount=amount}},default);return true;}catch(TenantBoundaryException e)when(e.Status==409){return false;}}
        Assert.Equal(1,(await Task.WhenAll(Task.Run(()=>Attempt(2)),Task.Run(()=>Attempt(3)))).Count(x=>x));var second=await app.ReadAsync("T001",Customer,first.GoalContributionId,default);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.GoalContributionId,Change(second) with {OperationId=first.OperationId},default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",Input() with {OperationId=second.OperationId},default))).Status);
    }
    [Theory,InlineData("goal"),InlineData("goal-version"),InlineData("amount"),InlineData("date"),InlineData("expected")]
    public async Task InvalidCorrectionsCannotMoveContributionsBetweenGoalVersions(string kind)
    {
        var app=Owner();var first=await app.CreateAsync("T001",Customer,"actor-001",Input(),default);var change=Change(first);
        change=kind switch {"goal"=>change with {Contribution=change.Contribution with {FinancialGoalId="goal-other"}},"goal-version"=>change with {Contribution=change.Contribution with {GoalRevision=2}},"amount"=>change with {Contribution=change.Contribution with {Amount=0}},"date"=>change with {Contribution=change.Contribution with {ContributionDate=new(2099,1,1)}},_=>change with {ExpectedRevision=0}};
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.GoalContributionId,change,default))).Status);Assert.Equal(first,await app.ReadAsync("T001",Customer,first.GoalContributionId,default));
    }
    [Fact]
    public async Task ListsAndHistoryAreBoundedAndAmountsAreNotCappedByOrConvertedToTheGoalTarget()
    {
        var app=Owner();var first=await app.CreateAsync("T001",Customer,"actor-001",Input() with {Contribution=Input().Contribution with {Amount=999999999999.99m}},default);var current=first;
        for(var i=0;i<53;i++)current=await app.ReviseAsync("T001",Customer,"actor-001",first.GoalContributionId,Change(current),default);
        var page=await app.HistoryAsync("T001",Customer,first.GoalContributionId,null,default);Assert.Equal(50,page.Items.Count);Assert.True(page.HasMore);Assert.Equal(5,page.NextBeforeRevision);Assert.Equal([4,3,2,1],(await app.HistoryAsync("T001",Customer,first.GoalContributionId,5,default)).Items.Select(x=>x.Revision));
        for(var i=0;i<200;i++)await app.CreateAsync("T001",Customer,"actor-001",Input() with {OperationId=Guid.NewGuid().ToString("N")},default);var list=await app.ListAsync("T001",Customer,GoalId,default);Assert.Equal(200,list.Items.Count);Assert.True(list.HasMore);Assert.Equal(999999999999.99m,first.Contribution.Amount);
    }
    [Fact]
    public async Task ActualOwnerAndProductRoutesRetainGoalSnapshotAndEverySavedContributionVersionWithoutDocumentAccess()
    {
        await using var h=await Harness.Create();using var client=h.Client();h.Authority.Failure="evidence";
        var response=await client.PostAsJsonAsync("/product-api/v1/goal-contributions",Input(),ContractJson.Options);Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("no-store",response.Headers.CacheControl?.ToString());var first=(await response.Content.ReadFromJsonAsync<ReportedGoalContribution>(ContractJson.Options))!;var path="/product-api/v1/goal-contributions/"+first.GoalContributionId;var change=Change(first);
        var second=(await(await client.PostAsJsonAsync(path+"/corrections",change,ContractJson.Options)).Content.ReadFromJsonAsync<ReportedGoalContribution>(ContractJson.Options))!;Assert.Equal(2,second.Revision);Assert.Equal(first.SourceGoal,second.SourceGoal);Assert.Equal(first,await client.GetFromJsonAsync<ReportedGoalContribution>(path+"?revision=1",ContractJson.Options));Assert.Equal([second,first],(await client.GetFromJsonAsync<GoalContributionHistory>(path+"/history",ContractJson.Options))!.Items);
        Assert.Equal(first,await(await client.PostAsJsonAsync("/product-api/v1/goal-contributions",Input(),ContractJson.Options)).Content.ReadFromJsonAsync<ReportedGoalContribution>(ContractJson.Options));Assert.Equal(second,Assert.Single((await client.GetFromJsonAsync<ProductGoalContributionList>("/product-api/v1/goal-contributions?financialGoalId="+GoalId,ContractJson.Options))!.Items));
    }
    [Theory,InlineData("deny",403),InlineData("advisor",403),InlineData("unavailable",503),InlineData("update",403),InlineData("read",403),InlineData("missing",401),InlineData("tenant",401)]
    public async Task CurrentSelfAuthorityIsRequiredBeforeWrite(string failure,int status)
    {
        await using var h=await Harness.Create();h.Authority.Failure=failure;using var client=h.Client(failure=="missing"?null:failure=="tenant"?"T002":"T001");Assert.Equal(status,(int)(await client.PostAsJsonAsync("/product-api/v1/goal-contributions",Input(),ContractJson.Options)).StatusCode);Assert.Empty((await h.Records.ListAsync("T001",Customer,GoalId,default)).Items);
    }
    [Fact]
    public async Task CurrentReadOnlyAccessCanInspectButNotEditAndOwnerBindingRejectsForeignTenant()
    {
        await using var h=await Harness.Create();using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/goal-contributions",Input(),ContractJson.Options);var first=Assert.Single((await h.Records.ListAsync("T001",Customer,GoalId,default)).Items);h.Authority.Failure="update";var path="/product-api/v1/goal-contributions/"+first.GoalContributionId;Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(path+"/history")).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path+"/corrections",Change(first),ContractJson.Options)).StatusCode);h.Authority.Failure="";h.Authority.AllowForeignTenant=true;using var foreign=h.OwnerClient("T002");Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync("/local/v1/customers/"+Customer+"/goal-contributions/"+first.GoalContributionId)).StatusCode);
    }
    [Theory,InlineData(true,"unknown"),InlineData(false,"unknown"),InlineData(true,"nested"),InlineData(false,"nested"),InlineData(true,"duplicate"),InlineData(false,"duplicate"),InlineData(true,"missing"),InlineData(false,"missing"),InlineData(true,"null"),InlineData(false,"null"),InlineData(true,"date"),InlineData(false,"date"),InlineData(true,"large"),InlineData(false,"large")]
    public async Task StrictIngressRejectsAmbiguousBodiesBeforeMutation(bool product,string kind)
    {
        await using var h=await Harness.Create();using var client=product?h.Client():h.OwnerClient();var json=JsonNode.Parse(JsonSerializer.Serialize(Input(),ContractJson.Options))!.AsObject();switch(kind){case "unknown":json["customerId"]="foreign";break;case "nested":json["contribution"]!["currency"]="USD";break;case "missing":json.Remove("operationId");break;case "null":json["contribution"]=null;break;case "date":json["contribution"]!["contributionDate"]="bad";break;case "large":json["unexpected"]=new string('x',40000);break;}var body=json.ToJsonString();if(kind=="duplicate")body=body.Insert(1,"\"OPERATIONID\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",");Assert.Equal(kind=="large"?413:400,(int)(await client.PostAsync(product?"/product-api/v1/goal-contributions":"/local/v1/customers/"+Customer+"/goal-contributions",new StringContent(body,Encoding.UTF8,"application/json"))).StatusCode);Assert.Empty((await h.Records.ListAsync("T001",Customer,GoalId,default)).Items);
    }
    [Theory,InlineData("amount"),InlineData("source"),InlineData("goal-version"),InlineData("date"),InlineData("actor"),InlineData("customer"),InlineData("operation"),InlineData("status"),InlineData("revision"),InlineData("null"),InlineData("readback")]
    public async Task ForgedCreationReceiptsOrReadbackCannotBeConfirmed(string kind)
    {
        await using var h=await Harness.Create("create-"+kind);using var client=h.Client();Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.PostAsJsonAsync("/product-api/v1/goal-contributions",Input(),ContractJson.Options)).StatusCode);Assert.Single((await h.Records.ListAsync("T001",Customer,GoalId,default)).Items);
    }
    [Theory,InlineData("amount"),InlineData("source"),InlineData("goal-version"),InlineData("date"),InlineData("actor"),InlineData("customer"),InlineData("operation"),InlineData("status"),InlineData("revision"),InlineData("null"),InlineData("readback")]
    public async Task ForgedCorrectionReceiptsOrReadbackCannotBeConfirmed(string kind)
    {
        await using var h=await Harness.Create("correction-"+kind);using var client=h.Client();Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/product-api/v1/goal-contributions",Input(),ContractJson.Options)).StatusCode);var first=Assert.Single((await h.Records.ListAsync("T001",Customer,GoalId,default)).Items);Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.PostAsJsonAsync("/product-api/v1/goal-contributions/"+first.GoalContributionId+"/corrections",Change(first),ContractJson.Options)).StatusCode);Assert.Equal(2,(await h.Records.ReadAsync("T001",Customer,first.GoalContributionId,default))!.Revision);
    }
    [Theory,InlineData("null"),InlineData("duplicate"),InlineData("customer"),InlineData("goal"),InlineData("hasMore"),InlineData("large")]
    public async Task ForgedListsCannotEscapeTheSelectedGoalOrBounds(string kind)
    {
        await using var h=await Harness.Create("list-"+kind);using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/goal-contributions",Input(),ContractJson.Options);Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.GetAsync("/product-api/v1/goal-contributions?financialGoalId="+GoalId)).StatusCode);
    }
    [Theory,InlineData("null"),InlineData("customer"),InlineData("goal"),InlineData("source"),InlineData("goal-version"),InlineData("status"),InlineData("gap"),InlineData("empty"),InlineData("cursor"),InlineData("tail")]
    public async Task ForgedHistoryCannotSubstituteGoalSnapshotCustomerOrVersionWindow(string kind)
    {
        await using var h=await Harness.Create("history-"+kind);using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/goal-contributions",Input(),ContractJson.Options);var first=Assert.Single((await h.Records.ListAsync("T001",Customer,GoalId,default)).Items);var path="/product-api/v1/goal-contributions/"+first.GoalContributionId;await client.PostAsJsonAsync(path+"/corrections",Change(first),ContractJson.Options);Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.GetAsync(path+"/history")).StatusCode);
    }
    [Fact]
    public async Task LostConfirmationReplaysTheExactEditAfterLaterContributionAndGoalChanges()
    {
        await using var h=await Harness.Create();using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/goal-contributions",Input(),ContractJson.Options);var first=Assert.Single((await h.Records.ListAsync("T001",Customer,GoalId,default)).Items);var path="/product-api/v1/goal-contributions/"+first.GoalContributionId+"/corrections";var change=Change(first);h.Authority.ResetEvaluations();h.Authority.DenyAfterEvaluations=6;Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path,change,ContractJson.Options)).StatusCode);var second=(await h.Records.ReadAsync("T001",Customer,first.GoalContributionId,default))!;Assert.Equal(2,second.Revision);h.Authority.DenyAfterEvaluations=0;await client.PostAsJsonAsync(path,Change(second),ContractJson.Options);Assert.Equal(second,await(await client.PostAsJsonAsync(path,change,ContractJson.Options)).Content.ReadFromJsonAsync<ReportedGoalContribution>(ContractJson.Options));
    }
    [Fact]
    public async Task GoalProgressUsesLatestActiveReportsAndRetainsExactSourceVersions()
    {
        var goals=new InMemoryFinancialGoals();var goal=await Seed(goals);var records=new InMemoryGoalContributions();var contributions=new GoalContributionsApplication(goals,records,TimeProvider.System);var progress=new FinancialGoalProgressApplication(goals,records,TimeProvider.System);
        var first=await contributions.CreateAsync("T001",Customer,"actor-001",Input(),default);var revised=await contributions.ReviseAsync("T001",Customer,"actor-001",first.GoalContributionId,Change(first),default);
        var secondInput=Input() with {OperationId=Guid.NewGuid().ToString("N"),Contribution=Input().Contribution with {Amount=2500m,ContributionDate=new(2026,10,3)}};var second=await contributions.CreateAsync("T001",Customer,"actor-001",secondInput,default);
        await contributions.WithdrawAsync("T001",Customer,"actor-001",second.GoalContributionId,new(Guid.NewGuid().ToString("N"),1,"Entered against the wrong goal."),default);
        var currentGoal=await new FinancialGoalsApplication(goals,TimeProvider.System).ReviseAsync("T001",Customer,"actor-001",GoalId,new(Guid.NewGuid().ToString("N"),1,goal.Goal with {TargetAmount=125000m}),default);
        var result=await progress.ReadAsync("T001",Customer,GoalId,default);
        Assert.Equal(currentGoal.Revision,result.GoalRevision);Assert.Equal(1250m,result.ReportedContributionAmount);Assert.Equal(123750m,result.RemainingAmount);Assert.Equal(1m,result.ProgressPercent);Assert.Equal(1,result.IncludedContributionCount);Assert.Equal(1,result.WithdrawnContributionCount);Assert.Equal(new DateOnly(2026,10,2),result.LatestContributionDate);
        var source=Assert.Single(result.Sources);Assert.Equal(revised.GoalContributionId,source.GoalContributionId);Assert.Equal(2,source.ContributionRevision);Assert.Equal(1,source.GoalRevision);Assert.Equal("CUSTOMER_REPORTED",result.ProgressBasis);Assert.Equal("NOT_DETERMINED",result.AchievementStatus);
    }
    [Fact]
    public async Task GoalProgressStartsAtZeroWithoutInferringAchievementOrRequiredContributions()
    {
        var goals=new InMemoryFinancialGoals();await Seed(goals);var result=await new FinancialGoalProgressApplication(goals,new InMemoryGoalContributions(),TimeProvider.System).ReadAsync("T001",Customer,GoalId,default);
        Assert.Equal(0m,result.ReportedContributionAmount);Assert.Equal(100000m,result.RemainingAmount);Assert.Equal(0m,result.ProgressPercent);Assert.Empty(result.Sources);Assert.Null(result.LatestContributionDate);Assert.Equal("NOT_DETERMINED",result.AchievementStatus);
    }
    [Fact]
    public async Task GoalCurrencyChangeFailsClosedInsteadOfConvertingHistoricalReports()
    {
        var goals=new InMemoryFinancialGoals();var goal=await Seed(goals);var records=new InMemoryGoalContributions();var contributions=new GoalContributionsApplication(goals,records,TimeProvider.System);await contributions.CreateAsync("T001",Customer,"actor-001",Input(),default);
        await new FinancialGoalsApplication(goals,TimeProvider.System).ReviseAsync("T001",Customer,"actor-001",GoalId,new(Guid.NewGuid().ToString("N"),1,goal.Goal with {Currency="USD"}),default);
        var error=await Assert.ThrowsAsync<TenantBoundaryException>(()=>new FinancialGoalProgressApplication(goals,records,TimeProvider.System).ReadAsync("T001",Customer,GoalId,default));Assert.Equal(409,error.Status);Assert.Equal("GOAL_PROGRESS_CURRENCY_CHANGED",error.Code);
    }
    [Fact]
    public async Task IncompleteContributionPageCannotProduceUnderstatedProgress()
    {
        var goals=new InMemoryFinancialGoals();await Seed(goals);var records=new InMemoryGoalContributions();var contributions=new GoalContributionsApplication(goals,records,TimeProvider.System);
        for(var index=0;index<201;index++)await contributions.CreateAsync("T001",Customer,"actor-001",Input() with {OperationId=Guid.NewGuid().ToString("N"),Contribution=Input().Contribution with {Amount=1m}},default);
        var error=await Assert.ThrowsAsync<TenantBoundaryException>(()=>new FinancialGoalProgressApplication(goals,records,TimeProvider.System).ReadAsync("T001",Customer,GoalId,default));Assert.Equal(503,error.Status);Assert.Equal("GOAL_PROGRESS_INCOMPLETE",error.Code);
    }
    [Fact]
    public async Task ProductGoalProgressRequiresCurrentSelfAuthorityBeforeAndAfterOwnerRead()
    {
        await using var h=await Harness.Create();using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/goal-contributions",Input(),ContractJson.Options);var path="/product-api/v1/financial-goals/"+GoalId+"/progress";
        var response=await client.GetAsync(path);Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("no-store",response.Headers.CacheControl?.ToString());var result=(await response.Content.ReadFromJsonAsync<FinancialGoalProgress>(ContractJson.Options))!;Assert.Equal(1000m,result.ReportedContributionAmount);
        h.Authority.Failure="read";Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(path)).StatusCode);h.Authority.Failure="";h.Authority.ResetEvaluations();h.Authority.DenyAfterEvaluations=2;Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(path)).StatusCode);
    }
    [Fact]
    public async Task ProductGoalProgressRejectsForeignTenantAndCrossCurrencyAggregation()
    {
        await using var h=await Harness.Create();using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/goal-contributions",Input(),ContractJson.Options);var path="/product-api/v1/financial-goals/"+GoalId+"/progress";
        h.Authority.AllowForeignTenant=true;using var foreign=h.Client("T002");Assert.Equal(HttpStatusCode.Unauthorized,(await foreign.GetAsync(path)).StatusCode);h.Authority.AllowForeignTenant=false;
        var goal=(await h.Goals.ReadAsync("T001",Customer,GoalId,default))!;await new FinancialGoalsApplication(h.Goals,TimeProvider.System).ReviseAsync("T001",Customer,"actor-001",GoalId,new(Guid.NewGuid().ToString("N"),goal.Revision,goal.Goal with {Currency="USD"}),default);Assert.Equal(HttpStatusCode.Conflict,(await client.GetAsync(path)).StatusCode);
    }
    private sealed class TamperHandler(string scenario):DelegatingHandler(new SocketsHttpHandler {AllowAutoRedirect=false,UseProxy=false})
    {
        private bool corrected;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var response=await base.SendAsync(request,ct);if(!response.IsSuccessStatusCode)return response;var path=request.RequestUri!.AbsolutePath;var kind=scenario[(scenario.IndexOf('-')+1)..];var json=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
            if(scenario.StartsWith("list-",StringComparison.Ordinal))
            {
                if(request.Method!=HttpMethod.Get||!path.EndsWith("/goal-contributions",StringComparison.Ordinal))return response;var items=json["items"]!.AsArray();switch(kind){case "null":json["items"]=null;break;case "duplicate":items.Add(items[0]!.DeepClone());break;case "customer":items[0]!["customerId"]="foreign";break;case "goal":items[0]!["contribution"]!["financialGoalId"]="goal-foreign";break;case "hasMore":json["hasMore"]=true;break;case "large":while(items.Count<201)items.Add(items[0]!.DeepClone());break;}
            }
            else if(scenario.StartsWith("history-",StringComparison.Ordinal))
            {
                if(!path.EndsWith("/history",StringComparison.Ordinal))return response;var items=json["items"]!.AsArray();switch(kind){case "null":json["items"]=null;break;case "customer":items[0]!["customerId"]="foreign";break;case "goal":items[0]!["contribution"]!["financialGoalId"]="goal-other";break;case "source":items[0]!["sourceGoal"]!["currency"]="USD";break;case "goal-version":items[0]!["contribution"]!["goalRevision"]=99;break;case "status":items[0]!["declarationStatus"]="VERIFIED";break;case "gap":items[0]!["revision"]=99;break;case "empty":json["items"]=new JsonArray();break;case "cursor":json["nextBeforeRevision"]=2;break;case "tail":items.RemoveAt(items.Count-1);break;}
            }
            else
            {
                if(scenario.StartsWith("correction-",StringComparison.Ordinal)||scenario.StartsWith("withdrawal-",StringComparison.Ordinal)){if(request.Method==HttpMethod.Post&&path.EndsWith(scenario.StartsWith("withdrawal-",StringComparison.Ordinal)?"/withdrawals":"/corrections",StringComparison.Ordinal))corrected=true;else if(!corrected||kind!="readback")return response;}
                if((kind=="readback")!=(request.Method==HttpMethod.Get))return response;
                switch(kind){case "reason":json["withdrawalReason"]="Substituted reason";break;case "source":json["sourceGoal"]!["currency"]="USD";break;case "goal-version":json["contribution"]!["goalRevision"]=99;break;case "date":json["contribution"]!["contributionDate"]="2026-09-01";break;case "actor":json["recordedBy"]="foreign";break;case "customer":json["customerId"]="foreign";break;case "operation":json["operationId"]=new string('b',32);break;case "status":json["declarationStatus"]="VERIFIED";break;case "revision":json["revision"]=99;break;case "null":json["contribution"]=null;break;default:json["contribution"]!["amount"]=1;break;}
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
    private sealed class Harness(WebApplication owner, WebApplication gateway, Authority authority, InMemoryGoalContributions records,InMemoryFinancialGoals goals) : IAsyncDisposable
    {
        public Authority Authority => authority; public InMemoryGoalContributions Records => records; public InMemoryFinancialGoals Goals=>goals;
        public static async Task<Harness> Create(string? tamper=null)
        {
            var records = new InMemoryGoalContributions();var goals=new InMemoryFinancialGoals();await Seed(goals); var authority = new Authority();
            var values = new Dictionary<string,string?> { ["Monergy:ExecutionZone"]="CI_EPHEMERAL", ["Monergy:AccessIntegration:Enabled"]="true", ["Monergy:Product:Enabled"]="true",
                ["Monergy:AccessIntegration:CustomerIdentityUrl"]="http://127.0.0.1:5001/", ["Monergy:AccessIntegration:AccessManagementUrl"]="http://127.0.0.1:5002/",
                ["Monergy:AccessIntegration:CustomerContextToken"]=new('a',32), ["Monergy:AccessIntegration:CustomerIdentityToken"]=new('b',32), ["Monergy:AccessIntegration:MembershipToken"]=new('c',32) };
            var ob = WebApplication.CreateSlimBuilder(); ob.WebHost.UseUrls("http://127.0.0.1:0"); ob.Configuration.AddInMemoryCollection(values);
            ob.Services.AddSingleton(new TenantAccessClient(ob.Configuration, new HttpClient(authority,false)));
            ob.Services.AddSingleton(new TenantBoundaryOptions { Service="financial-profile", CapabilityPrefix="financial-profile.", CustomerTenants=new Dictionary<string,string> { [Customer]="T001" }, ProbeUrl="http://127.0.0.1:5199/" });
            ob.Services.AddSingleton(new GoalContributionsApplication(goals,records,TimeProvider.System)); ob.Services.AddSingleton(new FinancialGoalsApplication(goals,TimeProvider.System));ob.Services.AddSingleton(new FinancialGoalProgressApplication(goals,records,TimeProvider.System));var owner=ob.Build();owner.MapFinancialGoals(); owner.MapGoalContributions(); await owner.StartAsync();
            values["Monergy:Product:FinancialProfileUrl"]=owner.Urls.Single()+"/";
            var gb=WebApplication.CreateSlimBuilder(); gb.WebHost.UseUrls("http://127.0.0.1:0"); gb.Configuration.AddInMemoryCollection(values);
            gb.Services.AddSingleton(new CustomerOwnerClient(gb.Configuration,new HttpClient(authority,false)));
            gb.Services.AddSingleton(new TenantAccessClient(gb.Configuration,new HttpClient(authority,false))); gb.AddProductFinancialGoals();gb.AddProductGoalContributions();
            if(tamper is not null) gb.Services.AddSingleton(new ProductGoalContributionClient(gb.Configuration,new HttpClient(new TamperHandler(tamper))));
            var gateway=gb.Build(); gateway.MapProductFinancialGoals();gateway.MapProductGoalContributions(); await gateway.StartAsync(); return new(owner,gateway,authority,records,goals);
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
