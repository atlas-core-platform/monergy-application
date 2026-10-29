using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules;
using Monergy.Services.FinancialRules.Application;
using Monergy.Services.FinancialRules.Domain;
using Monergy.Services.FinancialRules.Infrastructure;
using Monergy.UatGateway;
using Xunit;
namespace Monergy.FinancialProfile.Tests;
public sealed class LiquidityRatioJourneyTests
{
    private const string Customer="reference-customer";private static readonly DateOnly NetDay=new(2026,10,1);private static readonly DateOnly Month=new(2026,9,30);
    [Fact]
    public async Task ExactTwoSourceJourneyCalculatesReplaysListsAndRevalidatesCurrentAuthority()
    {
        await using var h=await Harness.Create();using var client=h.Client();var eligible=h.Net.Position.Position.Lines.Where(x=>x.Kind is "CASH" or "BANK_ACCOUNT").Select(x=>x.LineId).ToArray();var request=new CalculateLiquidityRatio(Guid.NewGuid().ToString("N"),h.Net.CalculationId,h.Monthly.CalculationId,eligible,true);
        var response=await client.PostAsJsonAsync("/product-api/v1/net-worth/liquidity-ratios",request,ContractJson.Options);Assert.Equal(HttpStatusCode.OK,response.StatusCode);var value=(await response.Content.ReadFromJsonAsync<LiquidityRatioCalculation>(ContractJson.Options))!;
        Assert.Equal("1800.00",value.LiquidAssets);Assert.Equal("600.00",value.MonthlyExpenses);Assert.Equal("3.00",value.RatioMonths);Assert.Equal(LiquidityRatioDefinition.DefinitionHash,value.DefinitionHash);
        Assert.True(NetWorthData.Same(value,await client.GetFromJsonAsync<LiquidityRatioCalculation>("/product-api/v1/net-worth/liquidity-ratios/"+value.CalculationId,ContractJson.Options)));
        Assert.True(NetWorthData.Same(value,await(await client.PostAsJsonAsync("/product-api/v1/net-worth/liquidity-ratios",request,ContractJson.Options)).Content.ReadFromJsonAsync<LiquidityRatioCalculation>(ContractJson.Options)));
        Assert.Single((await client.GetFromJsonAsync<LiquidityRatioCalculationPage>("/product-api/v1/net-worth/liquidity-ratios",ContractJson.Options))!.Items);
        var property=h.Net.Position.Position.Lines.Single(x=>x.Kind=="PROPERTY");Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/product-api/v1/net-worth/liquidity-ratios",request with{OperationId=Guid.NewGuid().ToString("N"),LiquidAssetLineIds=new[]{property.LineId}},ContractJson.Options)).StatusCode);
        h.Authority.Denied="financial-rules.calculation.execute";Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/product-api/v1/net-worth/liquidity-ratios/"+value.CalculationId)).StatusCode);
    }
    [Fact]
    public async Task MissingCoverageZeroExpensesAndCurrencyMismatchNeverInventLiquidity()
    {
        await using var h=await Harness.Create();var rule=new LiquidityRatioRule(new ConfigurationBuilder().Build());var cash=h.Net.Position.Position.Lines.Single(x=>x.Kind=="CASH");
        var incomplete=rule.Evaluate(h.Net,h.Monthly,new[]{cash.LineId},true);Assert.Equal("INSUFFICIENT_DATA",incomplete.State);Assert.Contains("LIQUID_ASSET_SELECTION_INCOMPLETE",incomplete.Issues);Assert.Null(incomplete.RatioMonths);
        var unconfirmed=rule.Evaluate(h.Net,h.Monthly,h.Net.Position.Position.Lines.Where(x=>x.Kind is "CASH" or "BANK_ACCOUNT").Select(x=>x.LineId).ToArray(),false);Assert.Contains("LIQUID_ASSETS_NOT_CONFIRMED",unconfirmed.Issues);Assert.Null(unconfirmed.RatioMonths);
        var zero=Cashflow("INR","0.00");Assert.Contains("MONTHLY_EXPENSES_ZERO",rule.Evaluate(h.Net,zero,h.Net.Position.Position.Lines.Where(x=>x.Kind is "CASH" or "BANK_ACCOUNT").Select(x=>x.LineId).ToArray(),true).Issues);
        var other=Cashflow("USD","600.00");Assert.Contains("CURRENCIES_DIFFER",rule.Evaluate(h.Net,other,h.Net.Position.Position.Lines.Where(x=>x.Kind is "CASH" or "BANK_ACCOUNT").Select(x=>x.LineId).ToArray(),true).Issues);
        Assert.Throws<InvalidOperationException>(()=>new LiquidityRatioRule(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Monergy:LiquidityRatio:RuleVersion","2.0.0"}}).Build()));
    }
    private static NetWorthCalculation NetWorth()
    {
        var lines=new[]{new NetWorthLineDraft(Guid.NewGuid().ToString("N"),"ASSET","CASH","Cash reserve","1200.00","INR",NetDay,"Reviewed cash"),new(Guid.NewGuid().ToString("N"),"ASSET","BANK_ACCOUNT","Savings","600.00","INR",NetDay,"Reviewed bank account"),new(Guid.NewGuid().ToString("N"),"ASSET","PROPERTY","Home","5000.00","INR",NetDay,"Reviewed property")};
        var position=new ReviewedNetWorthPosition("net-position",Guid.NewGuid().ToString("N"),Customer,new(NetDay,"INR",lines,true,true),lines.Select(x=>new NetWorthPositionLine(x,null)).ToArray(),"actor-001",DateTimeOffset.UtcNow);
        return new("networth-source",Guid.NewGuid().ToString("N"),Customer,position,NetWorthDefinition.Id,NetWorthDefinition.Version,NetWorthDefinition.DefinitionHash,NetWorthDefinition.Semantics,"CALCULATED","6800.00","0.00","6800.00",Array.Empty<string>(),"actor-001",DateTimeOffset.UtcNow);
    }
    private static MonthlyCashflowCalculation Cashflow(string currency="INR",string expenses="600.00")
    {
        var lines=new[]{new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"INCOME","SALARY","Salary","5000.00",currency,Month,"Reviewed salary"),new(Guid.NewGuid().ToString("N"),"EXPENSE","HOUSEHOLD","Monthly expenses",expenses,currency,Month,"Reviewed expenses")};
        var position=new ReviewedMonthlyCashflowPosition("cashflow-position",Guid.NewGuid().ToString("N"),Customer,new(Month,currency,lines,true,true),lines.Select(x=>new MonthlyCashflowPositionLine(x,null)).ToArray(),"actor-001",DateTimeOffset.UtcNow);
        var balance=(5000m-decimal.Parse(expenses,System.Globalization.CultureInfo.InvariantCulture)).ToString("F2",System.Globalization.CultureInfo.InvariantCulture);
        return new("cashflow-source-"+currency+expenses.Replace(".","",StringComparison.Ordinal),Guid.NewGuid().ToString("N"),Customer,position,MonthlyCashflowDefinition.Id,MonthlyCashflowDefinition.Version,MonthlyCashflowDefinition.DefinitionHash,MonthlyCashflowDefinition.Semantics,"CALCULATED","5000.00",expenses,balance,Array.Empty<string>(),"actor-001",DateTimeOffset.UtcNow);
    }
    private sealed class FixedNetWorthReader(NetWorthCalculation value):INetWorthPositionReader
    {public Task<ReviewedNetWorthPosition> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)=>tenant=="T001"&&customer==Customer&&id==value.Position.PositionId?Task.FromResult(value.Position):Task.FromException<ReviewedNetWorthPosition>(new TenantBoundaryException("NET_WORTH_NOT_FOUND",404));}
    private sealed class FixedCashflowReader(MonthlyCashflowCalculation value):IMonthlyCashflowPositionReader
    {public Task<ReviewedMonthlyCashflowPosition> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)=>tenant=="T001"&&customer==Customer&&id==value.Position.PositionId?Task.FromResult(value.Position):Task.FromException<ReviewedMonthlyCashflowPosition>(new TenantBoundaryException("MONTHLY_CASHFLOW_NOT_FOUND",404));}
    public sealed class Authority:HttpMessageHandler
    {
        public string Denied{get;set;}="";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {var path=request.RequestUri!.AbsolutePath;if(path=="/v1/authorization/customer-evaluations"){var input=(await request.Content!.ReadFromJsonAsync<CustomerAccessRequest>(cancellationToken:cancellationToken))!;var tenant=request.Headers.GetValues("X-Monergy-Tenant").Single();var denied=Denied==input.CapabilityId||tenant!="T001"||input.CustomerId!=Customer;return Reply(new CustomerAccessDecision("decision-001",denied?"Deny":"Allow",tenant,"actor-001",input.CustomerId,input.CapabilityId,input.Purpose,denied?"Denied":"Granted","Self",1,2,1,1,DateTimeOffset.UtcNow));}if(path=="/internal/v1/tenant-sessions/validate")return Reply(new CustomerOwnerSession("T001","actor-001","session-001",2,DateTimeOffset.UtcNow.AddHours(1)));if(path=="/internal/v1/customer-owner/actor-001")return Reply(new CustomerOwnerWorkspace("T001",Customer,"Customer",null,"actor-001",1,true));throw new InvalidOperationException("Unexpected authority route.");}
        private static HttpResponseMessage Reply<T>(T value)=>new(HttpStatusCode.OK){Content=JsonContent.Create(value)};
    }
    private sealed class Harness(WebApplication rules,WebApplication gateway,Authority authority,NetWorthCalculation net,MonthlyCashflowCalculation monthly):IAsyncDisposable
    {
        public Authority Authority=>authority;public NetWorthCalculation Net=>net;public MonthlyCashflowCalculation Monthly=>monthly;
        public static async Task<Harness> Create()
        {
            var authority=new Authority();var net=NetWorth();var monthly=Cashflow();var values=new Dictionary<string,string?>{{"Monergy:ExecutionZone","CI_EPHEMERAL"},{"Monergy:AccessIntegration:Enabled","true"},{"Monergy:AccessIntegration:CustomerIdentityUrl","http://127.0.0.1:5001/"},{"Monergy:AccessIntegration:AccessManagementUrl","http://127.0.0.1:5002/"},{"Monergy:AccessIntegration:CustomerContextToken",new('a',32)},{"Monergy:AccessIntegration:CustomerIdentityToken",new('b',32)},{"Monergy:AccessIntegration:MembershipToken",new('c',32)},{"Monergy:Product:Enabled","true"}};
            var rb=Builder(values,authority,"financial-rules");var netRepo=new InMemoryNetWorthCalculations();var monthlyRepo=new InMemoryMonthlyCashflowCalculations();var netOwner=new NetWorthCalculationsApplication(new FixedNetWorthReader(net),netRepo,new NetWorthRule(rb.Configuration),TimeProvider.System);var monthlyOwner=new MonthlyCashflowCalculationsApplication(new FixedCashflowReader(monthly),monthlyRepo,new MonthlyCashflowRule(rb.Configuration),TimeProvider.System);
            net=await netOwner.CalculateAsync("T001","session-001",Customer,"actor-001",new(Guid.NewGuid().ToString("N"),net.Position.PositionId),default);monthly=await monthlyOwner.CalculateAsync("T001","session-001",Customer,"actor-001",new(Guid.NewGuid().ToString("N"),monthly.Position.PositionId),default);
            rb.Services.AddSingleton(netOwner);rb.Services.AddSingleton(monthlyOwner);rb.Services.AddSingleton(new LiquidityRatioCalculationsApplication(netOwner,monthlyOwner,new InMemoryLiquidityRatioCalculations(),new LiquidityRatioRule(rb.Configuration),TimeProvider.System));var rules=rb.Build();rules.MapNetWorthCalculations();rules.MapMonthlyCashflowCalculations();rules.MapLiquidityRatioCalculations();await rules.StartAsync();values["Monergy:Product:FinancialRulesUrl"]=rules.Urls.Single()+"/";values["Monergy:Product:FinancialProfileUrl"]="http://127.0.0.1:1/";
            var gb=Builder(values,authority,"gateway");gb.Services.AddSingleton(new CustomerOwnerClient(gb.Configuration,new HttpClient(authority,false)));gb.Services.AddSingleton(new ProductNetWorthClient(gb.Configuration));var gateway=gb.Build();gateway.MapProductNetWorth();await gateway.StartAsync();return new(rules,gateway,authority,net,monthly);
        }
        private static WebApplicationBuilder Builder(Dictionary<string,string?> values,Authority authority,string service){var b=WebApplication.CreateSlimBuilder();b.WebHost.UseUrls("http://127.0.0.1:0");b.Configuration.AddInMemoryCollection(values);b.Services.AddSingleton(new TenantAccessClient(b.Configuration,new HttpClient(authority,false)));b.Services.AddSingleton(new TenantBoundaryOptions{Service=service,CapabilityPrefix=service+".",CustomerTenants=new Dictionary<string,string>{{Customer,"T001"}},ProbeUrl="http://127.0.0.1:5199/"});return b;}
        public HttpClient Client(){var c=new HttpClient{BaseAddress=new Uri(gateway.Urls.Single())};c.DefaultRequestHeaders.Add("X-Monergy-Tenant","T001");c.DefaultRequestHeaders.Add("X-Monergy-Session","session-001");return c;}
        public async ValueTask DisposeAsync(){await gateway.DisposeAsync();await rules.DisposeAsync();authority.Dispose();}
    }
}
