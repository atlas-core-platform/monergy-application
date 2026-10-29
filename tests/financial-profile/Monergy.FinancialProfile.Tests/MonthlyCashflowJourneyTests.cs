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
public sealed class MonthlyCashflowJourneyTests
{
    private const string Customer="reference-customer";
    private static readonly DateOnly Day=new(2026,9,30);
    private static MonthlyCashflowLineDraft Line(string side="INCOME",string? value="1000.10")=>new(Guid.NewGuid().ToString("N"),side,side=="INCOME"?"BANK_ACCOUNT":"LOAN",Guid.NewGuid().ToString("N"),value,"INR",Day,"My reviewed statement");
    private static CreateMonthlyCashflowPosition Input()=>new(Guid.NewGuid().ToString("N"),new(Day,"INR",new[]{Line(),Line(value:"200.20"),Line("EXPENSE","900.99")},true,true));
    private static MonthlyCashflowPositionsApplication Positions(IMonthlyCashflowPositionRepository? repository=null,IIncomeRecordRepository? incomes=null)=>new(repository??new InMemoryMonthlyCashflowPositions(),incomes??new InMemoryIncomeRecords(),new InMemoryExpenseRecords(),TimeProvider.System);
    private static MonthlyCashflowRule Rule()=>new(new ConfigurationBuilder().Build());
    [Theory,InlineData("1000.10","900.99","99.11"),InlineData("0.00","0.00","0.00"),InlineData("10.10","90.20","-80.10"),InlineData("999999999999.99","0.01","999999999999.98")]
    public async Task SpecifiedFormulaPreservesExactCentsAndNegativeMonthlyCashflow(string asset,string liability,string expected)
    {
        var input=Input();input=input with {Position=input.Position with {Lines=new[]{Line(value:asset),Line("EXPENSE",liability)}}};var position=await Positions().CreateAsync("T001",Customer,"actor-001",input,default);var result=Rule().Evaluate(position);
        Assert.Equal("CALCULATED",result.State);Assert.Equal(asset,result.TotalIncome);Assert.Equal(liability,result.TotalExpenses);Assert.Equal(expected,result.Balance);Assert.Empty(result.Issues);
    }
    [Fact]
    public async Task ExactCentsRemainStringsBeyondJavascriptSafeIntegerCentsAndZeroNeedsExplicitInventoryReview()
    {
        var input=Input();input=input with {Position=input.Position with {Lines=Enumerable.Range(0,200).Select(_=>Line(value:"999999999999.99")).ToArray()}};
        var result=Rule().Evaluate(await Positions().CreateAsync("T001",Customer,"actor-001",input,default));Assert.Equal("199999999999998.00",result.Balance);
        input=input with {OperationId=Guid.NewGuid().ToString("N"),Position=input.Position with {Lines=Array.Empty<MonthlyCashflowLineDraft>()}};
        result=Rule().Evaluate(await Positions().CreateAsync("T001",Customer,"actor-001",input,default));Assert.Equal("0.00",result.Balance);
        input=input with {Position=input.Position with {IncomeComplete=false}};result=Rule().Evaluate(await Positions().CreateAsync("T001",Customer,"actor-001",input,default));Assert.Null(result.Balance);Assert.Contains("INCOME_INVENTORY_NOT_CONFIRMED",result.Issues);
    }
    [Theory,InlineData("asset","INCOME_INVENTORY_NOT_CONFIRMED"),InlineData("liability","EXPENSE_INVENTORY_NOT_CONFIRMED"),InlineData("amount","MISSING_VALUE"),InlineData("date","MISSING_VALUE_DATE"),InlineData("mixed-date","MIXED_VALUE_DATES"),InlineData("currency","MIXED_CURRENCIES")]
    public async Task MissingAndIncomparableValuesNeverProducePartialTotals(string kind,string issue)
    {
        var input=Input();var lines=input.Position.Lines.ToArray();lines[0]=kind switch {"amount"=>lines[0] with {Amount=null},"date"=>lines[0] with {ValueDate=null},"mixed-date"=>lines[0] with {ValueDate=Day.AddDays(-1)},"currency"=>lines[0] with {Currency="USD"},_=>lines[0]};
        input=input with {Position=input.Position with {Lines=lines,IncomeComplete=kind!="asset",ExpensesComplete=kind!="liability"}};
        var result=Rule().Evaluate(await Positions().CreateAsync("T001",Customer,"actor-001",input,default));Assert.Equal("INSUFFICIENT_DATA",result.State);Assert.Contains(issue,result.Issues);Assert.Null(result.TotalIncome);Assert.Null(result.TotalExpenses);Assert.Null(result.Balance);
    }
    [Theory,InlineData("operation"),InlineData("upper-operation"),InlineData("date"),InlineData("currency"),InlineData("name"),InlineData("control"),InlineData("side"),InlineData("kind"),InlineData("negative"),InlineData("precision"),InlineData("exponent"),InlineData("large-money"),InlineData("leading-zero"),InlineData("note"),InlineData("duplicate-line"),InlineData("too-many"),InlineData("source-version")]
    public async Task InvalidReviewedInventoriesCannotBeSaved(string kind)
    {
        var input=Input();var line=input.Position.Lines[0];line=kind switch {"name"=>line with {Name=" blank "},"control"=>line with {Name="a\nb"},"side"=>line with {Side="ASSET"},"kind"=>line with {Kind=""},"negative"=>line with {Amount="-1.00"},"precision"=>line with {Amount="1.001"},"exponent"=>line with {Amount="1e2"},"large-money"=>line with {Amount="1000000000000.00"},"leading-zero"=>line with {Amount="01.00"},"note"=>line with {SourceNote=""},"source-version"=>line with {Kind="Salary",Record=new("INCOME","income-a",0)},_=>line};
        input=input with {OperationId=kind=="operation"?"bad":kind=="upper-operation"?new string('A',32):input.OperationId,Position=input.Position with {AsOfDate=kind=="date"?new(2199,1,1):Day,Currency=kind=="currency"?"inr":"INR",Lines=kind=="duplicate-line"?new[]{line,line}:kind=="too-many"?Enumerable.Range(0,201).Select(_=>Line()).ToArray():new[]{line}}};
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>Positions().CreateAsync("T001",Customer,"actor-001",input,default))).Status);
    }
    [Fact]
    public async Task InventoriesAreImmutableOwnedVersionedReviewsWithStableReplayAndScopedHistory()
    {
        var repository=new InMemoryMonthlyCashflowPositions();var owner=Positions(repository);var input=Input();var first=await owner.CreateAsync("T001",Customer,"actor-001",input,default);
        var copy=await owner.ReadAsync("T001",Customer,first.PositionId,default);((IList<MonthlyCashflowLineDraft>)copy.Position.Lines)[0]=Line(value:"1.00");Assert.Equal("1000.10",(await owner.ReadAsync("T001",Customer,first.PositionId,default)).Position.Lines[0].Amount);
        var next=await owner.CreateAsync("T001",Customer,"actor-001",Input() with {Position=input.Position with {ReplacesPositionId=first.PositionId,Lines=new[]{Line(value:"2000.00")}}},default);
        Assert.True(MonthlyCashflowData.Same(first,await owner.CreateAsync("T001",Customer,"actor-001",input,default)));Assert.Equal(first.PositionId,next.Position.ReplacesPositionId);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.CreateAsync("T001",Customer,"actor-001",input with {Position=input.Position with {IncomeComplete=false}},default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.ReadAsync("T002",Customer,first.PositionId,default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.ReadAsync("T001","foreign",first.PositionId,default))).Status);
        for(var i=0;i<50;i++)await owner.CreateAsync("T001",Customer,"actor-001",Input(),default);
        var page=await owner.ListAsync("T001",Customer,null,default);Assert.Equal(50,page.Items.Count);Assert.True(page.HasMore);var tail=await owner.ListAsync("T001",Customer,page.NextBeforeId,default);Assert.Equal(2,tail.Items.Count);Assert.False(tail.HasMore);Assert.Null(tail.NextBeforeId);Assert.Empty(page.Items.Select(x=>x.PositionId).Intersect(tail.Items.Select(x=>x.PositionId)));
    }
    [Fact]
    public async Task LinkedRecordsResolveExactOwnedValuesAndPreserveHistoricalSourceVersions()
    {
        var incomes=new InMemoryIncomeRecords();var source=Income("income-a","fact-a");await incomes.CreateAsync("T001",Customer,"actor-001","seed",source,default);
        var owner=Positions(incomes:incomes);var input=Linked(source);var first=await owner.CreateAsync("T001",Customer,"actor-001",input,default);
        Assert.Equal("fact-a",first.Lines[0].Source?.FinancialFactId);Assert.Equal("200.00",first.Lines[0].Line.Amount);
        await incomes.ReviseAsync("T001",Customer,"actor-001","change",1,source with {OperationId=Guid.NewGuid().ToString("N"),Revision=2,Income=source.Income with {Amount=400m}},default);
        Assert.True(MonthlyCashflowData.Same(first,await owner.ReadAsync("T001",Customer,first.PositionId,default)));Assert.Equal("200.00",Rule().Evaluate(first).Balance);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.CreateAsync("T001",Customer,"actor-001",input with {OperationId=Guid.NewGuid().ToString("N"),Position=input.Position with {Lines=new[]{input.Position.Lines[0] with {Amount="1.00"}}}},default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.CreateAsync("T002",Customer,"actor-001",input,default))).Status);
        var duplicate=Income("income-b","fact-a");await incomes.CreateAsync("T001",Customer,"actor-001","second",duplicate,default);
        var repeated=input with {OperationId=Guid.NewGuid().ToString("N"),Position=input.Position with {Lines=input.Position.Lines.Concat(Linked(duplicate).Position.Lines).ToArray()}};
        var blocked=Rule().Evaluate(await owner.CreateAsync("T001",Customer,"actor-001",repeated,default));Assert.Contains("OVERLAPPING_SOURCE_RECORDS",blocked.Issues);Assert.Null(blocked.Balance);
    }
    [Fact]
    public void CentralRuleSelectionRejectsUnspecifiedFormulaOrVersion()
    {
        Assert.Throws<InvalidOperationException>(()=>new MonthlyCashflowRule(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Monergy:MonthlyCashflow:RuleVersion","2.0.0"}}).Build()));Assert.Equal(64,MonthlyCashflowRule.DefinitionHash.Length);
    }
    [Fact]
    public async Task ActualThreeOwnerHttpJourneySavesCalculatesReopensExplainsAndUpdatesWithoutRewritingHistory()
    {
        await using var h=await Harness.Create();using var client=h.Client();var input=Input();var position=await Save(client,input);var request=new CalculateMonthlyCashflow(Guid.NewGuid().ToString("N"),position.PositionId);
        var response=await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/calculations",request,ContractJson.Options);Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("no-store",response.Headers.CacheControl?.ToString());var result=(await response.Content.ReadFromJsonAsync<MonthlyCashflowCalculation>(ContractJson.Options))!;
        Assert.Equal("299.31",result.Balance);Assert.Equal("1200.30",result.TotalIncome);Assert.Equal("900.99",result.TotalExpenses);Assert.Equal(MonthlyCashflowRule.DefinitionHash,result.DefinitionHash);
        Assert.True(MonthlyCashflowData.Same(result,await client.GetFromJsonAsync<MonthlyCashflowCalculation>("/product-api/v1/monthly-cashflow/calculations/"+result.CalculationId,ContractJson.Options)));
        var next=await Save(client,input with {OperationId=Guid.NewGuid().ToString("N"),Position=input.Position with {ReplacesPositionId=position.PositionId,Lines=new[]{Line(value:"10.00"),Line("EXPENSE","100.00")}}});var updated=(await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/calculations",new CalculateMonthlyCashflow(Guid.NewGuid().ToString("N"),next.PositionId),ContractJson.Options)).Content.ReadFromJsonAsync<MonthlyCashflowCalculation>(ContractJson.Options))!;Assert.Equal("-90.00",updated.Balance);
        Assert.True(MonthlyCashflowData.Same(result,await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/calculations",request,ContractJson.Options)).Content.ReadFromJsonAsync<MonthlyCashflowCalculation>(ContractJson.Options)));
        var page=(await client.GetFromJsonAsync<MonthlyCashflowCalculationPage>("/product-api/v1/monthly-cashflow/calculations",ContractJson.Options))!;Assert.Equal(2,page.Items.Count);Assert.Equal(updated.CalculationId,page.Items[0].CalculationId);
        h.Authority.Denied="financial-profile.profile.update";Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/product-api/v1/monthly-cashflow/calculations/"+result.CalculationId)).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/positions",Input(),ContractJson.Options)).StatusCode);
    }
    [Fact]
    public async Task ExactSavedMonthlyReviewProducesImmutableCustomerReviewedDebtServiceRatio()
    {
        await using var h=await Harness.Create();using var client=h.Client();var position=await Save(client,Input());
        var source=(await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/calculations",new CalculateMonthlyCashflow(Guid.NewGuid().ToString("N"),position.PositionId),ContractJson.Options)).Content.ReadFromJsonAsync<MonthlyCashflowCalculation>(ContractJson.Options))!;
        var debt=source.Position.Position.Lines.Single(x=>x.Side=="EXPENSE");var request=new CalculateDebtServiceRatio(Guid.NewGuid().ToString("N"),source.CalculationId,new[]{debt.LineId},true);
        var response=await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/debt-service-ratios",request,ContractJson.Options);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var result=(await response.Content.ReadFromJsonAsync<DebtServiceRatioCalculation>(ContractJson.Options))!;Assert.Equal("1200.30",result.MonthlyIncome);Assert.Equal("900.99",result.MonthlyDebtPayments);Assert.Equal("75.06",result.RatioPercent);Assert.Equal(DebtServiceRatioDefinition.DefinitionHash,result.DefinitionHash);
        Assert.True(MonthlyCashflowData.Same(result,await client.GetFromJsonAsync<DebtServiceRatioCalculation>("/product-api/v1/monthly-cashflow/debt-service-ratios/"+result.CalculationId,ContractJson.Options)));
        Assert.True(MonthlyCashflowData.Same(result,await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/debt-service-ratios",request,ContractJson.Options)).Content.ReadFromJsonAsync<DebtServiceRatioCalculation>(ContractJson.Options)));
        var page=(await client.GetFromJsonAsync<DebtServiceRatioCalculationPage>("/product-api/v1/monthly-cashflow/debt-service-ratios",ContractJson.Options))!;Assert.Single(page.Items);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/debt-service-ratios",request with{OperationId=Guid.NewGuid().ToString("N"),DebtPaymentLineIds=new[]{source.Position.Position.Lines.First(x=>x.Side=="INCOME").LineId}},ContractJson.Options)).StatusCode);
        h.Authority.Denied="financial-rules.calculation.execute";Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/product-api/v1/monthly-cashflow/debt-service-ratios/"+result.CalculationId)).StatusCode);
    }
    [Fact]
    public async Task DebtServiceRatioDoesNotInventAResultForUnconfirmedDebtOrZeroIncome()
    {
        await using var h=await Harness.Create();using var client=h.Client();var input=Input();input=input with{Position=input.Position with{Lines=new[]{Line(value:"0.00"),Line("EXPENSE","25.00")}}};var position=await Save(client,input);
        var source=(await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/calculations",new CalculateMonthlyCashflow(Guid.NewGuid().ToString("N"),position.PositionId),ContractJson.Options)).Content.ReadFromJsonAsync<MonthlyCashflowCalculation>(ContractJson.Options))!;var debt=source.Position.Position.Lines.Single(x=>x.Side=="EXPENSE");
        var result=(await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/debt-service-ratios",new CalculateDebtServiceRatio(Guid.NewGuid().ToString("N"),source.CalculationId,new[]{debt.LineId},false),ContractJson.Options)).Content.ReadFromJsonAsync<DebtServiceRatioCalculation>(ContractJson.Options))!;
        Assert.Equal("INSUFFICIENT_DATA",result.State);Assert.Contains("DEBT_PAYMENTS_NOT_CONFIRMED",result.Issues);Assert.Contains("MONTHLY_INCOME_ZERO",result.Issues);Assert.Null(result.RatioPercent);
    }
    [Fact]
    public async Task ExactDebtReceiptAndGrossIncomeConfirmationProduceImmutableCustomerReviewedMonthlyDti()
    {
        await using var h=await Harness.Create();using var client=h.Client();var position=await Save(client,Input());
        var cashflow=(await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/calculations",new CalculateMonthlyCashflow(Guid.NewGuid().ToString("N"),position.PositionId),ContractJson.Options)).Content.ReadFromJsonAsync<MonthlyCashflowCalculation>(ContractJson.Options))!;
        var debt=cashflow.Position.Position.Lines.Single(x=>x.Side=="EXPENSE");
        var ratio=(await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/debt-service-ratios",new CalculateDebtServiceRatio(Guid.NewGuid().ToString("N"),cashflow.CalculationId,new[]{debt.LineId},true),ContractJson.Options)).Content.ReadFromJsonAsync<DebtServiceRatioCalculation>(ContractJson.Options))!;
        var request=new CalculateDebtToIncome(Guid.NewGuid().ToString("N"),ratio.CalculationId,true);
        var response=await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/debt-to-income-ratios",request,ContractJson.Options);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var result=(await response.Content.ReadFromJsonAsync<DebtToIncomeCalculation>(ContractJson.Options))!;
        Assert.Equal("900.99",result.MonthlyDebtObligations);Assert.Equal("1200.30",result.GrossMonthlyIncome);Assert.Equal("75.06",result.RatioPercent);Assert.Equal("MONTHLY",result.TimeBasis);Assert.Equal("GROSS_INCOME",result.IncomeBasis);Assert.Equal(DebtToIncomeDefinition.DefinitionHash,result.DefinitionHash);
        Assert.True(MonthlyCashflowData.Same(result,await client.GetFromJsonAsync<DebtToIncomeCalculation>("/product-api/v1/monthly-cashflow/debt-to-income-ratios/"+result.CalculationId,ContractJson.Options)));
        Assert.True(MonthlyCashflowData.Same(result,await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/debt-to-income-ratios",request,ContractJson.Options)).Content.ReadFromJsonAsync<DebtToIncomeCalculation>(ContractJson.Options)));
        Assert.Single((await client.GetFromJsonAsync<DebtToIncomeCalculationPage>("/product-api/v1/monthly-cashflow/debt-to-income-ratios",ContractJson.Options))!.Items);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/debt-to-income-ratios",request with{GrossIncomeConfirmed=false},ContractJson.Options)).StatusCode);
        h.Authority.Denied="financial-rules.calculation.execute";Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/product-api/v1/monthly-cashflow/debt-to-income-ratios/"+result.CalculationId)).StatusCode);
    }
    [Fact]
    public async Task MonthlyDtiDoesNotInventGrossIncomeWithoutCustomerConfirmation()
    {
        await using var h=await Harness.Create();using var client=h.Client();var position=await Save(client,Input());
        var cashflow=(await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/calculations",new CalculateMonthlyCashflow(Guid.NewGuid().ToString("N"),position.PositionId),ContractJson.Options)).Content.ReadFromJsonAsync<MonthlyCashflowCalculation>(ContractJson.Options))!;var debt=cashflow.Position.Position.Lines.Single(x=>x.Side=="EXPENSE");
        var ratio=(await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/debt-service-ratios",new CalculateDebtServiceRatio(Guid.NewGuid().ToString("N"),cashflow.CalculationId,new[]{debt.LineId},true),ContractJson.Options)).Content.ReadFromJsonAsync<DebtServiceRatioCalculation>(ContractJson.Options))!;
        var result=(await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/debt-to-income-ratios",new CalculateDebtToIncome(Guid.NewGuid().ToString("N"),ratio.CalculationId,false),ContractJson.Options)).Content.ReadFromJsonAsync<DebtToIncomeCalculation>(ContractJson.Options))!;
        Assert.Equal("INSUFFICIENT_DATA",result.State);Assert.Contains("GROSS_MONTHLY_INCOME_NOT_CONFIRMED",result.Issues);Assert.Null(result.RatioPercent);
    }
    [Theory,InlineData("financial-profile.profile.read"),InlineData("financial-profile.profile.update"),InlineData("advisor")]
    public async Task CurrentAuthorityRejectsInventoryWritesBeforeMutation(string denied)
    {
        await using var h=await Harness.Create();h.Authority.Denied=denied;using var client=h.Client();Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/positions",Input(),ContractJson.Options)).StatusCode);Assert.Empty((await h.Positions.ListAsync("T001",Customer,null,default)).Items);
    }
    [Theory,InlineData("financial-profile.profile.read"),InlineData("financial-rules.calculation.execute"),InlineData("advisor")]
    public async Task CalculationAndSavedResultReadsRevalidateCurrentAuthority(string denied)
    {
        await using var h=await Harness.Create();using var client=h.Client();var position=await Save(client,Input());h.Authority.Denied=denied;Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/calculations",new CalculateMonthlyCashflow(Guid.NewGuid().ToString("N"),position.PositionId),ContractJson.Options)).StatusCode);Assert.Empty((await h.Calculations.ListAsync("T001",Customer,null,default)).Items);
    }
    [Theory,InlineData("positions"),InlineData("calculations")]
    public async Task LostConfirmationReplaysTheSameRequestAfterPermissionReturns(string stage)
    {
        await using var h=await Harness.Create(stage+"-lost");using var client=h.Client();var input=Input();
        if(stage=="positions")
        {Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/positions",input,ContractJson.Options)).StatusCode);h.Authority.Denied="";h.Tamper.Enabled=false;var replay=await Save(client,input);Assert.Single((await h.Positions.ListAsync("T001",Customer,null,default)).Items);Assert.Equal(input.OperationId,replay.OperationId);}
        else
        {var position=await Save(client,input);var request=new CalculateMonthlyCashflow(Guid.NewGuid().ToString("N"),position.PositionId);Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/calculations",request,ContractJson.Options)).StatusCode);h.Authority.Denied="";h.Tamper.Enabled=false;var replay=await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/calculations",request,ContractJson.Options)).Content.ReadFromJsonAsync<MonthlyCashflowCalculation>(ContractJson.Options);Assert.Equal("299.31",replay?.Balance);Assert.Single((await h.Calculations.ListAsync("T001",Customer,null,default)).Items);}
    }
    [Theory,InlineData("positions","customer"),InlineData("positions","actor"),InlineData("positions","operation"),InlineData("positions","value"),InlineData("positions","readback"),InlineData("calculations","customer"),InlineData("calculations","actor"),InlineData("calculations","operation"),InlineData("calculations","value"),InlineData("calculations","rule"),InlineData("calculations","definition"),InlineData("calculations","partial"),InlineData("calculations","readback")]
    public async Task ProductRejectsForgedReceiptsAndExactReadbacks(string stage,string field)
    {
        await using var h=await Harness.Create(stage+"-"+field);using var client=h.Client();HttpResponseMessage response;
        if(stage=="positions")response=await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/positions",Input(),ContractJson.Options);
        else {var position=await Save(client,Input());response=await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/calculations",new CalculateMonthlyCashflow(Guid.NewGuid().ToString("N"),position.PositionId),ContractJson.Options);}
        Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
    }
    [Theory,InlineData(true,"unknown"),InlineData(false,"unknown"),InlineData(true,"missing"),InlineData(false,"missing"),InlineData(true,"null"),InlineData(false,"null"),InlineData(true,"duplicate"),InlineData(false,"duplicate"),InlineData(true,"large"),InlineData(false,"large")]
    public async Task OwnerAndProductRejectAmbiguousInventoryBodies(bool product,string kind)
    {
        await using var h=await Harness.Create();using var client=product?h.Client():h.ProfileClient();var json=JsonNode.Parse(JsonSerializer.Serialize(Input(),ContractJson.Options))!.AsObject();if(kind=="unknown")json["customerId"]="foreign";if(kind=="missing")json["position"]!.AsObject().Remove("incomeComplete");if(kind=="null")json["position"]!["lines"]=null;if(kind=="large")json["position"]!["lines"]![0]!["sourceNote"]=new string('a',300000);var body=json.ToJsonString();if(kind=="duplicate")body=body.Insert(1,"\"operationId\":\""+Guid.NewGuid().ToString("N")+"\",");
        var path=product?"/product-api/v1/monthly-cashflow/positions":"/local/v1/customers/"+Customer+"/monthly-cashflow/positions";Assert.Equal(kind=="large"?413:400,(int)(await client.PostAsync(path,new StringContent(body,Encoding.UTF8,"application/json"))).StatusCode);Assert.Empty((await h.Positions.ListAsync("T001",Customer,null,default)).Items);
    }
    [Fact]
    public async Task LinkedHistoryRequiresCurrentDocumentAccessAndOwnerRejectsTenantOrCustomerSubstitution()
    {
        await using var h=await Harness.Create();var income=Income("income-a","fact-a");await h.Incomes.CreateAsync("T001",Customer,"actor-001","seed",income,default);using var client=h.Client();var position=await Save(client,Linked(income));var result=(await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/calculations",new CalculateMonthlyCashflow(Guid.NewGuid().ToString("N"),position.PositionId),ContractJson.Options)).Content.ReadFromJsonAsync<MonthlyCashflowCalculation>(ContractJson.Options))!;
        h.Authority.Denied="evidence.document.read";Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/product-api/v1/monthly-cashflow/calculations/"+result.CalculationId)).StatusCode);h.Authority.Denied="";
        using var foreign=h.ProfileClient("T002");Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync("/local/v1/customers/"+Customer+"/monthly-cashflow/positions/"+position.PositionId)).StatusCode);Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/product-api/v1/monthly-cashflow/positions?customerId=foreign")).StatusCode);
    }
    [Fact]
    public async Task CalculationHistoryPaginatesWithoutOverlapAndOperationReuseCannotChangeReviewedInventory()
    {
        var positions=Positions();var source=await positions.CreateAsync("T001",Customer,"actor-001",Input(),default);var repository=new InMemoryMonthlyCashflowCalculations();var owner=new MonthlyCashflowCalculationsApplication(new LocalReader(positions),repository,Rule(),TimeProvider.System);var input=new CalculateMonthlyCashflow(Guid.NewGuid().ToString("N"),source.PositionId);var first=await owner.CalculateAsync("T001","session-001",Customer,"actor-001",input,default);
        ((IList<MonthlyCashflowLineDraft>)first.Position.Position.Lines)[0]=Line(value:"1.00");Assert.Equal("1000.10",(await owner.ReadAsync("T001","session-001",Customer,first.CalculationId,default)).Position.Position.Lines[0].Amount);
        for(var n=0;n<50;n++)await owner.CalculateAsync("T001","session-001",Customer,"actor-001",new(Guid.NewGuid().ToString("N"),source.PositionId),default);
        var page=await owner.ListAsync("T001","session-001",Customer,null,default);Assert.Equal(50,page.Items.Count);Assert.True(page.HasMore);var tail=await owner.ListAsync("T001","session-001",Customer,page.NextBeforeId,default);Assert.Single(tail.Items);Assert.Empty(page.Items.Select(x=>x.CalculationId).Intersect(tail.Items.Select(x=>x.CalculationId)));
        var other=await positions.CreateAsync("T001",Customer,"actor-001",Input(),default);Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.CalculateAsync("T001","session-001",Customer,"actor-001",input with {PositionId=other.PositionId},default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.ListAsync("T002","session-001",Customer,page.NextBeforeId,default))).Status);
    }
    [Theory,InlineData("positions"),InlineData("calculations")]
    public async Task UnknownQueryFieldsAndCrossOriginRequestsDoNotCrossCustomerBoundaries(string route)
    {
        await using var h=await Harness.Create();using var client=h.Client();Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/product-api/v1/monthly-cashflow/"+route+"?customerId=foreign")).StatusCode);
        client.DefaultRequestHeaders.Add("Origin","https://foreign.invalid");Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/positions",Input(),ContractJson.Options)).StatusCode);Assert.Empty((await h.Positions.ListAsync("T001",Customer,null,default)).Items);
    }
    [Theory]
    [InlineData("2024-02-29",true)]
    [InlineData("2025-02-28",true)]
    [InlineData("2026-09-29",false)]
    [InlineData("2026-11-30",false)]
    public async Task OnlyCompletedCalendarMonthEndsCanBeReviewed(string date,bool valid)
    {
        var input=Input();var day=DateOnly.Parse(date,CultureInfo.InvariantCulture);input=input with {Position=input.Position with {AsOfDate=day,Lines=Array.Empty<MonthlyCashflowLineDraft>()}};
        if(valid)Assert.NotNull(await Positions().CreateAsync("T001",Customer,"actor-001",input,default));
        else Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>Positions().CreateAsync("T001",Customer,"actor-001",input,default))).Status);
    }
    [Fact]
    public async Task ExpensesResolveExactMonthlyRecordAndRejectUnequalSourcePeriod()
    {
        var expenses=new InMemoryExpenseRecords();var source=new ReviewedExpenseRecord("expense-a",Guid.NewGuid().ToString("N"),Customer,new("Rent","Housing",100m,new(2026,9,1),Day,"fact-expense",1),"INR","provenance-expense","version-expense","evidence-expense","actor-001",DateTimeOffset.UtcNow,"CUSTOMER_REVIEWED");
        await expenses.CreateAsync("T001",Customer,"actor-001","seed",source,default);
        var owner=new MonthlyCashflowPositionsApplication(new InMemoryMonthlyCashflowPositions(),new InMemoryIncomeRecords(),expenses,TimeProvider.System);
        var line=new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"EXPENSE","Housing","Rent","100.00","INR",Day,"Reviewed rent",new("EXPENSE","expense-a",1),"Family");
        var input=new CreateMonthlyCashflowPosition(Guid.NewGuid().ToString("N"),new(Day,"INR",new[]{line},true,true,Household:"Family"));
        var saved=await owner.CreateAsync("T001",Customer,"actor-001",input,default);Assert.Equal("fact-expense",saved.Lines[0].Source!.FinancialFactId);Assert.Equal("-100.00",Rule().Evaluate(saved).Balance);
        await expenses.ReviseAsync("T001",Customer,"actor-001","revision",1,source with {OperationId=Guid.NewGuid().ToString("N"),Revision=2,Expense=source.Expense with {PeriodStart=new(2026,9,2)}},default);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.CreateAsync("T001",Customer,"actor-001",input with {OperationId=Guid.NewGuid().ToString("N"),Position=input.Position with {Lines=new[]{line with {Record=new("EXPENSE","expense-a",2)}}}},default))).Status);
    }
    [Fact]
    public async Task RepeatedHouseholdLinesDoNotProduceAnInflatedTotal()
    {
        var line=Line();var input=Input();input=input with {Position=input.Position with {Lines=new[]{line,line with {LineId=Guid.NewGuid().ToString("N")}}}};
        var value=Rule().Evaluate(await Positions().CreateAsync("T001",Customer,"actor-001",input,default));Assert.Contains("DUPLICATE_REVIEW_LINES",value.Issues);Assert.Null(value.TotalIncome);
    }
    [Theory]
    [InlineData("valid",200)]
    [InlineData("household",409)]
    [InlineData("currency",409)]
    [InlineData("incomplete",409)]
    [InlineData("reverse",409)]
    public async Task ComparisonUsesWholeUnequalCalendarMonthsAndCurrentOwnerResults(string scenario,int expected)
    {
        await using var h=await Harness.Create();using var client=h.Client();
        async Task<MonthlyCashflowCalculation> SaveMonth(DateOnly month,string value,bool earlier)
        {
            var line=Line(value:value) with {ValueDate=month,Currency=scenario=="currency"&&!earlier?"USD":"INR"};
            var input=new CreateMonthlyCashflowPosition(Guid.NewGuid().ToString("N"),new(month,line.Currency,new[]{line},scenario!="incomplete"||earlier,true,Household:scenario=="household"&&!earlier?"Other family":"Family"));
            var position=await Save(client,input);return(await(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/calculations",new CalculateMonthlyCashflow(Guid.NewGuid().ToString("N"),position.PositionId),ContractJson.Options)).Content.ReadFromJsonAsync<MonthlyCashflowCalculation>(ContractJson.Options))!;
        }
        var earlier=await SaveMonth(new(2026,2,28),"100.10",true);var later=await SaveMonth(new(2026,3,31),"125.25",false);
        var input=new CompareMonthlyCashflow(scenario=="reverse"?later.CalculationId:earlier.CalculationId,scenario=="reverse"?earlier.CalculationId:later.CalculationId);
        var response=await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/comparison",input,ContractJson.Options);Assert.Equal(expected,(int)response.StatusCode);
        if(expected==200){var result=(await response.Content.ReadFromJsonAsync<MonthlyCashflowComparison>(ContractJson.Options))!;Assert.Equal("25.15",result.IncomeChange);Assert.Equal("0.00",result.ExpenseChange);Assert.Equal("25.15",result.BalanceChange);}
        h.Authority.Denied="financial-rules.calculation.execute";Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/comparison",input,ContractJson.Options)).StatusCode);
    }
    private sealed class LocalReader(MonthlyCashflowPositionsApplication owner):IMonthlyCashflowPositionReader
    {public Task<ReviewedMonthlyCashflowPosition> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)=>owner.ReadAsync(tenant,customer,id,ct);}
    private static ReviewedIncomeRecord Income(string id,string source)=>new(id,Guid.NewGuid().ToString("N"),Customer,new("Salary","Employer","Salary",200m,"MONTHLY","2026-2027",new DateOnly(2026,9,1),Day,source,1),"INR","provenance-"+source,"version-"+source,"evidence-"+source,"actor-001",DateTimeOffset.UtcNow,"CUSTOMER_REVIEWED");
    private static CreateMonthlyCashflowPosition Linked(ReviewedIncomeRecord value)=>new(Guid.NewGuid().ToString("N"),new(Day,"INR",new[]{new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"INCOME",value.Income.IncomeType,value.Income.Name,value.Income.Amount.ToString("F2",CultureInfo.InvariantCulture),value.Currency,Day,"Reviewed saved record",new("INCOME",value.IncomeRecordId,value.Revision))},true,true));
    private static async Task<ReviewedMonthlyCashflowPosition> Save(HttpClient client,CreateMonthlyCashflowPosition input)
    {var response=await client.PostAsJsonAsync("/product-api/v1/monthly-cashflow/positions",input,ContractJson.Options);Assert.Equal(HttpStatusCode.OK,response.StatusCode);return(await response.Content.ReadFromJsonAsync<ReviewedMonthlyCashflowPosition>(ContractJson.Options))!;}
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
            var json=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();switch(field){case "customer":json["customerId"]="foreign";break;case "actor":json["recordedBy"]="foreign";break;case "operation":json["operationId"]=new string('f',32);break;case "rule":json["ruleVersion"]="2.0.0";break;case "definition":json["definitionHash"]=new string('f',64);break;case "partial":json["state"]="INSUFFICIENT_DATA";break;default:if(stage=="positions")json["position"]!["incomeComplete"]=false;else json["balance"]="1.00";break;}
            response.Content.Dispose();response.Content=new StringContent(json.ToJsonString(),Encoding.UTF8,"application/json");return response;
        }
    }
    private sealed class Harness(WebApplication profile,WebApplication rules,WebApplication gateway,Authority authority,InMemoryMonthlyCashflowPositions positions,InMemoryMonthlyCashflowCalculations calculations,InMemoryDebtServiceRatioCalculations ratios,InMemoryDebtToIncomeCalculations dti,InMemoryIncomeRecords incomes,Tamper tamper):IAsyncDisposable
    {
        public Authority Authority=>authority;public InMemoryMonthlyCashflowPositions Positions=>positions;public InMemoryMonthlyCashflowCalculations Calculations=>calculations;public InMemoryDebtServiceRatioCalculations Ratios=>ratios;public InMemoryDebtToIncomeCalculations Dti=>dti;public InMemoryIncomeRecords Incomes=>incomes;public Tamper Tamper=>tamper;
        public static async Task<Harness> Create(string? scenario=null)
        {
            var authority=new Authority();var positions=new InMemoryMonthlyCashflowPositions();var calculations=new InMemoryMonthlyCashflowCalculations();var ratios=new InMemoryDebtServiceRatioCalculations();var dti=new InMemoryDebtToIncomeCalculations();var incomes=new InMemoryIncomeRecords();var values=new Dictionary<string,string?>{["Monergy:ExecutionZone"]="CI_EPHEMERAL",["Monergy:AccessIntegration:Enabled"]="true",["Monergy:AccessIntegration:CustomerIdentityUrl"]="http://127.0.0.1:5001/",["Monergy:AccessIntegration:AccessManagementUrl"]="http://127.0.0.1:5002/",["Monergy:AccessIntegration:CustomerContextToken"]=new('a',32),["Monergy:AccessIntegration:CustomerIdentityToken"]=new('b',32),["Monergy:AccessIntegration:MembershipToken"]=new('c',32),["Monergy:Product:Enabled"]="true"};
            var pb=Builder(values,authority,"financial-profile");pb.Services.AddSingleton(PositionsOwner(positions,incomes));var profile=pb.Build();profile.MapMonthlyCashflowPositions();await profile.StartAsync();values["Monergy:MonthlyCashflow:FinancialProfileUrl"]=profile.Urls.Single()+"/";values["Monergy:Product:FinancialProfileUrl"]=profile.Urls.Single()+"/";
            var rb=Builder(values,authority,"financial-rules");var cashflow=new MonthlyCashflowCalculationsApplication(new MonthlyCashflowPositionClient(rb.Configuration),calculations,new MonthlyCashflowRule(rb.Configuration),TimeProvider.System);rb.Services.AddSingleton(cashflow);var debtService=new DebtServiceRatioCalculationsApplication(cashflow,ratios,new DebtServiceRatioRule(rb.Configuration),TimeProvider.System);rb.Services.AddSingleton(debtService);rb.Services.AddSingleton(new DebtToIncomeCalculationsApplication(debtService,dti,new DebtToIncomeRule(rb.Configuration),TimeProvider.System));var rules=rb.Build();rules.MapMonthlyCashflowCalculations();rules.MapDebtServiceRatioCalculations();rules.MapDebtToIncomeCalculations();await rules.StartAsync();values["Monergy:Product:FinancialRulesUrl"]=rules.Urls.Single()+"/";
            var gb=Builder(values,authority,"gateway");gb.Services.AddSingleton(new CustomerOwnerClient(gb.Configuration,new HttpClient(authority,false)));var tamper=new Tamper(scenario,authority);gb.Services.AddSingleton(new ProductMonthlyCashflowClient(gb.Configuration,new HttpClient(tamper)));var gateway=gb.Build();gateway.MapProductMonthlyCashflow();await gateway.StartAsync();return new(profile,rules,gateway,authority,positions,calculations,ratios,dti,incomes,tamper);
        }
        private static MonthlyCashflowPositionsApplication PositionsOwner(InMemoryMonthlyCashflowPositions positions,InMemoryIncomeRecords incomes)=>new(positions,incomes,new InMemoryExpenseRecords(),TimeProvider.System);
        private static WebApplicationBuilder Builder(Dictionary<string,string?> values,Authority authority,string service)
        {var b=WebApplication.CreateSlimBuilder();b.WebHost.UseUrls("http://127.0.0.1:0");b.Configuration.AddInMemoryCollection(values);b.Services.AddSingleton(new TenantAccessClient(b.Configuration,new HttpClient(authority,false)));b.Services.AddSingleton(new TenantBoundaryOptions{Service=service,CapabilityPrefix=service+".",CustomerTenants=new Dictionary<string,string>{{Customer,"T001"}},ProbeUrl="http://127.0.0.1:5199/"});return b;}
        public HttpClient Client()=>ClientFor(gateway,"T001");public HttpClient ProfileClient(string tenant="T001")=>ClientFor(profile,tenant);
        private static HttpClient ClientFor(WebApplication app,string tenant){var c=new HttpClient{BaseAddress=new Uri(app.Urls.Single())};c.DefaultRequestHeaders.Add("X-Monergy-Tenant",tenant);c.DefaultRequestHeaders.Add("X-Monergy-Session","session-001");return c;}
        public async ValueTask DisposeAsync(){await gateway.DisposeAsync();await rules.DisposeAsync();await profile.DisposeAsync();authority.Dispose();}
    }
}
