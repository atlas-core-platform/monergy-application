using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Xunit;
namespace Monergy.FinancialProfile.Tests;

public sealed partial class ReviewedExpenseRecordTests
{
    private static CompareExpensePeriods CompareInput(ReviewedExpenseRecord earlier, ReviewedExpenseRecord later) =>
        new(earlier.ExpenseRecordId,earlier.Revision,later.ExpenseRecordId,later.Revision);
    private static async Task<(ExpenseRecordsApplication App, ReviewedExpenseRecord Earlier, ReviewedExpenseRecord Later)> Pair(
        InMemoryFinancialProfileRepository financial, InMemoryExpenseRecords records, AuthoritativeFinancialFact source, string variation="")
    {
        var suffix=Guid.NewGuid().ToString("N");var normalizer=new FinancialProfileApplication(financial,new CapturingTelemetry(),TimeProvider.System);
        var fact=new ValidatedSourceFact("comparison-"+suffix,Customer,"EXPENSE","Later groceries",120000m,variation=="currency"?"INR":"USD",new(2026,9,30),"line 2",1m,"evidence-"+suffix,"document-"+suffix,"extract/1","validate/1");
        var normalized=await normalizer.NormalizeSourceFactsAsync(FinancialProfileTestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts("profile-"+Customer,Customer,"processing-"+suffix,[fact],"normalize/1",DateTimeOffset.UtcNow),"request-"+suffix,"key-"+suffix));
        var laterSource=variation=="same-source"?source.FinancialFactId:Assert.Single(normalized.Data!.Facts).FinancialFactId;
        var app=new ExpenseRecordsApplication(financial,records,TimeProvider.System);var input=Input(source.FinancialFactId);
        var earlier=await app.CreateAsync("T001",Customer,"actor-001",input with {Expense=input.Expense with {Name="August groceries",PeriodStart=new(2026,8,1),PeriodEnd=new(2026,8,30)}},default);
        var draft=input.Expense with {Name="September groceries",Amount=450000m,SourceFinancialFactId=laterSource,Category=" groceries "};
        draft=variation switch {"category"=>draft with {Category="Other category"},
            "overlap"=>draft with {PeriodStart=new(2026,8,15),PeriodEnd=new(2026,9,13)},"duration"=>draft with {PeriodEnd=new(2026,9,29)},
            "reverse"=>draft with {PeriodStart=new(2026,7,1),PeriodEnd=new(2026,7,30)},_=>draft};
        var later=await app.CreateAsync("T001",Customer,"actor-001",new(Guid.NewGuid().ToString("N"),draft),default);
        return(app,earlier,later);
    }
    [Fact]
    public async Task ComparisonReturnsOnlyTheTwoSelectedImmutableVersionsWithoutConvertingOrAddingAmounts()
    {
        var(financial,source)=await Seed();var(app,earlier,later)=await Pair(financial,new InMemoryExpenseRecords(),source);
        var input=CompareInput(earlier,later);var view=await app.CompareAsync("T001",Customer,input,default);
        Assert.Equal(new ExpensePeriodComparison(earlier,later,30),view);Assert.Equal(400000m,view.Earlier.Expense.Amount);Assert.Equal(450000m,view.Later.Expense.Amount);
        var revised=await app.ReviseAsync("T001",Customer,"actor-001",later.ExpenseRecordId,new(Guid.NewGuid().ToString("N"),1,later.Expense with {Amount=1}),default);
        Assert.Equal(view,await app.CompareAsync("T001",Customer,input,default));Assert.Equal(revised,(await app.CompareAsync("T001",Customer,CompareInput(earlier,revised),default)).Later);
        Assert.Equal(100000m,(await financial.GetFactAsync(source.FinancialFactId,1,default))!.Value);
        Assert.Equal(2,(await app.ListAsync("T001",Customer,default)).Items.Count);
    }
    [Theory,InlineData("same-record"),InlineData("same-source"),InlineData("currency"),InlineData("category"),InlineData("overlap"),InlineData("duration"),InlineData("reverse")]
    public async Task IncompatibleRecordsAreRejectedWithoutCreatingOrChangingAnyVersion(string variation)
    {
        var(financial,source)=await Seed();var(app,earlier,later)=await Pair(financial,new InMemoryExpenseRecords(),source,variation);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CompareAsync("T001",Customer,CompareInput(earlier,variation=="same-record"?earlier:later),default))).Status);
        Assert.Equal(earlier,await app.ReadAsync("T001",Customer,earlier.ExpenseRecordId,default));Assert.Equal(later,await app.ReadAsync("T001",Customer,later.ExpenseRecordId,default));
    }
    [Theory,InlineData(0),InlineData(-1)]
    public async Task InvalidVersionsOrIdentifiersCannotReadRecords(int revision)
    {
        var(financial,source)=await Seed();var(app,earlier,later)=await Pair(financial,new InMemoryExpenseRecords(),source);var input=CompareInput(earlier,later);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CompareAsync("T001",Customer,input with {EarlierRevision=revision},default))).Status);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CompareAsync("T001",Customer,input with {LaterExpenseRecordId="bad id"},default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CompareAsync("T002",Customer,input,default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CompareAsync("T001","other",input,default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CompareAsync("T001",Customer,input with {LaterRevision=99},default))).Status);
    }
    [Theory,InlineData("0"),InlineData("999999999999.99")]
    public async Task ExplicitZeroAndLargeCentTotalsStayUnchangedInComparison(string amount)
    {
        var(financial,source)=await Seed();var(app,earlier,later)=await Pair(financial,new InMemoryExpenseRecords(),source);
        var revised=await app.ReviseAsync("T001",Customer,"actor-001",earlier.ExpenseRecordId,new(Guid.NewGuid().ToString("N"),1,earlier.Expense with {Amount=decimal.Parse(amount,System.Globalization.CultureInfo.InvariantCulture)}),default);
        var view=await app.CompareAsync("T001",Customer,CompareInput(revised,later),default);Assert.Equal(revised.Expense.Amount,view.Earlier.Expense.Amount);Assert.Equal(later.Expense.Amount,view.Later.Expense.Amount);
    }
    [Fact]
    public async Task SingleDayPeriodsCanCompareWithoutInferringRecurrenceFromCategoryOrName()
    {
        var(financial,source)=await Seed();var(app,earlier,later)=await Pair(financial,new InMemoryExpenseRecords(),source);
        earlier=await app.ReviseAsync("T001",Customer,"actor-001",earlier.ExpenseRecordId,new(Guid.NewGuid().ToString("N"),1,earlier.Expense with {PeriodEnd=earlier.Expense.PeriodStart}),default);
        later=await app.ReviseAsync("T001",Customer,"actor-001",later.ExpenseRecordId,new(Guid.NewGuid().ToString("N"),1,later.Expense with {PeriodEnd=later.Expense.PeriodStart,Name="Different one-off purchase"}),default);
        var view=await app.CompareAsync("T001",Customer,CompareInput(earlier,later),default);
        Assert.Equal(1,view.PeriodDays);Assert.Equal(earlier,view.Earlier);Assert.Equal(later,view.Later);
    }
    [Fact]
    public async Task ASharedBoundaryDayIsAnOverlapEvenWithEqualDurations()
    {
        var(financial,source)=await Seed();var(app,earlier,later)=await Pair(financial,new InMemoryExpenseRecords(),source);
        later=await app.ReviseAsync("T001",Customer,"actor-001",later.ExpenseRecordId,new(Guid.NewGuid().ToString("N"),1,later.Expense with {PeriodStart=earlier.Expense.PeriodEnd,PeriodEnd=earlier.Expense.PeriodEnd.AddDays(29)}),default);
        Assert.Equal("EXPENSE_COMPARISON_PERIODS_NOT_COMPARABLE",(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CompareAsync("T001",Customer,CompareInput(earlier,later),default))).Code);
    }
    [Fact]
    public async Task NativeComparisonAllowsReadOnlyAccessAndFreshlyReadsBothExactVersions()
    {
        await using var h=await Harness.Create();var(_,earlier,later)=await Pair(h.Financial,h.Records,h.Source);h.Authority.Failure="update";using var client=h.Client();
        var response=await client.PostAsJsonAsync("/product-api/v1/expense-records/comparison",CompareInput(earlier,later),ContractJson.Options);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal(new ExpensePeriodComparison(earlier,later,30),await response.Content.ReadFromJsonAsync<ExpensePeriodComparison>(ContractJson.Options));
        Assert.Contains("no-store",response.Headers.CacheControl!.ToString(),StringComparison.Ordinal);Assert.Equal(2,(await h.Records.ListAsync("T001",Customer,default)).Items.Count);
    }
    [Theory,InlineData("deny",403),InlineData("advisor",403),InlineData("read",403),InlineData("evidence",403),InlineData("unavailable",503),InlineData("missing",401),InlineData("tenant",401)]
    public async Task NativeComparisonRequiresCurrentSelfDocumentAndFinancialReadAuthority(string failure,int status)
    {
        await using var h=await Harness.Create();var(_,earlier,later)=await Pair(h.Financial,h.Records,h.Source);h.Authority.Failure=failure;using var client=h.Client(failure=="missing"?null:failure=="tenant"?"T002":"T001");
        Assert.Equal(status,(int)(await client.PostAsJsonAsync("/product-api/v1/expense-records/comparison",CompareInput(earlier,later),ContractJson.Options)).StatusCode);
        Assert.Equal(2,(await h.Records.ListAsync("T001",Customer,default)).Items.Count);
    }
    [Fact]
    public async Task ComparisonReconfirmsAuthorityAndRejectsForeignOwnerTenant()
    {
        await using var h=await Harness.Create();var(_,earlier,later)=await Pair(h.Financial,h.Records,h.Source);h.Authority.DenyAfterEvaluations=8;h.Authority.ResetEvaluations();using var client=h.Client();
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/product-api/v1/expense-records/comparison",CompareInput(earlier,later),ContractJson.Options)).StatusCode);
        h.Authority.DenyAfterEvaluations=0;h.Authority.AllowForeignTenant=true;using var foreign=h.OwnerClient("T002");
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.PostAsJsonAsync("/local/v1/customers/"+Customer+"/expense-records/comparison",CompareInput(earlier,later),ContractJson.Options)).StatusCode);
    }
    [Theory,InlineData(true,"duplicate"),InlineData(false,"duplicate"),InlineData(true,"unknown"),InlineData(false,"missing"),InlineData(true,"null"),InlineData(false,"large")]
    public async Task ComparisonIngressRejectsAmbiguousOrCallerSuppliedValues(bool product,string variation)
    {
        await using var h=await Harness.Create();var(_,earlier,later)=await Pair(h.Financial,h.Records,h.Source);var json=JsonSerializer.Serialize(CompareInput(earlier,later),ContractJson.Options);
        json=variation switch {"duplicate"=>json.Insert(1,"\"EARLIERREVISION\":1,"),"unknown"=>json.Insert(1,"\"amount\":9,"),"missing"=>json.Replace("\"earlierRevision\":1,","",StringComparison.Ordinal),"null"=>"null","large"=>json.Insert(1,"\"extra\":\""+new string('x',40000)+"\","),_=>json};
        using var client=product?h.Client():h.OwnerClient();var path=product?"/product-api/v1/expense-records/comparison":"/local/v1/customers/"+Customer+"/expense-records/comparison";
        var response=await client.PostAsync(path,new StringContent(json,Encoding.UTF8,"application/json"));Assert.Equal(variation=="large"?413:400,(int)response.StatusCode);
    }
    [Theory,InlineData("customer"),InlineData("revision"),InlineData("days"),InlineData("status"),InlineData("currency"),InlineData("category"),InlineData("source"),InlineData("null"),InlineData("swapped"),InlineData("amount"),InlineData("readback")]
    public async Task GatewayRejectsForgedComparisonOrMismatchedFreshReadback(string variation)
    {
        await using var h=await Harness.Create("comparison-"+variation);var(_,earlier,later)=await Pair(h.Financial,h.Records,h.Source);using var client=h.Client();
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.PostAsJsonAsync("/product-api/v1/expense-records/comparison",CompareInput(earlier,later),ContractJson.Options)).StatusCode);
        Assert.Equal(2,(await h.Records.ListAsync("T001",Customer,default)).Items.Count);
    }
}
