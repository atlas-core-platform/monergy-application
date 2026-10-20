using System.Globalization;
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
using Monergy.Services.FinancialRules;
using Monergy.Services.FinancialRules.Application;
using Monergy.Services.FinancialRules.Domain;
using Monergy.Services.FinancialRules.Infrastructure;
using Monergy.UatGateway;
using Xunit;
namespace Monergy.FinancialProfile.Tests;
public sealed class NetWorthJourneyTests
{
    private const string Customer="reference-customer";
    private static readonly DateOnly Day=new(2026,10,1);
    private static NetWorthLineDraft Line(string side="ASSET",string? value="1000.10")=>new(Guid.NewGuid().ToString("N"),side,side=="ASSET"?"BANK_ACCOUNT":"LOAN",side=="ASSET"?"Savings":"Home loan",value,"INR",Day,"My reviewed statement");
    private static CreateNetWorthPosition Input()=>new(Guid.NewGuid().ToString("N"),new(Day,"INR",new[]{Line(),Line(value:"200.20"),Line("LIABILITY","900.99")},true,true));
    private static NetWorthPositionsApplication Positions(INetWorthPositionRepository? repository=null,IInvestmentRecordRepository? investments=null)=>new(repository??new InMemoryNetWorthPositions(),investments??new InMemoryInvestmentRecords(),new InMemoryPropertyRecords(),new InMemoryVehicleRecords(),new InMemoryLiabilityRecords(),TimeProvider.System);
    private static NetWorthRule Rule()=>new(new ConfigurationBuilder().Build());
    [Theory,InlineData("1000.10","900.99","99.11"),InlineData("0.00","0.00","0.00"),InlineData("10.10","90.20","-80.10"),InlineData("999999999999.99","0.01","999999999999.98")]
    public async Task SpecifiedFormulaPreservesExactCentsAndNegativeNetWorth(string asset,string liability,string expected)
    {
        var input=Input();input=input with {Position=input.Position with {Lines=new[]{Line(value:asset),Line("LIABILITY",liability)}}};var position=await Positions().CreateAsync("T001",Customer,"actor-001",input,default);var result=Rule().Evaluate(position);
        Assert.Equal("CALCULATED",result.State);Assert.Equal(asset,result.TotalAssets);Assert.Equal(liability,result.TotalLiabilities);Assert.Equal(expected,result.NetWorth);Assert.Empty(result.Issues);
    }
    [Fact]
    public async Task ExactCentsRemainStringsBeyondJavascriptSafeIntegerCentsAndZeroNeedsExplicitInventoryReview()
    {
        var input=Input();input=input with {Position=input.Position with {Lines=Enumerable.Range(0,200).Select(_=>Line(value:"999999999999.99")).ToArray()}};
        var result=Rule().Evaluate(await Positions().CreateAsync("T001",Customer,"actor-001",input,default));Assert.Equal("199999999999998.00",result.NetWorth);
        input=input with {OperationId=Guid.NewGuid().ToString("N"),Position=input.Position with {Lines=Array.Empty<NetWorthLineDraft>()}};
        result=Rule().Evaluate(await Positions().CreateAsync("T001",Customer,"actor-001",input,default));Assert.Equal("0.00",result.NetWorth);
        input=input with {Position=input.Position with {AssetsComplete=false}};result=Rule().Evaluate(await Positions().CreateAsync("T001",Customer,"actor-001",input,default));Assert.Null(result.NetWorth);Assert.Contains("ASSET_INVENTORY_NOT_CONFIRMED",result.Issues);
    }
    [Theory,InlineData("asset","ASSET_INVENTORY_NOT_CONFIRMED"),InlineData("liability","LIABILITY_INVENTORY_NOT_CONFIRMED"),InlineData("amount","MISSING_VALUE"),InlineData("date","MISSING_VALUE_DATE"),InlineData("mixed-date","MIXED_VALUE_DATES"),InlineData("currency","MIXED_CURRENCIES")]
    public async Task MissingAndIncomparableValuesNeverProducePartialTotals(string kind,string issue)
    {
        var input=Input();var lines=input.Position.Lines.ToArray();lines[0]=kind switch {"amount"=>lines[0] with {Amount=null},"date"=>lines[0] with {ValueDate=null},"mixed-date"=>lines[0] with {ValueDate=Day.AddDays(-1)},"currency"=>lines[0] with {Currency="USD"},_=>lines[0]};
        input=input with {Position=input.Position with {Lines=lines,AssetsComplete=kind!="asset",LiabilitiesComplete=kind!="liability"}};
        var result=Rule().Evaluate(await Positions().CreateAsync("T001",Customer,"actor-001",input,default));Assert.Equal("INSUFFICIENT_DATA",result.State);Assert.Contains(issue,result.Issues);Assert.Null(result.TotalAssets);Assert.Null(result.TotalLiabilities);Assert.Null(result.NetWorth);
    }
    [Theory,InlineData("operation"),InlineData("upper-operation"),InlineData("date"),InlineData("currency"),InlineData("name"),InlineData("control"),InlineData("side"),InlineData("kind"),InlineData("negative"),InlineData("precision"),InlineData("exponent"),InlineData("large-money"),InlineData("leading-zero"),InlineData("note"),InlineData("duplicate-line"),InlineData("too-many"),InlineData("source-version")]
    public async Task InvalidReviewedInventoriesCannotBeSaved(string kind)
    {
        var input=Input();var line=input.Position.Lines[0];line=kind switch {"name"=>line with {Name=" blank "},"control"=>line with {Name="a\nb"},"side"=>line with {Side="INCOME"},"kind"=>line with {Kind="GOAL"},"negative"=>line with {Amount="-1.00"},"precision"=>line with {Amount="1.001"},"exponent"=>line with {Amount="1e2"},"large-money"=>line with {Amount="1000000000000.00"},"leading-zero"=>line with {Amount="01.00"},"note"=>line with {SourceNote=""},"source-version"=>line with {Kind="INVESTMENT",Record=new("INVESTMENT","investment-a",0)},_=>line};
        input=input with {OperationId=kind=="operation"?"bad":kind=="upper-operation"?new string('A',32):input.OperationId,Position=input.Position with {AsOfDate=kind=="date"?new(2199,1,1):Day,Currency=kind=="currency"?"inr":"INR",Lines=kind=="duplicate-line"?new[]{line,line}:kind=="too-many"?Enumerable.Range(0,201).Select(_=>Line()).ToArray():new[]{line}}};
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>Positions().CreateAsync("T001",Customer,"actor-001",input,default))).Status);
    }
    [Fact]
    public async Task InventoriesAreImmutableOwnedVersionedReviewsWithStableReplayAndScopedHistory()
    {
        var repository=new InMemoryNetWorthPositions();var owner=Positions(repository);var input=Input();var first=await owner.CreateAsync("T001",Customer,"actor-001",input,default);
        var copy=await owner.ReadAsync("T001",Customer,first.PositionId,default);((IList<NetWorthLineDraft>)copy.Position.Lines)[0]=Line(value:"1.00");Assert.Equal("1000.10",(await owner.ReadAsync("T001",Customer,first.PositionId,default)).Position.Lines[0].Amount);
        var next=await owner.CreateAsync("T001",Customer,"actor-001",Input() with {Position=input.Position with {ReplacesPositionId=first.PositionId,Lines=new[]{Line(value:"2000.00")}}},default);
        Assert.True(NetWorthData.Same(first,await owner.CreateAsync("T001",Customer,"actor-001",input,default)));Assert.Equal(first.PositionId,next.Position.ReplacesPositionId);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.CreateAsync("T001",Customer,"actor-001",input with {Position=input.Position with {AssetsComplete=false}},default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.ReadAsync("T002",Customer,first.PositionId,default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.ReadAsync("T001","foreign",first.PositionId,default))).Status);
        for(var i=0;i<50;i++)await owner.CreateAsync("T001",Customer,"actor-001",Input(),default);
        var page=await owner.ListAsync("T001",Customer,null,default);Assert.Equal(50,page.Items.Count);Assert.True(page.HasMore);var tail=await owner.ListAsync("T001",Customer,page.NextBeforeId,default);Assert.Equal(2,tail.Items.Count);Assert.False(tail.HasMore);Assert.Null(tail.NextBeforeId);Assert.Empty(page.Items.Select(x=>x.PositionId).Intersect(tail.Items.Select(x=>x.PositionId)));
    }
    [Fact]
    public async Task LinkedRecordsResolveExactOwnedValuesAndPreserveHistoricalSourceVersions()
    {
        var investments=new InMemoryInvestmentRecords();var source=Investment("investment-a","fact-a");await investments.CreateAsync("T001",Customer,"actor-001","seed",source,default);
        var owner=Positions(investments:investments);var input=Linked(source);var first=await owner.CreateAsync("T001",Customer,"actor-001",input,default);
        Assert.Equal("fact-a",first.Lines[0].Source?.FinancialFactId);Assert.Equal("200.00",first.Lines[0].Line.Amount);
        await investments.ReviseAsync("T001",Customer,"actor-001","change",1,source with {OperationId=Guid.NewGuid().ToString("N"),Revision=2,Investment=source.Investment with {CurrentValue=400m}},default);
        Assert.True(NetWorthData.Same(first,await owner.ReadAsync("T001",Customer,first.PositionId,default)));Assert.Equal("200.00",Rule().Evaluate(first).NetWorth);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.CreateAsync("T001",Customer,"actor-001",input with {OperationId=Guid.NewGuid().ToString("N"),Position=input.Position with {Lines=new[]{input.Position.Lines[0] with {Amount="1.00"}}}},default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.CreateAsync("T002",Customer,"actor-001",input,default))).Status);
        var duplicate=Investment("investment-b","fact-a");await investments.CreateAsync("T001",Customer,"actor-001","second",duplicate,default);
        var repeated=input with {OperationId=Guid.NewGuid().ToString("N"),Position=input.Position with {Lines=input.Position.Lines.Concat(Linked(duplicate).Position.Lines).ToArray()}};
        var blocked=Rule().Evaluate(await owner.CreateAsync("T001",Customer,"actor-001",repeated,default));Assert.Contains("OVERLAPPING_SOURCE_RECORDS",blocked.Issues);Assert.Null(blocked.NetWorth);
    }
    [Fact]
    public void CentralRuleSelectionRejectsUnspecifiedFormulaOrVersion()
    {
        Assert.Throws<InvalidOperationException>(()=>new NetWorthRule(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Monergy:NetWorth:RuleVersion","2.0.0"}}).Build()));
        Assert.Throws<InvalidOperationException>(()=>new NetWorthRule(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Monergy:NetWorth:Formula","arbitrary"}}).Build()));Assert.Equal(64,NetWorthRule.DefinitionHash.Length);
    }
    [Fact]
    public async Task ActualThreeOwnerHttpJourneySavesCalculatesReopensExplainsAndUpdatesWithoutRewritingHistory()
    {
        await using var h=await Harness.Create();using var client=h.Client();var input=Input();var position=await Save(client,input);var request=new CalculateNetWorth(Guid.NewGuid().ToString("N"),position.PositionId);
        var response=await client.PostAsJsonAsync("/product-api/v1/net-worth/calculations",request,ContractJson.Options);Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("no-store",response.Headers.CacheControl?.ToString());var result=(await response.Content.ReadFromJsonAsync<NetWorthCalculation>(ContractJson.Options))!;
        Assert.Equal("299.31",result.NetWorth);Assert.Equal("1200.30",result.TotalAssets);Assert.Equal("900.99",result.TotalLiabilities);Assert.Equal(NetWorthRule.DefinitionHash,result.DefinitionHash);
        Assert.True(NetWorthData.Same(result,await client.GetFromJsonAsync<NetWorthCalculation>("/product-api/v1/net-worth/calculations/"+result.CalculationId,ContractJson.Options)));
        var next=await Save(client,input with {OperationId=Guid.NewGuid().ToString("N"),Position=input.Position with {ReplacesPositionId=position.PositionId,Lines=new[]{Line(value:"10.00"),Line("LIABILITY","100.00")}}});var updated=(await(await client.PostAsJsonAsync("/product-api/v1/net-worth/calculations",new CalculateNetWorth(Guid.NewGuid().ToString("N"),next.PositionId),ContractJson.Options)).Content.ReadFromJsonAsync<NetWorthCalculation>(ContractJson.Options))!;Assert.Equal("-90.00",updated.NetWorth);
        Assert.True(NetWorthData.Same(result,await(await client.PostAsJsonAsync("/product-api/v1/net-worth/calculations",request,ContractJson.Options)).Content.ReadFromJsonAsync<NetWorthCalculation>(ContractJson.Options)));
        var page=(await client.GetFromJsonAsync<NetWorthCalculationPage>("/product-api/v1/net-worth/calculations",ContractJson.Options))!;Assert.Equal(2,page.Items.Count);Assert.Equal(updated.CalculationId,page.Items[0].CalculationId);
        h.Authority.Denied="financial-profile.profile.update";Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/product-api/v1/net-worth/calculations/"+result.CalculationId)).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/product-api/v1/net-worth/positions",Input(),ContractJson.Options)).StatusCode);
    }
    [Theory,InlineData("financial-profile.profile.read"),InlineData("financial-profile.profile.update"),InlineData("advisor")]
    public async Task CurrentAuthorityRejectsInventoryWritesBeforeMutation(string denied)
    {
        await using var h=await Harness.Create();h.Authority.Denied=denied;using var client=h.Client();Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/product-api/v1/net-worth/positions",Input(),ContractJson.Options)).StatusCode);Assert.Empty((await h.Positions.ListAsync("T001",Customer,null,default)).Items);
    }
    [Theory,InlineData("financial-profile.profile.read"),InlineData("financial-rules.calculation.execute"),InlineData("advisor")]
    public async Task CalculationAndSavedResultReadsRevalidateCurrentAuthority(string denied)
    {
        await using var h=await Harness.Create();using var client=h.Client();var position=await Save(client,Input());h.Authority.Denied=denied;Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/product-api/v1/net-worth/calculations",new CalculateNetWorth(Guid.NewGuid().ToString("N"),position.PositionId),ContractJson.Options)).StatusCode);Assert.Empty((await h.Calculations.ListAsync("T001",Customer,null,default)).Items);
    }
    [Theory,InlineData("positions"),InlineData("calculations")]
    public async Task LostConfirmationReplaysTheSameRequestAfterPermissionReturns(string stage)
    {
        await using var h=await Harness.Create(stage+"-lost");using var client=h.Client();var input=Input();
        if(stage=="positions")
        {Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/product-api/v1/net-worth/positions",input,ContractJson.Options)).StatusCode);h.Authority.Denied="";h.Tamper.Enabled=false;var replay=await Save(client,input);Assert.Single((await h.Positions.ListAsync("T001",Customer,null,default)).Items);Assert.Equal(input.OperationId,replay.OperationId);}
        else
        {var position=await Save(client,input);var request=new CalculateNetWorth(Guid.NewGuid().ToString("N"),position.PositionId);Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/product-api/v1/net-worth/calculations",request,ContractJson.Options)).StatusCode);h.Authority.Denied="";h.Tamper.Enabled=false;var replay=await(await client.PostAsJsonAsync("/product-api/v1/net-worth/calculations",request,ContractJson.Options)).Content.ReadFromJsonAsync<NetWorthCalculation>(ContractJson.Options);Assert.Equal("299.31",replay?.NetWorth);Assert.Single((await h.Calculations.ListAsync("T001",Customer,null,default)).Items);}
    }
    [Theory,InlineData("positions","customer"),InlineData("positions","actor"),InlineData("positions","operation"),InlineData("positions","value"),InlineData("positions","readback"),InlineData("calculations","customer"),InlineData("calculations","actor"),InlineData("calculations","operation"),InlineData("calculations","value"),InlineData("calculations","rule"),InlineData("calculations","definition"),InlineData("calculations","partial"),InlineData("calculations","readback")]
    public async Task ProductRejectsForgedReceiptsAndExactReadbacks(string stage,string field)
    {
        await using var h=await Harness.Create(stage+"-"+field);using var client=h.Client();HttpResponseMessage response;
        if(stage=="positions")response=await client.PostAsJsonAsync("/product-api/v1/net-worth/positions",Input(),ContractJson.Options);
        else {var position=await Save(client,Input());response=await client.PostAsJsonAsync("/product-api/v1/net-worth/calculations",new CalculateNetWorth(Guid.NewGuid().ToString("N"),position.PositionId),ContractJson.Options);}
        Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
    }
    [Theory,InlineData(true,"unknown"),InlineData(false,"unknown"),InlineData(true,"missing"),InlineData(false,"missing"),InlineData(true,"null"),InlineData(false,"null"),InlineData(true,"duplicate"),InlineData(false,"duplicate"),InlineData(true,"large"),InlineData(false,"large")]
    public async Task OwnerAndProductRejectAmbiguousInventoryBodies(bool product,string kind)
    {
        await using var h=await Harness.Create();using var client=product?h.Client():h.ProfileClient();var json=JsonNode.Parse(JsonSerializer.Serialize(Input(),ContractJson.Options))!.AsObject();if(kind=="unknown")json["customerId"]="foreign";if(kind=="missing")json["position"]!.AsObject().Remove("assetsComplete");if(kind=="null")json["position"]!["lines"]=null;if(kind=="large")json["position"]!["lines"]![0]!["sourceNote"]=new string('a',300000);var body=json.ToJsonString();if(kind=="duplicate")body=body.Insert(1,"\"operationId\":\""+Guid.NewGuid().ToString("N")+"\",");
        var path=product?"/product-api/v1/net-worth/positions":"/local/v1/customers/"+Customer+"/net-worth/positions";Assert.Equal(kind=="large"?413:400,(int)(await client.PostAsync(path,new StringContent(body,Encoding.UTF8,"application/json"))).StatusCode);Assert.Empty((await h.Positions.ListAsync("T001",Customer,null,default)).Items);
    }
    [Fact]
    public async Task LinkedHistoryRequiresCurrentDocumentAccessAndOwnerRejectsTenantOrCustomerSubstitution()
    {
        await using var h=await Harness.Create();var investment=Investment("investment-a","fact-a");await h.Investments.CreateAsync("T001",Customer,"actor-001","seed",investment,default);using var client=h.Client();var position=await Save(client,Linked(investment));var result=(await(await client.PostAsJsonAsync("/product-api/v1/net-worth/calculations",new CalculateNetWorth(Guid.NewGuid().ToString("N"),position.PositionId),ContractJson.Options)).Content.ReadFromJsonAsync<NetWorthCalculation>(ContractJson.Options))!;
        h.Authority.Denied="evidence.document.read";Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/product-api/v1/net-worth/calculations/"+result.CalculationId)).StatusCode);h.Authority.Denied="";
        using var foreign=h.ProfileClient("T002");Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync("/local/v1/customers/"+Customer+"/net-worth/positions/"+position.PositionId)).StatusCode);Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/product-api/v1/net-worth/positions?customerId=foreign")).StatusCode);
    }
    [Fact]
    public async Task AllReviewedOwnerKindsRetainExactDatedCurrentValuesAndSourceLineage()
    {
        var investments=new InMemoryInvestmentRecords();var properties=new InMemoryPropertyRecords();var vehicles=new InMemoryVehicleRecords();var liabilities=new InMemoryLiabilityRecords();
        var investment=Investment("investment-a","fact-investment");await investments.CreateAsync("T001",Customer,"actor-001","investment",investment,default);
        var property=new ReviewedPropertyRecord("property-a",Guid.NewGuid().ToString("N"),Customer,new("Home","Location","Home",100m,500m,Day,"fact-property",1),"INR","provenance-property","version-property","evidence-property","actor-001",DateTimeOffset.UtcNow);await properties.CreateAsync("T001",Customer,"actor-001","property",property,default);
        var vehicle=new ReviewedVehicleRecord("vehicle-a",Guid.NewGuid().ToString("N"),Customer,new("Car",null,"Car",100m,300m,Day,"fact-vehicle",1),"INR","provenance-vehicle","version-vehicle","evidence-vehicle","actor-001",DateTimeOffset.UtcNow);await vehicles.CreateAsync("T001",Customer,"actor-001","vehicle",vehicle,default);
        var liability=new ReviewedLiabilityRecord("liability-a",Guid.NewGuid().ToString("N"),Customer,new("Debt","Creditor",100m,Day,"fact-liability",1),"INR","provenance-liability","version-liability","evidence-liability","actor-001",DateTimeOffset.UtcNow,"CUSTOMER_REVIEWED");await liabilities.CreateAsync("T001",Customer,"actor-001","liability",liability,default);
        var lines=new[]{Linked(investment).Position.Lines[0],new NetWorthLineDraft(Guid.NewGuid().ToString("N"),"ASSET","PROPERTY","Home","500.00","INR",Day,"Saved record",new("PROPERTY","property-a",1)),new(Guid.NewGuid().ToString("N"),"ASSET","VEHICLE","Car","300.00","INR",Day,"Saved record",new("VEHICLE","vehicle-a",1)),new(Guid.NewGuid().ToString("N"),"LIABILITY","LOAN","Debt","100.00","INR",Day,"Saved record",new("LIABILITY","liability-a",1))};
        var owner=new NetWorthPositionsApplication(new InMemoryNetWorthPositions(),investments,properties,vehicles,liabilities,TimeProvider.System);var position=await owner.CreateAsync("T001",Customer,"actor-001",new(Guid.NewGuid().ToString("N"),new(Day,"INR",lines,true,true)),default);
        Assert.All(position.Lines,x=>Assert.NotNull(x.Source));var value=Rule().Evaluate(position);Assert.Equal("1000.00",value.TotalAssets);Assert.Equal("100.00",value.TotalLiabilities);Assert.Equal("900.00",value.NetWorth);
    }
    [Fact]
    public async Task CalculationHistoryPaginatesWithoutOverlapAndOperationReuseCannotChangeReviewedInventory()
    {
        var positions=Positions();var source=await positions.CreateAsync("T001",Customer,"actor-001",Input(),default);var repository=new InMemoryNetWorthCalculations();var owner=new NetWorthCalculationsApplication(new LocalReader(positions),repository,Rule(),TimeProvider.System);var input=new CalculateNetWorth(Guid.NewGuid().ToString("N"),source.PositionId);var first=await owner.CalculateAsync("T001","session-001",Customer,"actor-001",input,default);
        ((IList<NetWorthLineDraft>)first.Position.Position.Lines)[0]=Line(value:"1.00");Assert.Equal("1000.10",(await owner.ReadAsync("T001","session-001",Customer,first.CalculationId,default)).Position.Position.Lines[0].Amount);
        for(var n=0;n<50;n++)await owner.CalculateAsync("T001","session-001",Customer,"actor-001",new(Guid.NewGuid().ToString("N"),source.PositionId),default);
        var page=await owner.ListAsync("T001","session-001",Customer,null,default);Assert.Equal(50,page.Items.Count);Assert.True(page.HasMore);var tail=await owner.ListAsync("T001","session-001",Customer,page.NextBeforeId,default);Assert.Single(tail.Items);Assert.Empty(page.Items.Select(x=>x.CalculationId).Intersect(tail.Items.Select(x=>x.CalculationId)));
        var other=await positions.CreateAsync("T001",Customer,"actor-001",Input(),default);Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.CalculateAsync("T001","session-001",Customer,"actor-001",input with {PositionId=other.PositionId},default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.ListAsync("T002","session-001",Customer,page.NextBeforeId,default))).Status);
    }
    [Theory,InlineData("positions"),InlineData("calculations")]
    public async Task UnknownQueryFieldsAndCrossOriginRequestsDoNotCrossCustomerBoundaries(string route)
    {
        await using var h=await Harness.Create();using var client=h.Client();Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/product-api/v1/net-worth/"+route+"?customerId=foreign")).StatusCode);
        client.DefaultRequestHeaders.Add("Origin","https://foreign.invalid");Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/product-api/v1/net-worth/positions",Input(),ContractJson.Options)).StatusCode);Assert.Empty((await h.Positions.ListAsync("T001",Customer,null,default)).Items);
    }
    private sealed class LocalReader(NetWorthPositionsApplication owner):INetWorthPositionReader
    {public Task<ReviewedNetWorthPosition> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)=>owner.ReadAsync(tenant,customer,id,ct);}
    private static ReviewedInvestmentRecord Investment(string id,string source)=>new(id,Guid.NewGuid().ToString("N"),Customer,new("Fund","Institution","Fund",100m,200m,Day,source,1),"INR","provenance-"+source,"version-"+source,"evidence-"+source,"actor-001",DateTimeOffset.UtcNow);
    private static CreateNetWorthPosition Linked(ReviewedInvestmentRecord value)=>new(Guid.NewGuid().ToString("N"),new(Day,"INR",new[]{new NetWorthLineDraft(Guid.NewGuid().ToString("N"),"ASSET","INVESTMENT",value.Investment.Name,value.Investment.CurrentValue.ToString("F2",CultureInfo.InvariantCulture),value.Currency,Day,"Reviewed saved record",new("INVESTMENT",value.InvestmentRecordId,value.Revision))},true,true));
    private static async Task<ReviewedNetWorthPosition> Save(HttpClient client,CreateNetWorthPosition input)
    {var response=await client.PostAsJsonAsync("/product-api/v1/net-worth/positions",input,ContractJson.Options);Assert.Equal(HttpStatusCode.OK,response.StatusCode);return(await response.Content.ReadFromJsonAsync<ReviewedNetWorthPosition>(ContractJson.Options))!;}
    private sealed class Authority:HttpMessageHandler
    {
        public string Denied{get;set;}="";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;
            if(path=="/v1/authorization/customer-evaluations")
            {var input=(await request.Content!.ReadFromJsonAsync<CustomerAccessRequest>(cancellationToken:ct))!;var tenant=request.Headers.GetValues("X-Monergy-Tenant").Single();var denied=Denied=="all"||Denied==input.CapabilityId||tenant!="T001"||input.CustomerId!=Customer;return Reply(new CustomerAccessDecision("decision-001",denied?"Deny":"Allow",tenant,"actor-001",input.CustomerId,input.CapabilityId,input.Purpose,denied?"Denied":"Granted",Denied=="advisor"?"Advisor":"Self",1,2,1,1,DateTimeOffset.UtcNow));}
            if(path=="/internal/v1/tenant-sessions/validate")return Reply(new CustomerOwnerSession("T001","actor-001","session-001",2,DateTimeOffset.UtcNow.AddHours(1)));
            if(path=="/internal/v1/customer-owner/actor-001")return Reply(new CustomerOwnerWorkspace("T001",Customer,"Customer",null,"actor-001",1,true));
            throw new InvalidOperationException("Unexpected authority route.");
        }
        private static HttpResponseMessage Reply<T>(T value)=>new(HttpStatusCode.OK){Content=JsonContent.Create(value)};
    }
    private sealed class Tamper(string? scenario,Authority authority):DelegatingHandler(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false})
    {
        public bool Enabled{get;set;}=true;private bool wrote;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var response=await base.SendAsync(request,ct);if(!Enabled||scenario is null||!response.IsSuccessStatusCode)return response;var parts=scenario.Split('-',2);var stage=parts[0];var field=parts[1];var path=request.RequestUri!.AbsolutePath;
            if(!path.Contains("/"+stage,StringComparison.Ordinal))return response;
            if(request.Method==HttpMethod.Post){wrote=true;if(field=="lost"){authority.Denied="all";return response;}}
            if(field=="lost"||field=="readback"?(field!="readback"||!wrote||request.Method!=HttpMethod.Get):request.Method!=HttpMethod.Post)return response;
            var json=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();switch(field){case "customer":json["customerId"]="foreign";break;case "actor":json["recordedBy"]="foreign";break;case "operation":json["operationId"]=new string('f',32);break;case "rule":json["ruleVersion"]="2.0.0";break;case "definition":json["definitionHash"]=new string('f',64);break;case "partial":json["state"]="INSUFFICIENT_DATA";break;default:if(stage=="positions")json["position"]!["assetsComplete"]=false;else json["netWorth"]="1.00";break;}
            response.Content.Dispose();response.Content=new StringContent(json.ToJsonString(),Encoding.UTF8,"application/json");return response;
        }
    }
    private sealed class Harness(WebApplication profile,WebApplication rules,WebApplication gateway,Authority authority,InMemoryNetWorthPositions positions,InMemoryNetWorthCalculations calculations,InMemoryInvestmentRecords investments,Tamper tamper):IAsyncDisposable
    {
        public Authority Authority=>authority;public InMemoryNetWorthPositions Positions=>positions;public InMemoryNetWorthCalculations Calculations=>calculations;public InMemoryInvestmentRecords Investments=>investments;public Tamper Tamper=>tamper;
        public static async Task<Harness> Create(string? scenario=null)
        {
            var authority=new Authority();var positions=new InMemoryNetWorthPositions();var calculations=new InMemoryNetWorthCalculations();var investments=new InMemoryInvestmentRecords();var values=new Dictionary<string,string?>{["Monergy:ExecutionZone"]="CI_EPHEMERAL",["Monergy:AccessIntegration:Enabled"]="true",["Monergy:AccessIntegration:CustomerIdentityUrl"]="http://127.0.0.1:5001/",["Monergy:AccessIntegration:AccessManagementUrl"]="http://127.0.0.1:5002/",["Monergy:AccessIntegration:CustomerContextToken"]=new('a',32),["Monergy:AccessIntegration:CustomerIdentityToken"]=new('b',32),["Monergy:AccessIntegration:MembershipToken"]=new('c',32),["Monergy:Product:Enabled"]="true"};
            var pb=Builder(values,authority,"financial-profile");pb.Services.AddSingleton(PositionsOwner(positions,investments));var profile=pb.Build();profile.MapNetWorthPositions();await profile.StartAsync();values["Monergy:NetWorth:FinancialProfileUrl"]=profile.Urls.Single()+"/";values["Monergy:Product:FinancialProfileUrl"]=profile.Urls.Single()+"/";
            var rb=Builder(values,authority,"financial-rules");rb.Services.AddSingleton(new NetWorthCalculationsApplication(new NetWorthPositionClient(rb.Configuration),calculations,new NetWorthRule(rb.Configuration),TimeProvider.System));var rules=rb.Build();rules.MapNetWorthCalculations();await rules.StartAsync();values["Monergy:Product:FinancialRulesUrl"]=rules.Urls.Single()+"/";
            var gb=Builder(values,authority,"gateway");gb.Services.AddSingleton(new CustomerOwnerClient(gb.Configuration,new HttpClient(authority,false)));var tamper=new Tamper(scenario,authority);gb.Services.AddSingleton(new ProductNetWorthClient(gb.Configuration,new HttpClient(tamper)));var gateway=gb.Build();gateway.MapProductNetWorth();await gateway.StartAsync();return new(profile,rules,gateway,authority,positions,calculations,investments,tamper);
        }
        private static NetWorthPositionsApplication PositionsOwner(InMemoryNetWorthPositions positions,InMemoryInvestmentRecords investments)=>new(positions,investments,new InMemoryPropertyRecords(),new InMemoryVehicleRecords(),new InMemoryLiabilityRecords(),TimeProvider.System);
        private static WebApplicationBuilder Builder(Dictionary<string,string?> values,Authority authority,string service)
        {var b=WebApplication.CreateSlimBuilder();b.WebHost.UseUrls("http://127.0.0.1:0");b.Configuration.AddInMemoryCollection(values);b.Services.AddSingleton(new TenantAccessClient(b.Configuration,new HttpClient(authority,false)));b.Services.AddSingleton(new TenantBoundaryOptions{Service=service,CapabilityPrefix=service+".",CustomerTenants=new Dictionary<string,string>{{Customer,"T001"}},ProbeUrl="http://127.0.0.1:5199/"});return b;}
        public HttpClient Client()=>ClientFor(gateway,"T001");public HttpClient ProfileClient(string tenant="T001")=>ClientFor(profile,tenant);
        private static HttpClient ClientFor(WebApplication app,string tenant){var c=new HttpClient{BaseAddress=new Uri(app.Urls.Single())};c.DefaultRequestHeaders.Add("X-Monergy-Tenant",tenant);c.DefaultRequestHeaders.Add("X-Monergy-Session","session-001");return c;}
        public async ValueTask DisposeAsync(){await gateway.DisposeAsync();await rules.DisposeAsync();await profile.DisposeAsync();authority.Dispose();}
    }
}
