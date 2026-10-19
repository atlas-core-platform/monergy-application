using System.Net;
using System.Net.Http.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Xunit;
namespace Monergy.FinancialProfile.Tests;

public sealed partial class ReviewedVehicleRecordTests
{
    private static readonly DateOnly AssetDate=new(2026,9,30);
    private const string AssetPath="/product-api/v1/asset-records/snapshot?asOfDate=2026-09-30&currency=USD";
    private sealed class AssetClock : TimeProvider
    {
        private long seconds;
        public override DateTimeOffset GetUtcNow()=>new DateTimeOffset(2026,10,19,0,0,0,TimeSpan.Zero).AddSeconds(Interlocked.Increment(ref seconds));
    }
    private static Task<ReviewedInvestmentRecord> InvestmentSnapshotRecord(InvestmentRecordsApplication app,string source,DateOnly? date=null,decimal amount=400000m) =>
        app.CreateAsync("T001",Customer,"actor-001",new(Guid.NewGuid().ToString("N"),new("Reviewed investment","Example reference","Example type",100m,amount,date??AssetDate,source,1)),default);
    [Fact]
    public async Task InvestmentSnapshotFiltersCurrentImmutableVersionsAndDoesNotCarryBalancesForward()
    {
        var(financial,source)=await Seed("INVESTMENT");var repo=new InMemoryInvestmentRecords();var app=new InvestmentRecordsApplication(financial,repo,TimeProvider.System);
        var first=await InvestmentSnapshotRecord(app,source.FinancialFactId);var another=await InvestmentSnapshotRecord(app,source.FinancialFactId,amount:0);
        var corrected=await app.ReviseAsync("T001",Customer,"actor-001",first.InvestmentRecordId,new(Guid.NewGuid().ToString("N"),1,first.Investment with {ValueAsOfDate=new(2026,8,31),CurrentValue=1234.56m}),default);
        Assert.Equal(another,Assert.Single((await new AssetSnapshotApplication(repo,new InMemoryPropertyRecords(),new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"USD",default)).Investments.Items));
        Assert.Equal(corrected,Assert.Single((await new AssetSnapshotApplication(repo,new InMemoryPropertyRecords(),new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001",Customer,new(2026,8,31),"USD",default)).Investments.Items));
        Assert.Empty((await new AssetSnapshotApplication(repo,new InMemoryPropertyRecords(),new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001",Customer,new(2026,9,1),"USD",default)).Investments.Items);
        Assert.Equal(first,await app.ReadAsync("T001",Customer,first.InvestmentRecordId,default,1));
        Assert.Equal(2,(await app.ListAsync("T001",Customer,default)).Items.Count);
        Assert.Equal(100000m,(await financial.GetFactAsync(source.FinancialFactId,1,default))!.Value);
    }
    [Fact]
    public async Task InvestmentSnapshotFiltersTheCompleteOwnedSetBeforeApplyingItsBound()
    {
        var(financial,source)=await Seed("INVESTMENT");var repo=new InMemoryInvestmentRecords();var app=new InvestmentRecordsApplication(financial,repo,new AssetClock());
        var olderMatch=await InvestmentSnapshotRecord(app,source.FinancialFactId);
        for(var i=0;i<205;i++)await InvestmentSnapshotRecord(app,source.FinancialFactId,new(2026,9,29));
        Assert.DoesNotContain((await app.ListAsync("T001",Customer,default)).Items,x=>x.InvestmentRecordId==olderMatch.InvestmentRecordId);
        var view=await new AssetSnapshotApplication(repo,new InMemoryPropertyRecords(),new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"USD",default);
        Assert.Equal(olderMatch,Assert.Single(view.Investments.Items));Assert.False(view.Investments.HasMore);
    }
    [Fact]
    public async Task InvestmentSnapshotHasAnExplicitBoundWithoutCombiningRepeatedSourcesOrAmounts()
    {
        var(financial,source)=await Seed("INVESTMENT");var repo=new InMemoryInvestmentRecords();var app=new InvestmentRecordsApplication(financial,repo,new AssetClock());
        for(var i=0;i<201;i++)await InvestmentSnapshotRecord(app,source.FinancialFactId,amount:i==200?999999999999.99m:0);
        var view=await new AssetSnapshotApplication(repo,new InMemoryPropertyRecords(),new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"USD",default);
        Assert.Equal(200,view.Investments.Items.Count);Assert.True(view.Investments.HasMore);Assert.Equal(999999999999.99m,view.Investments.Items[0].Investment.CurrentValue);
        Assert.Equal(199,view.Investments.Items.Count(x=>x.Investment.CurrentValue==0));Assert.Single(view.Investments.Items.Select(x=>x.Investment.SourceFinancialFactId).Distinct());
        Assert.Empty((await new AssetSnapshotApplication(repo,new InMemoryPropertyRecords(),new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T002",Customer,AssetDate,"USD",default)).Investments.Items);
        Assert.Empty((await new AssetSnapshotApplication(repo,new InMemoryPropertyRecords(),new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001","other-customer",AssetDate,"USD",default)).Investments.Items);
    }
    [Fact]
    public async Task InvestmentSnapshotKeepsSeparateCurrenciesWithoutConversion()
    {
        var(financial,source)=await Seed("INVESTMENT");var suffix=Guid.NewGuid().ToString("N");var normalizer=new FinancialProfileApplication(financial,new CapturingTelemetry(),TimeProvider.System);
        var normalized=await normalizer.NormalizeSourceFactsAsync(FinancialProfileTestContext.Request(Vs02ContractNames.NormalizeSourceFacts,new NormalizeSourceFacts("profile-"+Customer,Customer,"processing-"+suffix,
            [new ValidatedSourceFact("snapshot-"+suffix,Customer,"INVESTMENT","Debt figure",100m,"INR",AssetDate,"line 1",1m,"evidence-"+suffix,"document-"+suffix,"extract/1","validate/1")],"normalize/1",DateTimeOffset.UtcNow),"request-"+suffix,"key-"+suffix));
        var repo=new InMemoryInvestmentRecords();var app=new InvestmentRecordsApplication(financial,repo,TimeProvider.System);
        var usd=await InvestmentSnapshotRecord(app,source.FinancialFactId);var inr=await InvestmentSnapshotRecord(app,Assert.Single(normalized.Data!.Facts).FinancialFactId,amount:2345.67m);
        Assert.Equal(usd,Assert.Single((await new AssetSnapshotApplication(repo,new InMemoryPropertyRecords(),new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"USD",default)).Investments.Items));
        Assert.Equal(inr,Assert.Single((await new AssetSnapshotApplication(repo,new InMemoryPropertyRecords(),new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"INR",default)).Investments.Items));
    }
    private static Task<ReviewedPropertyRecord> PropertySnapshotRecord(PropertyRecordsApplication app,string source,DateOnly? date=null,decimal amount=400000m) =>
        app.CreateAsync("T001",Customer,"actor-001",new(Guid.NewGuid().ToString("N"),new("Reviewed property","Example reference","Example type",100m,amount,date??AssetDate,source,1)),default);
    [Fact]
    public async Task PropertySnapshotFiltersCurrentImmutableVersionsAndDoesNotCarryBalancesForward()
    {
        var(financial,source)=await Seed("PROPERTY");var repo=new InMemoryPropertyRecords();var app=new PropertyRecordsApplication(financial,repo,TimeProvider.System);
        var first=await PropertySnapshotRecord(app,source.FinancialFactId);var another=await PropertySnapshotRecord(app,source.FinancialFactId,amount:0);
        var corrected=await app.ReviseAsync("T001",Customer,"actor-001",first.PropertyRecordId,new(Guid.NewGuid().ToString("N"),1,first.Property with {ValueAsOfDate=new(2026,8,31),CurrentValue=1234.56m}),default);
        Assert.Equal(another,Assert.Single((await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),repo,new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"USD",default)).Properties.Items));
        Assert.Equal(corrected,Assert.Single((await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),repo,new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001",Customer,new(2026,8,31),"USD",default)).Properties.Items));
        Assert.Empty((await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),repo,new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001",Customer,new(2026,9,1),"USD",default)).Properties.Items);
        Assert.Equal(first,await app.ReadAsync("T001",Customer,first.PropertyRecordId,default,1));
        Assert.Equal(2,(await app.ListAsync("T001",Customer,default)).Items.Count);
        Assert.Equal(100000m,(await financial.GetFactAsync(source.FinancialFactId,1,default))!.Value);
    }
    [Fact]
    public async Task PropertySnapshotFiltersTheCompleteOwnedSetBeforeApplyingItsBound()
    {
        var(financial,source)=await Seed("PROPERTY");var repo=new InMemoryPropertyRecords();var app=new PropertyRecordsApplication(financial,repo,new AssetClock());
        var olderMatch=await PropertySnapshotRecord(app,source.FinancialFactId);
        for(var i=0;i<205;i++)await PropertySnapshotRecord(app,source.FinancialFactId,new(2026,9,29));
        Assert.DoesNotContain((await app.ListAsync("T001",Customer,default)).Items,x=>x.PropertyRecordId==olderMatch.PropertyRecordId);
        var view=await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),repo,new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"USD",default);
        Assert.Equal(olderMatch,Assert.Single(view.Properties.Items));Assert.False(view.Properties.HasMore);
    }
    [Fact]
    public async Task PropertySnapshotHasAnExplicitBoundWithoutCombiningRepeatedSourcesOrAmounts()
    {
        var(financial,source)=await Seed("PROPERTY");var repo=new InMemoryPropertyRecords();var app=new PropertyRecordsApplication(financial,repo,new AssetClock());
        for(var i=0;i<201;i++)await PropertySnapshotRecord(app,source.FinancialFactId,amount:i==200?999999999999.99m:0);
        var view=await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),repo,new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"USD",default);
        Assert.Equal(200,view.Properties.Items.Count);Assert.True(view.Properties.HasMore);Assert.Equal(999999999999.99m,view.Properties.Items[0].Property.CurrentValue);
        Assert.Equal(199,view.Properties.Items.Count(x=>x.Property.CurrentValue==0));Assert.Single(view.Properties.Items.Select(x=>x.Property.SourceFinancialFactId).Distinct());
        Assert.Empty((await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),repo,new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T002",Customer,AssetDate,"USD",default)).Properties.Items);
        Assert.Empty((await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),repo,new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001","other-customer",AssetDate,"USD",default)).Properties.Items);
    }
    [Fact]
    public async Task PropertySnapshotKeepsSeparateCurrenciesWithoutConversion()
    {
        var(financial,source)=await Seed("PROPERTY");var suffix=Guid.NewGuid().ToString("N");var normalizer=new FinancialProfileApplication(financial,new CapturingTelemetry(),TimeProvider.System);
        var normalized=await normalizer.NormalizeSourceFactsAsync(FinancialProfileTestContext.Request(Vs02ContractNames.NormalizeSourceFacts,new NormalizeSourceFacts("profile-"+Customer,Customer,"processing-"+suffix,
            [new ValidatedSourceFact("snapshot-"+suffix,Customer,"PROPERTY","Debt figure",100m,"INR",AssetDate,"line 1",1m,"evidence-"+suffix,"document-"+suffix,"extract/1","validate/1")],"normalize/1",DateTimeOffset.UtcNow),"request-"+suffix,"key-"+suffix));
        var repo=new InMemoryPropertyRecords();var app=new PropertyRecordsApplication(financial,repo,TimeProvider.System);
        var usd=await PropertySnapshotRecord(app,source.FinancialFactId);var inr=await PropertySnapshotRecord(app,Assert.Single(normalized.Data!.Facts).FinancialFactId,amount:2345.67m);
        Assert.Equal(usd,Assert.Single((await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),repo,new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"USD",default)).Properties.Items));
        Assert.Equal(inr,Assert.Single((await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),repo,new InMemoryVehicleRecords(),TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"INR",default)).Properties.Items));
    }
    private static Task<ReviewedVehicleRecord> VehicleSnapshotRecord(VehicleRecordsApplication app,string source,DateOnly? date=null,decimal amount=400000m) =>
        app.CreateAsync("T001",Customer,"actor-001",new(Guid.NewGuid().ToString("N"),new("Reviewed vehicle","Example reference","Example type",100m,amount,date??AssetDate,source,1)),default);
    [Fact]
    public async Task VehicleSnapshotFiltersCurrentImmutableVersionsAndDoesNotCarryBalancesForward()
    {
        var(financial,source)=await Seed("VEHICLE");var repo=new InMemoryVehicleRecords();var app=new VehicleRecordsApplication(financial,repo,TimeProvider.System);
        var first=await VehicleSnapshotRecord(app,source.FinancialFactId);var another=await VehicleSnapshotRecord(app,source.FinancialFactId,amount:0);
        var corrected=await app.ReviseAsync("T001",Customer,"actor-001",first.VehicleRecordId,new(Guid.NewGuid().ToString("N"),1,first.Vehicle with {ValueAsOfDate=new(2026,8,31),CurrentValue=1234.56m}),default);
        Assert.Equal(another,Assert.Single((await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),new InMemoryPropertyRecords(),repo,TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"USD",default)).Vehicles.Items));
        Assert.Equal(corrected,Assert.Single((await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),new InMemoryPropertyRecords(),repo,TimeProvider.System).ReadAsync("T001",Customer,new(2026,8,31),"USD",default)).Vehicles.Items));
        Assert.Empty((await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),new InMemoryPropertyRecords(),repo,TimeProvider.System).ReadAsync("T001",Customer,new(2026,9,1),"USD",default)).Vehicles.Items);
        Assert.Equal(first,await app.ReadAsync("T001",Customer,first.VehicleRecordId,default,1));
        Assert.Equal(2,(await app.ListAsync("T001",Customer,default)).Items.Count);
        Assert.Equal(100000m,(await financial.GetFactAsync(source.FinancialFactId,1,default))!.Value);
    }
    [Fact]
    public async Task VehicleSnapshotFiltersTheCompleteOwnedSetBeforeApplyingItsBound()
    {
        var(financial,source)=await Seed("VEHICLE");var repo=new InMemoryVehicleRecords();var app=new VehicleRecordsApplication(financial,repo,new AssetClock());
        var olderMatch=await VehicleSnapshotRecord(app,source.FinancialFactId);
        for(var i=0;i<205;i++)await VehicleSnapshotRecord(app,source.FinancialFactId,new(2026,9,29));
        Assert.DoesNotContain((await app.ListAsync("T001",Customer,default)).Items,x=>x.VehicleRecordId==olderMatch.VehicleRecordId);
        var view=await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),new InMemoryPropertyRecords(),repo,TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"USD",default);
        Assert.Equal(olderMatch,Assert.Single(view.Vehicles.Items));Assert.False(view.Vehicles.HasMore);
    }
    [Fact]
    public async Task VehicleSnapshotHasAnExplicitBoundWithoutCombiningRepeatedSourcesOrAmounts()
    {
        var(financial,source)=await Seed("VEHICLE");var repo=new InMemoryVehicleRecords();var app=new VehicleRecordsApplication(financial,repo,new AssetClock());
        for(var i=0;i<201;i++)await VehicleSnapshotRecord(app,source.FinancialFactId,amount:i==200?999999999999.99m:0);
        var view=await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),new InMemoryPropertyRecords(),repo,TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"USD",default);
        Assert.Equal(200,view.Vehicles.Items.Count);Assert.True(view.Vehicles.HasMore);Assert.Equal(999999999999.99m,view.Vehicles.Items[0].Vehicle.CurrentValue);
        Assert.Equal(199,view.Vehicles.Items.Count(x=>x.Vehicle.CurrentValue==0));Assert.Single(view.Vehicles.Items.Select(x=>x.Vehicle.SourceFinancialFactId).Distinct());
        Assert.Empty((await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),new InMemoryPropertyRecords(),repo,TimeProvider.System).ReadAsync("T002",Customer,AssetDate,"USD",default)).Vehicles.Items);
        Assert.Empty((await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),new InMemoryPropertyRecords(),repo,TimeProvider.System).ReadAsync("T001","other-customer",AssetDate,"USD",default)).Vehicles.Items);
    }
    [Fact]
    public async Task VehicleSnapshotKeepsSeparateCurrenciesWithoutConversion()
    {
        var(financial,source)=await Seed("VEHICLE");var suffix=Guid.NewGuid().ToString("N");var normalizer=new FinancialProfileApplication(financial,new CapturingTelemetry(),TimeProvider.System);
        var normalized=await normalizer.NormalizeSourceFactsAsync(FinancialProfileTestContext.Request(Vs02ContractNames.NormalizeSourceFacts,new NormalizeSourceFacts("profile-"+Customer,Customer,"processing-"+suffix,
            [new ValidatedSourceFact("snapshot-"+suffix,Customer,"VEHICLE","Debt figure",100m,"INR",AssetDate,"line 1",1m,"evidence-"+suffix,"document-"+suffix,"extract/1","validate/1")],"normalize/1",DateTimeOffset.UtcNow),"request-"+suffix,"key-"+suffix));
        var repo=new InMemoryVehicleRecords();var app=new VehicleRecordsApplication(financial,repo,TimeProvider.System);
        var usd=await VehicleSnapshotRecord(app,source.FinancialFactId);var inr=await VehicleSnapshotRecord(app,Assert.Single(normalized.Data!.Facts).FinancialFactId,amount:2345.67m);
        Assert.Equal(usd,Assert.Single((await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),new InMemoryPropertyRecords(),repo,TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"USD",default)).Vehicles.Items));
        Assert.Equal(inr,Assert.Single((await new AssetSnapshotApplication(new InMemoryInvestmentRecords(),new InMemoryPropertyRecords(),repo,TimeProvider.System).ReadAsync("T001",Customer,AssetDate,"INR",default)).Vehicles.Items));
    }
    [Theory,InlineData("usd"),InlineData("US"),InlineData("USDD"),InlineData("U1D"),InlineData("")]
    public async Task SnapshotRejectsInvalidCurrencyOrDatesWithoutWriting(string currency)
    {
        var(financial,source)=await Seed();var app=new VehicleRecordsApplication(financial,new InMemoryVehicleRecords(),TimeProvider.System);var snapshot=new AssetSnapshotApplication(new InMemoryInvestmentRecords(),new InMemoryPropertyRecords(),new InMemoryVehicleRecords(),TimeProvider.System);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>snapshot.ReadAsync("T001",Customer,AssetDate,currency,default))).Status);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>snapshot.ReadAsync("T001",Customer,new(1899,1,1),"USD",default))).Status);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>snapshot.ReadAsync("T001",Customer,new(2099,1,1),"USD",default))).Status);
        Assert.Empty((await app.ListAsync("T001",Customer,default)).Items);
    }
    [Fact]
    public async Task NativeSnapshotAllowsReadOnlyAccessAndReturnsOnlyExactSelection()
    {
        await using var h=await Harness.Create();using var client=h.Client();
        var created=await client.PostAsJsonAsync("/product-api/v1/vehicle-records",Input(h.Source.FinancialFactId) with {Vehicle=Input(h.Source.FinancialFactId).Vehicle with {ValueAsOfDate=AssetDate}},ContractJson.Options);var record=await created.Content.ReadFromJsonAsync<ReviewedVehicleRecord>(ContractJson.Options);
        h.Authority.Failure="update";var response=await client.GetAsync(AssetPath);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal(record,Assert.Single((await response.Content.ReadFromJsonAsync<AssetRecordSnapshot>(ContractJson.Options))!.Vehicles.Items));
        Assert.Contains("no-store",response.Headers.CacheControl!.ToString(),StringComparison.Ordinal);
        Assert.Empty((await client.GetFromJsonAsync<AssetRecordSnapshot>(AssetPath.Replace("USD","INR",StringComparison.Ordinal),ContractJson.Options))!.Vehicles.Items);
        Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    [Theory,InlineData("deny",403),InlineData("advisor",403),InlineData("read",403),InlineData("evidence",403),InlineData("unavailable",503),InlineData("missing",401),InlineData("tenant",401)]
    public async Task SnapshotRequiresCurrentSelfFinancialAndDocumentRead(string failure,int status)
    {
        await using var h=await Harness.Create();h.Authority.Failure=failure;using var client=h.Client(failure=="missing"?null:failure=="tenant"?"T002":"T001");
        Assert.Equal(status,(int)(await client.GetAsync(AssetPath)).StatusCode);
    }
    [Fact]
    public async Task SnapshotReconfirmsAuthorityAndOwnerTenantBinding()
    {
        await using var h=await Harness.Create();h.Authority.DenyAfterEvaluations=4;h.Authority.ResetEvaluations();using var client=h.Client();
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(AssetPath)).StatusCode);
        h.Authority.DenyAfterEvaluations=0;h.Authority.AllowForeignTenant=true;using var foreign=h.OwnerClient("T002");
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync("/local/v1/customers/"+Customer+"/asset-records/snapshot?asOfDate=2026-09-30&currency=USD")).StatusCode);
    }
    private static readonly bool[] SnapshotIngresses=[true,false];
    private static readonly string[] SnapshotBadSelections=[
        "currency=USD","asOfDate=2026-09-30","asOfDate=2026-9-30&currency=USD","asOfDate=2026-02-30&currency=USD","asOfDate=1899-01-01&currency=USD","asOfDate=2099-01-01&currency=USD",
        "asOfDate=2026-09-30&currency=usd","asOfDate=2026-09-30&currency=USD&currency=USD","asOfDate=2026-09-30&currency=USD&amount=1"];
    public static IEnumerable<object[]> SnapshotInvalidQueries()=>from product in SnapshotIngresses from query in SnapshotBadSelections select new object[]{product,query};
    [Theory,MemberData(nameof(SnapshotInvalidQueries))]
    public async Task SnapshotIngressRejectsAmbiguousUnsupportedOrCallerSuppliedSelection(bool product,string query)
    {
        await using var h=await Harness.Create();using var client=product?h.Client():h.OwnerClient();
        var path=(product?"/product-api/v1/asset-records/snapshot?":"/local/v1/customers/"+Customer+"/asset-records/snapshot?")+query;
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(path)).StatusCode);
    }
    [Theory,InlineData("date"),InlineData("currency"),InlineData("item-date"),InlineData("item-currency"),InlineData("customer"),InlineData("metadata"),InlineData("revision"),InlineData("duplicate"),InlineData("large"),InlineData("hasMore"),InlineData("null")]
    public async Task SnapshotGatewayRejectsUnsupportedOrMismatchedOwnerResponses(string variation)
    {
        await using var h=await Harness.Create("asset-"+variation);using var client=h.Client();
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/product-api/v1/vehicle-records",Input(h.Source.FinancialFactId) with {Vehicle=Input(h.Source.FinancialFactId).Vehicle with {ValueAsOfDate=AssetDate}},ContractJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.GetAsync(AssetPath)).StatusCode);
    }
    private sealed class AssetTamperHandler(string scenario) : HttpMessageHandler
    {
        private readonly HttpClient inner=new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            using var forwarded=new HttpRequestMessage(request.Method,request.RequestUri);
            foreach(var header in request.Headers)forwarded.Headers.TryAddWithoutValidation(header.Key,header.Value);
            var response=await inner.SendAsync(forwarded,ct);if(!response.IsSuccessStatusCode)return response;
            var root=System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            var group=root["vehicles"]!;var items=group["items"]!.AsArray();var item=items[0]!;
            switch(scenario[6..])
            {
                case "date":root["asOfDate"]="2026-09-29";break;
                case "currency":root["currency"]="INR";break;
                case "item-date":item["vehicle"]!["valueAsOfDate"]="2026-09-29";break;
                case "item-currency":item["currency"]="INR";break;
                case "customer":item["customerId"]="other";break;
                case "metadata":item["sourceProvenanceId"]="";break;
                case "revision":item["revision"]=0;break;
                case "duplicate":items.Add(item.DeepClone());break;
                case "large":for(var n=1;n<201;n++)items.Add(item.DeepClone());break;
                case "hasMore":group["hasMore"]=true;break;
                case "null":root["properties"]=null;break;
            }
            response.Content=new StringContent(root.ToJsonString(),System.Text.Encoding.UTF8,"application/json");return response;
        }
        protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
    }

}
