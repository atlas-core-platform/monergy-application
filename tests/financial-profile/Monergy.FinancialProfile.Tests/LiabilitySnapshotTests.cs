using System.Net;
using System.Net.Http.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Xunit;
namespace Monergy.FinancialProfile.Tests;

public sealed partial class ReviewedLiabilityRecordTests
{
    private static readonly DateOnly SnapshotDate=new(2026,9,30);
    private const string SnapshotPath="/product-api/v1/liability-records/snapshot?asOfDate=2026-09-30&currency=USD";
    private static Task<ReviewedLiabilityRecord> SnapshotRecord(LiabilityRecordsApplication app,string source,DateOnly? date=null,decimal amount=400000m) =>
        app.CreateAsync("T001",Customer,"actor-001",Input(source) with {OperationId=Guid.NewGuid().ToString("N"),Liability=Input(source).Liability with {BalanceAsOfDate=date??SnapshotDate,OutstandingAmount=amount}},default);
    private sealed class SnapshotClock : TimeProvider
    {
        private long seconds;
        public override DateTimeOffset GetUtcNow()=>new DateTimeOffset(2026,10,19,0,0,0,TimeSpan.Zero).AddSeconds(Interlocked.Increment(ref seconds));
    }
    [Fact]
    public async Task SnapshotFiltersCurrentImmutableVersionsAndDoesNotCarryBalancesForward()
    {
        var(financial,source)=await Seed();var app=new LiabilityRecordsApplication(financial,new InMemoryLiabilityRecords(),TimeProvider.System);
        var first=await SnapshotRecord(app,source.FinancialFactId);var another=await SnapshotRecord(app,source.FinancialFactId,amount:0);
        var corrected=await app.ReviseAsync("T001",Customer,"actor-001",first.LiabilityRecordId,new(Guid.NewGuid().ToString("N"),1,first.Liability with {BalanceAsOfDate=new(2026,8,31),OutstandingAmount=1234.56m}),default);
        Assert.Equal(another,Assert.Single((await app.SnapshotAsync("T001",Customer,SnapshotDate,"USD",default)).Items));
        Assert.Equal(corrected,Assert.Single((await app.SnapshotAsync("T001",Customer,new(2026,8,31),"USD",default)).Items));
        Assert.Empty((await app.SnapshotAsync("T001",Customer,new(2026,9,1),"USD",default)).Items);
        Assert.Equal(first,await app.ReadAsync("T001",Customer,first.LiabilityRecordId,default,1));
        Assert.Equal(2,(await app.ListAsync("T001",Customer,default)).Items.Count);
        Assert.Equal(100000m,(await financial.GetFactAsync(source.FinancialFactId,1,default))!.Value);
    }
    [Fact]
    public async Task SnapshotFiltersTheCompleteOwnedSetBeforeApplyingItsBound()
    {
        var(financial,source)=await Seed();var app=new LiabilityRecordsApplication(financial,new InMemoryLiabilityRecords(),new SnapshotClock());
        var olderMatch=await SnapshotRecord(app,source.FinancialFactId);
        for(var i=0;i<205;i++)await SnapshotRecord(app,source.FinancialFactId,new(2026,9,29));
        Assert.DoesNotContain((await app.ListAsync("T001",Customer,default)).Items,x=>x.LiabilityRecordId==olderMatch.LiabilityRecordId);
        var view=await app.SnapshotAsync("T001",Customer,SnapshotDate,"USD",default);
        Assert.Equal(olderMatch,Assert.Single(view.Items));Assert.False(view.HasMore);
    }
    [Fact]
    public async Task SnapshotHasAnExplicitBoundWithoutCombiningRepeatedSourcesOrAmounts()
    {
        var(financial,source)=await Seed();var app=new LiabilityRecordsApplication(financial,new InMemoryLiabilityRecords(),new SnapshotClock());
        for(var i=0;i<201;i++)await SnapshotRecord(app,source.FinancialFactId,amount:i==200?999999999999.99m:0);
        var view=await app.SnapshotAsync("T001",Customer,SnapshotDate,"USD",default);
        Assert.Equal(200,view.Items.Count);Assert.True(view.HasMore);Assert.Equal(999999999999.99m,view.Items[0].Liability.OutstandingAmount);
        Assert.Equal(199,view.Items.Count(x=>x.Liability.OutstandingAmount==0));Assert.Single(view.Items.Select(x=>x.Liability.SourceFinancialFactId).Distinct());
        Assert.Empty((await app.SnapshotAsync("T002",Customer,SnapshotDate,"USD",default)).Items);
        Assert.Empty((await app.SnapshotAsync("T001","other-customer",SnapshotDate,"USD",default)).Items);
    }
    [Fact]
    public async Task SnapshotKeepsSeparateCurrenciesWithoutConversion()
    {
        var(financial,source)=await Seed();var suffix=Guid.NewGuid().ToString("N");var normalizer=new FinancialProfileApplication(financial,new CapturingTelemetry(),TimeProvider.System);
        var normalized=await normalizer.NormalizeSourceFactsAsync(FinancialProfileTestContext.Request(Vs02ContractNames.NormalizeSourceFacts,new NormalizeSourceFacts("profile-"+Customer,Customer,"processing-"+suffix,
            [new ValidatedSourceFact("snapshot-"+suffix,Customer,"LOAN","Debt figure",100m,"INR",SnapshotDate,"line 1",1m,"evidence-"+suffix,"document-"+suffix,"extract/1","validate/1")],"normalize/1",DateTimeOffset.UtcNow),"request-"+suffix,"key-"+suffix));
        var app=new LiabilityRecordsApplication(financial,new InMemoryLiabilityRecords(),TimeProvider.System);
        var usd=await SnapshotRecord(app,source.FinancialFactId);var inr=await SnapshotRecord(app,Assert.Single(normalized.Data!.Facts).FinancialFactId,amount:2345.67m);
        Assert.Equal(usd,Assert.Single((await app.SnapshotAsync("T001",Customer,SnapshotDate,"USD",default)).Items));
        Assert.Equal(inr,Assert.Single((await app.SnapshotAsync("T001",Customer,SnapshotDate,"INR",default)).Items));
    }
    [Theory,InlineData("usd"),InlineData("US"),InlineData("USDD"),InlineData("U1D"),InlineData("")]
    public async Task SnapshotRejectsInvalidCurrencyOrDatesWithoutWriting(string currency)
    {
        var(financial,source)=await Seed();var app=new LiabilityRecordsApplication(financial,new InMemoryLiabilityRecords(),TimeProvider.System);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.SnapshotAsync("T001",Customer,SnapshotDate,currency,default))).Status);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.SnapshotAsync("T001",Customer,new(1899,1,1),"USD",default))).Status);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.SnapshotAsync("T001",Customer,new(2099,1,1),"USD",default))).Status);
        Assert.Empty((await app.ListAsync("T001",Customer,default)).Items);
    }
    [Fact]
    public async Task NativeSnapshotAllowsReadOnlyAccessAndReturnsOnlyExactSelection()
    {
        await using var h=await Harness.Create();using var client=h.Client();
        var created=await client.PostAsJsonAsync("/product-api/v1/liability-records",Input(h.Source.FinancialFactId),ContractJson.Options);var record=await created.Content.ReadFromJsonAsync<ReviewedLiabilityRecord>(ContractJson.Options);
        h.Authority.Failure="update";var response=await client.GetAsync(SnapshotPath);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal(record,Assert.Single((await response.Content.ReadFromJsonAsync<LiabilityRecordSnapshot>(ContractJson.Options))!.Items));
        Assert.Contains("no-store",response.Headers.CacheControl!.ToString(),StringComparison.Ordinal);
        Assert.Empty((await client.GetFromJsonAsync<LiabilityRecordSnapshot>(SnapshotPath.Replace("USD","INR",StringComparison.Ordinal),ContractJson.Options))!.Items);
        Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    [Theory,InlineData("deny",403),InlineData("advisor",403),InlineData("read",403),InlineData("evidence",403),InlineData("unavailable",503),InlineData("missing",401),InlineData("tenant",401)]
    public async Task SnapshotRequiresCurrentSelfFinancialAndDocumentRead(string failure,int status)
    {
        await using var h=await Harness.Create();h.Authority.Failure=failure;using var client=h.Client(failure=="missing"?null:failure=="tenant"?"T002":"T001");
        Assert.Equal(status,(int)(await client.GetAsync(SnapshotPath)).StatusCode);
    }
    [Fact]
    public async Task SnapshotReconfirmsAuthorityAndOwnerTenantBinding()
    {
        await using var h=await Harness.Create();h.Authority.DenyAfterEvaluations=4;h.Authority.ResetEvaluations();using var client=h.Client();
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(SnapshotPath)).StatusCode);
        h.Authority.DenyAfterEvaluations=0;h.Authority.AllowForeignTenant=true;using var foreign=h.OwnerClient("T002");
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync("/local/v1/customers/"+Customer+"/liability-records/snapshot?asOfDate=2026-09-30&currency=USD")).StatusCode);
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
        var path=(product?"/product-api/v1/liability-records/snapshot?":"/local/v1/customers/"+Customer+"/liability-records/snapshot?")+query;
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(path)).StatusCode);
    }
    [Theory,InlineData("date"),InlineData("currency"),InlineData("item-date"),InlineData("item-currency"),InlineData("customer"),InlineData("status"),InlineData("revision"),InlineData("duplicate"),InlineData("large"),InlineData("hasMore"),InlineData("null")]
    public async Task SnapshotGatewayRejectsUnsupportedOrMismatchedOwnerResponses(string variation)
    {
        await using var h=await Harness.Create("snapshot-"+variation);using var client=h.Client();
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/product-api/v1/liability-records",Input(h.Source.FinancialFactId),ContractJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.GetAsync(SnapshotPath)).StatusCode);
    }
}
