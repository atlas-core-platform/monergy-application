using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Monergy.Contracts;
using Monergy.Platform;
using Xunit;
namespace Monergy.FinancialProfile.Tests;
public sealed partial class CustomerGoalContributionTests
{
    private static WithdrawGoalContribution Withdrawal(int revision=1)=>new(Guid.NewGuid().ToString("N"),revision,"Reported twice by mistake");
    [Fact]
    public async Task WithdrawalRetainsExactPriorReportAndOriginalReceiptsWithoutChangingTheGoal()
    {
        var owner=Owner();var first=await owner.CreateAsync("T001",Customer,"actor-001",Input(),default);
        var change=Change(first);var second=await owner.ReviseAsync("T001",Customer,"actor-001",first.GoalContributionId,change,default);
        var input=Withdrawal(2);var withdrawn=await owner.WithdrawAsync("T001",Customer,"actor-001",first.GoalContributionId,input,default);
        Assert.Equal(second.Contribution,withdrawn.Contribution);Assert.Equal(first.SourceGoal,withdrawn.SourceGoal);
        Assert.Equal("CUSTOMER_WITHDRAWN",withdrawn.DeclarationStatus);Assert.Equal(input.Reason,withdrawn.WithdrawalReason);Assert.Equal(3,withdrawn.Revision);
        Assert.Equal(withdrawn,await owner.WithdrawAsync("T001",Customer,"actor-001",first.GoalContributionId,input,default));
        Assert.Equal(second,await owner.ReviseAsync("T001",Customer,"actor-001",first.GoalContributionId,change,default));
        Assert.Equal(first,await owner.CreateAsync("T001",Customer,"actor-001",Input(),default));
        Assert.Equal([withdrawn,second,first],(await owner.HistoryAsync("T001",Customer,first.GoalContributionId,null,default)).Items);
        Assert.Equal([second,first],(await owner.HistoryAsync("T001",Customer,first.GoalContributionId,3,default)).Items);
        Assert.Equal(withdrawn,Assert.Single((await owner.ListAsync("T001",Customer,GoalId,default)).Items));
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.ReviseAsync("T001",Customer,"actor-001",first.GoalContributionId,Change(withdrawn),default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.WithdrawAsync("T001",Customer,"actor-001",first.GoalContributionId,Withdrawal(3),default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.WithdrawAsync("T001",Customer,"actor-001",first.GoalContributionId,input with {Reason="Changed reason"},default))).Status);
    }
    [Theory,InlineData("operation"),InlineData("upper"),InlineData("revision"),InlineData("overflow"),InlineData("blank"),InlineData("trim"),InlineData("control"),InlineData("large"),InlineData("null")]
    public async Task InvalidWithdrawalDoesNotMutateTheReport(string kind)
    {
        var owner=Owner();var first=await owner.CreateAsync("T001",Customer,"actor-001",Input(),default);var input=Withdrawal();
        input=kind switch {"operation"=>input with {OperationId="bad"},"upper"=>input with {OperationId=new string('A',32)},"revision"=>input with {ExpectedRevision=0},"overflow"=>input with {ExpectedRevision=int.MaxValue},"blank"=>input with {Reason=" "},"trim"=>input with {Reason=" reason "},"control"=>input with {Reason="a\nb"},"large"=>input with {Reason=new string('a',501)},_=>input with {Reason=null!}};
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.WithdrawAsync("T001",Customer,"actor-001",first.GoalContributionId,input,default))).Status);
        Assert.Equal(first,await owner.ReadAsync("T001",Customer,first.GoalContributionId,default));
    }
    [Fact]
    public async Task ConcurrentCorrectionAndWithdrawalPermitExactlyOneVersionAndRemainTenantScoped()
    {
        var owner=Owner();var first=await owner.CreateAsync("T001",Customer,"actor-001",Input(),default);
        async Task<int> Attempt(Func<Task<ReportedGoalContribution>> action){try{await action();return 200;}catch(TenantBoundaryException e){return e.Status;}}
        var results=await Task.WhenAll(Attempt(()=>owner.WithdrawAsync("T001",Customer,"actor-001",first.GoalContributionId,Withdrawal(),default)),Attempt(()=>owner.ReviseAsync("T001",Customer,"actor-001",first.GoalContributionId,Change(first),default)));
        Assert.Equal([200,409],results.Order().ToArray());Assert.Equal(2,(await owner.ReadAsync("T001",Customer,first.GoalContributionId,default)).Revision);
        foreach(var scope in new[]{("T002",Customer),("T001","foreign")})Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.WithdrawAsync(scope.Item1,scope.Item2,"actor-001",first.GoalContributionId,Withdrawal(),default))).Status);
    }
    [Theory,InlineData("create"),InlineData("correction"),InlineData("other-report")]
    public async Task WithdrawalCannotReuseAnotherMutationOperation(string kind)
    {
        var owner=Owner();var first=await owner.CreateAsync("T001",Customer,"actor-001",Input(),default);var op=first.OperationId;var revision=1;
        if(kind=="correction"){var second=await owner.ReviseAsync("T001",Customer,"actor-001",first.GoalContributionId,Change(first),default);op=second.OperationId;revision=2;}
        if(kind=="other-report"){var other=await owner.CreateAsync("T001",Customer,"actor-001",Input() with {OperationId=Guid.NewGuid().ToString("N")},default);op=(await owner.WithdrawAsync("T001",Customer,"actor-001",other.GoalContributionId,Withdrawal(),default)).OperationId;}
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.WithdrawAsync("T001",Customer,"actor-001",first.GoalContributionId,new(op,revision,"Wrong operation"),default))).Status);
    }
    [Theory,InlineData(true),InlineData(false)]
    public async Task ActualHttpWithdrawalRequiresExplicitReviewAndRetainsReadOnlyHistory(bool product)
    {
        await using var h=await Harness.Create();using var client=product?h.Client():h.OwnerClient();var root=product?"/product-api/v1/goal-contributions":"/local/v1/customers/"+Customer+"/goal-contributions";
        var first=(await(await client.PostAsJsonAsync(root,Input(),ContractJson.Options)).Content.ReadFromJsonAsync<ReportedGoalContribution>(ContractJson.Options))!;var path=root+"/"+first.GoalContributionId;var input=Withdrawal();
        var response=await client.PostAsJsonAsync(path+"/withdrawals",input,ContractJson.Options);Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("no-store",response.Headers.CacheControl?.ToString());var saved=(await response.Content.ReadFromJsonAsync<ReportedGoalContribution>(ContractJson.Options))!;
        Assert.Equal("CUSTOMER_WITHDRAWN",saved.DeclarationStatus);Assert.Equal(first.Contribution,saved.Contribution);Assert.Equal(saved,await(await client.PostAsJsonAsync(path+"/withdrawals",input,ContractJson.Options)).Content.ReadFromJsonAsync<ReportedGoalContribution>(ContractJson.Options));
        h.Authority.Failure="update";Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path+"/withdrawals",Withdrawal(2),ContractJson.Options)).StatusCode);Assert.Equal([saved,first],(await client.GetFromJsonAsync<GoalContributionHistory>(path+"/history",ContractJson.Options))!.Items);
    }
    [Theory,InlineData("deny",403),InlineData("advisor",403),InlineData("read",403),InlineData("update",403),InlineData("unavailable",503)]
    public async Task WithdrawalChecksCurrentSelfAuthorityBeforeMutation(string failure,int status)
    {
        await using var h=await Harness.Create();using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/goal-contributions",Input(),ContractJson.Options);var first=Assert.Single((await h.Records.ListAsync("T001",Customer,GoalId,default)).Items);h.Authority.Failure=failure;
        Assert.Equal(status,(int)(await client.PostAsJsonAsync("/product-api/v1/goal-contributions/"+first.GoalContributionId+"/withdrawals",Withdrawal(),ContractJson.Options)).StatusCode);Assert.Equal(first,await h.Records.ReadAsync("T001",Customer,first.GoalContributionId,default));
    }
    [Theory,InlineData(true,"unknown"),InlineData(false,"unknown"),InlineData(true,"duplicate"),InlineData(false,"duplicate"),InlineData(true,"missing"),InlineData(false,"missing"),InlineData(true,"null"),InlineData(false,"null"),InlineData(true,"large"),InlineData(false,"large")]
    public async Task WithdrawalIngressRejectsAmbiguousBodies(bool product,string kind)
    {
        await using var h=await Harness.Create();using var client=product?h.Client():h.OwnerClient();var root=product?"/product-api/v1/goal-contributions":"/local/v1/customers/"+Customer+"/goal-contributions";await client.PostAsJsonAsync(root,Input(),ContractJson.Options);var first=Assert.Single((await h.Records.ListAsync("T001",Customer,GoalId,default)).Items);
        var json=JsonNode.Parse(JsonSerializer.Serialize(Withdrawal(),ContractJson.Options))!.AsObject();if(kind=="unknown")json["amount"]=0;if(kind=="missing")json.Remove("reason");if(kind=="null")json["reason"]=null;if(kind=="large")json["reason"]=new string('a',40000);var body=json.ToJsonString();if(kind=="duplicate")body=body.Insert(1,"\"reason\":\"another\",");
        var response=await client.PostAsync(root+"/"+first.GoalContributionId+"/withdrawals",new StringContent(body,Encoding.UTF8,"application/json"));Assert.Equal(kind=="large"?413:400,(int)response.StatusCode);Assert.Equal(first,await h.Records.ReadAsync("T001",Customer,first.GoalContributionId,default));
    }
    [Theory,InlineData("amount"),InlineData("source"),InlineData("goal-version"),InlineData("date"),InlineData("actor"),InlineData("customer"),InlineData("operation"),InlineData("status"),InlineData("revision"),InlineData("null"),InlineData("reason"),InlineData("readback")]
    public async Task ProductRejectsForgedWithdrawalReceiptsAndReadbacks(string kind)
    {
        await using var h=await Harness.Create("withdrawal-"+kind);using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/goal-contributions",Input(),ContractJson.Options);var first=Assert.Single((await h.Records.ListAsync("T001",Customer,GoalId,default)).Items);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.PostAsJsonAsync("/product-api/v1/goal-contributions/"+first.GoalContributionId+"/withdrawals",Withdrawal(),ContractJson.Options)).StatusCode);
        Assert.Equal("CUSTOMER_WITHDRAWN",(await h.Records.ReadAsync("T001",Customer,first.GoalContributionId,default))!.DeclarationStatus);
    }
    [Fact]
    public async Task LostWithdrawalConfirmationCanReplayAfterGoalCurrencyChangesAndAuthorityReturns()
    {
        await using var h=await Harness.Create();using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/goal-contributions",Input(),ContractJson.Options);var first=Assert.Single((await h.Records.ListAsync("T001",Customer,GoalId,default)).Items);var input=Withdrawal();var path="/product-api/v1/goal-contributions/"+first.GoalContributionId+"/withdrawals";
        h.Authority.ResetEvaluations();h.Authority.DenyAfterEvaluations=6;Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path,input,ContractJson.Options)).StatusCode);var saved=(await h.Records.ReadAsync("T001",Customer,first.GoalContributionId,default))!;Assert.Equal("CUSTOMER_WITHDRAWN",saved.DeclarationStatus);h.Authority.DenyAfterEvaluations=0;
        await new Monergy.Services.FinancialProfile.Application.FinancialGoalsApplication(h.Goals,TimeProvider.System).ReviseAsync("T001",Customer,"actor-001",GoalId,new(Guid.NewGuid().ToString("N"),1,first.SourceGoal with {Currency="USD"}),default);
        Assert.Equal(saved,await(await client.PostAsJsonAsync(path,input,ContractJson.Options)).Content.ReadFromJsonAsync<ReportedGoalContribution>(ContractJson.Options));Assert.Equal("INR",saved.SourceGoal.Currency);
    }
}
