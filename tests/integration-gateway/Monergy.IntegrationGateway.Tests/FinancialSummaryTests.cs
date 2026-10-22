using System.Net;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.AiIntelligence.Qa;
using Monergy.Services.IntegrationGateway.AiModel;
using Xunit;
namespace Monergy.IntegrationGateway.Tests;
public sealed partial class FinancialQaTests
{
    private static readonly QaClaim[] SummaryClaims=Enumerable.Range(0,6).Select(x=>new QaClaim("summary-"+x,"Canonical result "+x,x<3?"Calculation":"MonthlyCalculation","position-"+x,1,"hash","",null,"2026-09-30",x<3?"net-result":"cash-result","1.0.0")).ToArray();
    private static AskFinancialQuestion SummaryInput()=>Input() with {Question="Summarize reviewed finances",SummaryNetWorthCalculationId="net-result",SummaryCashflowCalculationId="cash-result"};
    [Fact]
    public async Task SummaryUsesAllSixCanonicalResultsAndOnlyOneModelCallAcrossReplay()
    {
        using var fixture=new Fixture();var owners=new Owners();var service=new QaApplication(owners,fixture.Store);var input=SummaryInput();
        var answer=await service.Ask(Http(),input);Assert.Equal("SOURCES",answer.State);Assert.Equal(SummaryClaims,answer.Claims);Assert.Equal(1,owners.Calls);
        Assert.Contains("not a bank balance",answer.Introduction,StringComparison.Ordinal);Assert.True(NetWorthData.Same(answer,await service.Ask(Http(),input)));Assert.Equal(1,owners.Calls);
        using var reopened=new QaStore(fixture.Config);Assert.Single(await new QaApplication(owners,reopened).History(Http()));
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>service.Ask(Http(),input with {SummaryCashflowCalculationId="different"}))).Status);
    }
    [Fact]
    public async Task SummaryIncompleteCoverageBypassesModelAndNeverReportsTotals()
    {
        using var fixture=new Fixture();var owners=new Owners{SummaryIncomplete=true};var service=new QaApplication(owners,fixture.Store);
        var answer=await service.Ask(Http(),SummaryInput());Assert.Equal("INSUFFICIENT_DATA",answer.State);Assert.Equal("monthly-incomplete",Assert.Single(answer.Claims).Id);Assert.Equal(0,owners.Calls);
    }
    [Fact]
    public async Task ModelCannotOmitOneSummarySideAndTheFailedAttemptDoesNotSpendAgain()
    {
        using var fixture=new Fixture();var owners=new Owners{OmitSummaryClaim=true};var service=new QaApplication(owners,fixture.Store);var input=SummaryInput();
        await Assert.ThrowsAsync<TenantBoundaryException>(()=>service.Ask(Http(),input));Assert.Equal(1,owners.Calls);Assert.Equal("NOT_COMPLETED",(await service.Ask(Http(),input)).State);Assert.Equal(1,owners.Calls);
    }
    [Theory]
    [InlineData("missing-net")][InlineData("missing-cash")][InlineData("document")][InlineData("position")][InlineData("invalid")]
    public async Task SummaryRejectsMissingMixedOrInvalidSourceIdentitiesBeforeSpend(string mode)
    {
        using var fixture=new Fixture();var owners=new Owners();var service=new QaApplication(owners,fixture.Store);var input=SummaryInput();
        input=mode switch {"missing-net"=>input with {SummaryNetWorthCalculationId=null},"missing-cash"=>input with {SummaryCashflowCalculationId=null},"document"=>input with {DocumentVersionId="version"},"position"=>input with {NetWorthPositionId="position"},_=>input with {SummaryCashflowCalculationId="../other"}};
        await Assert.ThrowsAsync<TenantBoundaryException>(()=>service.Ask(Http(),input));Assert.Equal(0,owners.Calls);
    }
    [Fact]
    public async Task SummaryRevocationDuringGenerationAndOnHistoryFailsClosed()
    {
        using var fixture=new Fixture();var owners=new Owners{RevokeAfterSelection=true};var service=new QaApplication(owners,fixture.Store);
        await Assert.ThrowsAsync<TenantBoundaryException>(()=>service.Ask(Http(),SummaryInput()));owners.Denied=false;owners.RevokeAfterSelection=false;await service.Ask(Http(),SummaryInput());owners.SourceDenied=true;
        await Assert.ThrowsAsync<TenantBoundaryException>(()=>service.History(Http()));
    }
    [Theory]
    [InlineData("valid")][InlineData("date")][InlineData("currency")][InlineData("household")][InlineData("member")][InlineData("foreign")][InlineData("changed-input")][InlineData("denied")][InlineData("incomplete")]
    public async Task SummaryOwnerRequiresComparableCompleteExactInputsAndRevalidatesCitations(string mode)
    {
        using var fixture=new Fixture();fixture.Config["Monergy:ExecutionZone"]="CI_EPHEMERAL";
        fixture.Config["Monergy:AccessIntegration:Enabled"]="true";fixture.Config["Monergy:AccessIntegration:AccessManagementUrl"]="http://127.0.0.1:5088/";
        fixture.Config["Monergy:AccessIntegration:MembershipToken"]=new string('c',32);fixture.Config["Monergy:AccessIntegration:CustomerIdentityUrl"]="http://127.0.0.1:5101/";
        fixture.Config["Monergy:AccessIntegration:CustomerContextToken"]=new string('a',32);fixture.Config["Monergy:AccessIntegration:CustomerIdentityToken"]=new string('b',32);
        fixture.Config["Monergy:FinancialQa:FinancialProfileUrl"]="http://127.0.0.1:5113/";fixture.Config["Monergy:FinancialQa:FinancialRulesUrl"]="http://127.0.0.1:5114/";
        var http=Http();http.Request.Headers["X-Monergy-Tenant"]="T001";http.Request.Headers["X-Monergy-Session"]="session";var customer=await new Owners().Authorize(http);var calls=0;
        var netInput=new ReviewedNetWorthPosition("position-1",new string('a',32),"customer",new(new(2026,9,30),"USD",[],true,true),[],"actor",DateTimeOffset.UtcNow);
        var net=new NetWorthCalculation("net-result",new string('b',32),"customer",netInput,NetWorthDefinition.Id,NetWorthDefinition.Version,NetWorthDefinition.DefinitionHash,NetWorthDefinition.Semantics,"CALCULATED","0.00","0.00","0.00",[],"actor",DateTimeOffset.UtcNow);
        var line=new MonthlyCashflowLineDraft(new string('c',32),"INCOME","Salary","Monthly salary","0.00","USD",new(2026,9,30),"Reviewed zero",null,mode=="member"?"Other":"Self");
        var cashInput=new ReviewedMonthlyCashflowPosition("cash-position",new string('a',32),"customer",new(mode=="date"?new(2026,8,31):new(2026,9,30),mode=="currency"?"INR":"USD",[line],true,mode!="incomplete",null,mode=="household"?"Family":"Self"),[new(line,null)],"actor",DateTimeOffset.UtcNow);
        var cash=new MonthlyCashflowCalculation("cash-result",new string('b',32),mode=="foreign"?"other":"customer",cashInput,MonthlyCashflowDefinition.Id,MonthlyCashflowDefinition.Version,MonthlyCashflowDefinition.DefinitionHash,MonthlyCashflowDefinition.Semantics,mode=="incomplete"?"INSUFFICIENT_DATA":"CALCULATED",mode=="incomplete"?null:"0.00",mode=="incomplete"?null:"0.00",mode=="incomplete"?null:"0.00",mode=="incomplete"?["EXPENSE_INVENTORY_NOT_CONFIRMED"]:[],"actor",DateTimeOffset.UtcNow);
        using var identity=new CustomerOwnerClient(fixture.Config);var deny=mode=="denied";
        using var access=new TenantAccessClient(fixture.Config,new HttpClient(new Handler(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=System.Net.Http.Json.JsonContent.Create(customer.Decision with{CapabilityId="financial-rules.calculation.execute",Outcome=deny?"Deny":"Allow",EvaluatedAt=DateTimeOffset.UtcNow},options:ContractJson.Options)}))));
        using var owner=new QaOwners(identity,access,fixture.Config,new HttpClient(new Handler(request=>{
            calls++;Assert.Equal(HttpMethod.Get,request.Method);object value;var path=request.RequestUri!.AbsolutePath;
            if(path.Contains("/monthly-cashflow/",StringComparison.Ordinal))value=path.Contains("/positions/",StringComparison.Ordinal)?mode=="changed-input"?cashInput with{RecordedBy="changed"}:cashInput:cash;
            else value=path.Contains("/positions/",StringComparison.Ordinal)?netInput:net;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=System.Net.Http.Json.JsonContent.Create(value,options:ContractJson.Options)});
        })));
        if(mode=="valid") {var claims=await owner.Summary(http,customer,"net-result","cash-result");Assert.Equal(6,claims.Count);Assert.Equal(3,claims.Count(x=>x.Kind=="MonthlyCalculation"));Assert.All(claims,x=>Assert.Contains("0.00 USD",x.Text,StringComparison.Ordinal));await owner.Validate(http,customer,claims);deny=true;await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.Validate(http,customer,claims));}
        else if(mode=="incomplete")Assert.Equal("monthly-incomplete",Assert.Single(await owner.Summary(http,customer,"net-result","cash-result")).Id);
        else {await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.Summary(http,customer,"net-result","cash-result"));if(mode=="denied")Assert.Equal(0,calls);}
    }

    [Fact]
    public async Task SummaryProviderReceivesOnlyCanonicalDescriptionsAndRequiresCompleteSelectionInput()
    {
        using var fixture=new Fixture();var calls=0;
        using var adapter=new QaModelAdapter(fixture.Config,new HttpClient(new Handler(async request=>{
            calls++;var body=await request.Content!.ReadAsStringAsync();Assert.Contains("all six",body,StringComparison.Ordinal);Assert.Contains("Canonical result 5",body,StringComparison.Ordinal);Assert.DoesNotContain("net-result",body,StringComparison.Ordinal);Assert.DoesNotContain("cash-result",body,StringComparison.Ordinal);Assert.DoesNotContain("position-",body,StringComparison.Ordinal);
            var json=JsonSerializer.Serialize(new{model="pinned-model",choices=new[]{new{finish_reason="stop",message=new{content=JsonSerializer.Serialize(new{state="SOURCES",claimIds=SummaryClaims.Select(x=>x.Id)})}}}});
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)};
        })));
        var config=adapter.Configuration("T001");var input=new QaModelRequest("select","Summarize",config.Revision,true,SummaryClaims,"summary");
        var response=await adapter.GenerateAsync("T001",input,default);Assert.Equal(6,QaApplication.Select(response.Content,SummaryClaims).Count);Assert.Equal(1,calls);
        await Assert.ThrowsAsync<TenantBoundaryException>(()=>adapter.GenerateAsync("T001",input with{Claims=SummaryClaims.Take(5).ToArray()},default));
        await Assert.ThrowsAsync<TenantBoundaryException>(()=>adapter.GenerateAsync("T001",input with{Phase="plan"},default));Assert.Equal(1,calls);
    }

}
