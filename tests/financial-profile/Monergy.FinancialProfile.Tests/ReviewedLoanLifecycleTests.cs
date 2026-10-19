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

public sealed partial class ReviewedLoanRecordTests
{
    private static ReviewLoanRecordStatus StatusReview(ReviewedLoanRecord record,string status="CLOSED",DateOnly? date=null) =>
        new(Guid.NewGuid().ToString("N"),record.Revision,status,date ?? new(2026,10,19));
    [Fact]
    public async Task StatusReviewAcceptsTheCurrentLocalDateBeforeUtcMidnightAndRejectsTomorrow()
    {
        var (financial,source)=await Seed();var app=new LoanRecordsApplication(financial,new InMemoryLoanRecords(),new StatusClock(new(2026,10,18,20,0,0,TimeSpan.Zero)));
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        var closed=await app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,StatusReview(first),default);Assert.Equal(new DateOnly(2026,10,19),closed.LifecycleAsOfDate);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,StatusReview(closed,"ACTIVE",new(2026,10,20)),default))).Status);
    }
    private sealed class StatusClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    [Fact]
    public async Task ExplicitStatusReviewsCloseAndReopenWithoutChangingLoanBalancesOrSources()
    {
        var (financial,source)=await Seed();var app=new LoanRecordsApplication(financial,new InMemoryLoanRecords(),TimeProvider.System);var input=Input(source.FinancialFactId);
        var first=await app.CreateAsync("T001",Customer,"actor-001",input,default);
        Assert.Equal("UNREVIEWED",first.LifecycleStatus);Assert.Null(first.LifecycleAsOfDate);
        var active=await app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,StatusReview(first,"ACTIVE",new(2026,10,1)),default);
        var closeRequest=StatusReview(active,"CLOSED",new(2026,10,10));
        var closed=await app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,closeRequest,default);
        var reopened=await app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,StatusReview(closed,"ACTIVE"),default);
        var corrected=await app.ReviseAsync("T001",Customer,"actor-001",first.LoanRecordId,Correction(reopened),default);
        Assert.Equal(5,corrected.Revision);Assert.Equal("ACTIVE",corrected.LifecycleStatus);Assert.Equal(reopened.LifecycleAsOfDate,corrected.LifecycleAsOfDate);
        Assert.Equal(closed,await app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,closeRequest,default));
        Assert.Equal(first,await app.CreateAsync("T001",Customer,"actor-001",input,default));
        var history=await app.HistoryAsync("T001",Customer,first.LoanRecordId,null,default);Assert.Equal(5,history.Items.Count);
        Assert.All(new[]{first,active,closed,reopened},x=> {Assert.Equal(first.Loan,x.Loan);Assert.Equal(first.Currency,x.Currency);Assert.Equal(first.SourceProvenanceId,x.SourceProvenanceId);Assert.Equal(first.DocumentVersionId,x.DocumentVersionId);Assert.Equal(first.EvidenceId,x.EvidenceId);});
        Assert.Equal(corrected,Assert.Single((await app.ListAsync("T001",Customer,default)).Items));
        Assert.Equal(100000m,(await financial.GetFactAsync(source.FinancialFactId,1,default))!.Value);
        Assert.Equal(400000m,closed.Loan.Outstanding);Assert.Equal(10000m,closed.Loan.Emi);Assert.Equal(new DateOnly(2030,12,31),closed.Loan.EndDate);
    }
    [Fact]
    public async Task CorrectionsRetainClosedStatusAndCannotMoveBalanceOrStartAfterClosure()
    {
        var (financial,source)=await Seed();var app=new LoanRecordsApplication(financial,new InMemoryLoanRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        var closed=await app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,StatusReview(first,"CLOSED",new(2026,10,10)),default);
        var change=Correction(closed);var corrected=await app.ReviseAsync("T001",Customer,"actor-001",first.LoanRecordId,change,default);
        Assert.Equal("CLOSED",corrected.LifecycleStatus);Assert.Equal(closed.LifecycleAsOfDate,corrected.LifecycleAsOfDate);
        var invalid=Correction(corrected) with {Loan=corrected.Loan with {BalanceAsOfDate=new(2026,10,11)}};
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.LoanRecordId,invalid,default))).Status);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.LoanRecordId,invalid with {Loan=invalid.Loan with {StartDate=new(2026,10,11)}},default))).Status);
        Assert.Equal(corrected,await app.ReadAsync("T001",Customer,first.LoanRecordId,default));
    }
    [Theory,InlineData("operation"),InlineData("revision"),InlineData("overflow"),InlineData("status"),InlineData("reset"),InlineData("case"),InlineData("default-date"),InlineData("before-start"),InlineData("before-balance"),InlineData("future")]
    public async Task InvalidLifecycleReviewCannotAppendHistory(string field)
    {
        var (financial,source)=await Seed();var app=new LoanRecordsApplication(financial,new InMemoryLoanRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);var input=StatusReview(first);
        input=field switch {"operation"=>input with {OperationId="invalid"},"revision"=>input with {ExpectedRevision=0},"overflow"=>input with {ExpectedRevision=int.MaxValue},
            "status"=>input with {Status="PAID"},"reset"=>input with {Status="UNREVIEWED"},"case"=>input with {Status="closed"},"default-date"=>input with {AsOfDate=default},
            "before-start"=>input with {AsOfDate=new(2025,1,1)},"before-balance"=>input with {AsOfDate=new(2026,9,30)},_=>input with {AsOfDate=new(2099,1,1)}};
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,input,default))).Status);
        Assert.Single((await app.HistoryAsync("T001",Customer,first.LoanRecordId,null,default)).Items);
    }
    [Fact]
    public async Task ReopenReviewCannotBackdateTheLatestReviewedState()
    {
        var (financial,source)=await Seed();var app=new LoanRecordsApplication(financial,new InMemoryLoanRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        var closed=await app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,StatusReview(first),default);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,StatusReview(closed,"ACTIVE",new(2026,10,18)),default))).Status);
        Assert.Equal(closed,await app.ReadAsync("T001",Customer,first.LoanRecordId,default));
    }
    [Fact]
    public async Task LifecycleAndTermOperationsShareOneConcurrencyAndReplayBoundary()
    {
        var (financial,source)=await Seed();var app=new LoanRecordsApplication(financial,new InMemoryLoanRecords(),TimeProvider.System);
        var input=Input(source.FinancialFactId);var first=await app.CreateAsync("T001",Customer,"actor-001",input,default);var status=StatusReview(first);
        var closed=await app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,status,default);
        Assert.Equal("LOAN_REVISION_CONFLICT",(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.LoanRecordId,Correction(first),default))).Code);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,status with {Status="ACTIVE"},default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,status with {ExpectedRevision=2},default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.LoanRecordId,Correction(first) with {OperationId=status.OperationId},default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input with {OperationId=status.OperationId},default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,StatusReview(closed) with {OperationId=input.OperationId},default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviewStatusAsync("T002",Customer,"actor-001",first.LoanRecordId,status,default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviewStatusAsync("T001","other-customer","actor-001",first.LoanRecordId,status,default))).Status);
    }
    [Fact]
    public async Task CompetingLifecycleAndTermEditsCannotOverwriteEachOther()
    {
        var (financial,source)=await Seed();var app=new LoanRecordsApplication(financial,new InMemoryLoanRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        async Task<bool> Change(bool lifecycle) {try {if(lifecycle)await app.ReviewStatusAsync("T001",Customer,"actor-001",first.LoanRecordId,StatusReview(first),default);else await app.ReviseAsync("T001",Customer,"actor-001",first.LoanRecordId,Correction(first),default);return true;}catch(TenantBoundaryException e) when(e.Code=="LOAN_REVISION_CONFLICT"){return false;}}
        var results=await Task.WhenAll(Task.Run(()=>Change(true)),Task.Run(()=>Change(false)));Assert.Single(results,x=>x);Assert.Equal(2,(await app.HistoryAsync("T001",Customer,first.LoanRecordId,null,default)).Items.Count);
    }
    [Fact]
    public async Task LegacyLoanPayloadsRemainUnreviewedAndNeverAcquireAnInferredActiveState()
    {
        var (financial,source)=await Seed();var app=new LoanRecordsApplication(financial,new InMemoryLoanRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        var node=JsonSerializer.SerializeToNode(first,ContractJson.Options)!.AsObject();node.Remove("lifecycleStatus");node.Remove("lifecycleAsOfDate");
        var legacy=node.Deserialize<ReviewedLoanRecord>(ContractJson.Options);Assert.NotNull(legacy);Assert.Equal("UNREVIEWED",legacy.LifecycleStatus);Assert.Null(legacy.LifecycleAsOfDate);Assert.Equal(first,legacy);
    }
    [Fact]
    public async Task ActualRoutesConfirmDatedStatusAndPreserveItThroughTermCorrections()
    {
        await using var h=await Harness.Create();using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/loan-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);var path="/product-api/v1/loan-records/"+first.LoanRecordId;
        var response=await client.PostAsJsonAsync(path+"/lifecycle",StatusReview(first),ContractJson.Options);Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("no-store",response.Headers.CacheControl?.ToString());
        var closed=await response.Content.ReadFromJsonAsync<ReviewedLoanRecord>(ContractJson.Options);Assert.Equal("CLOSED",closed!.LifecycleStatus);Assert.Equal(first.Loan,closed.Loan);
        var corrected=await client.PostAsJsonAsync(path+"/corrections",Correction(closed),ContractJson.Options);Assert.Equal(HttpStatusCode.OK,corrected.StatusCode);Assert.Equal("CLOSED",(await corrected.Content.ReadFromJsonAsync<ReviewedLoanRecord>(ContractJson.Options))!.LifecycleStatus);
        var history=await client.GetFromJsonAsync<LoanRecordHistory>(path+"/history",ContractJson.Options);Assert.Equal(3,history!.Items.Count);Assert.Equal("UNREVIEWED",history.Items[^1].LifecycleStatus);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync(path+"/lifecycle",StatusReview(first),ContractJson.Options)).StatusCode);
    }
    [Theory,InlineData("deny",403),InlineData("advisor",403),InlineData("unavailable",503),InlineData("update",403),InlineData("evidence",403),InlineData("read",403),InlineData("missing",401),InlineData("tenant",401)]
    public async Task StatusReviewRechecksCurrentAuthorityWithoutChangingTheRecord(string failure,int status)
    {
        await using var h=await Harness.Create();using var original=h.Client();await original.PostAsJsonAsync("/product-api/v1/loan-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);h.Authority.Failure=failure;using var client=h.Client(failure=="missing"?null:failure=="tenant"?"T002":"T001");
        Assert.Equal(status,(int)(await client.PostAsJsonAsync("/product-api/v1/loan-records/"+first.LoanRecordId+"/lifecycle",StatusReview(first),ContractJson.Options)).StatusCode);
        Assert.Equal(first,await h.Records.ReadAsync("T001",Customer,first.LoanRecordId,default));
    }
    [Theory,InlineData(true,"duplicate"),InlineData(false,"duplicate"),InlineData(true,"currency"),InlineData(false,"recordedBy"),InlineData(true,"missing"),InlineData(false,"missing")]
    public async Task StatusIngressRejectsDuplicateUnknownAndMissingFields(bool product,string field)
    {
        await using var h=await Harness.Create();using var original=h.Client();await original.PostAsJsonAsync("/product-api/v1/loan-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);var json=JsonSerializer.Serialize(StatusReview(first),ContractJson.Options);
        json=field switch {"duplicate"=>json.Insert(1,"\"STATUS\":\"ACTIVE\","),"missing"=>json.Replace("\"status\":\"CLOSED\",","",StringComparison.Ordinal),_=>json.Insert(1,"\""+field+"\":\"forged\",")};
        using var client=product?h.Client():h.OwnerClient();var path=product?"/product-api/v1/loan-records/":"/local/v1/customers/"+Customer+"/loan-records/";
        using var body=new StringContent(json,Encoding.UTF8,"application/json");Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync(path+first.LoanRecordId+"/lifecycle",body)).StatusCode);
        Assert.Equal(first,await h.Records.ReadAsync("T001",Customer,first.LoanRecordId,default));
    }
    [Fact]
    public async Task ForeignTenantCannotChangeLoanStateDespitePermissiveCollidingCustomerPolicy()
    {
        await using var h=await Harness.Create();using var original=h.Client();await original.PostAsJsonAsync("/product-api/v1/loan-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);h.Authority.AllowForeignTenant=true;using var foreign=h.OwnerClient("T002");
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.PostAsJsonAsync("/local/v1/customers/"+Customer+"/loan-records/"+first.LoanRecordId+"/lifecycle",StatusReview(first),ContractJson.Options)).StatusCode);
        Assert.Equal(first,await h.Records.ReadAsync("T001",Customer,first.LoanRecordId,default));Assert.Empty((await h.Records.ListAsync("T002",Customer,default)).Items);
    }
    [Fact]
    public async Task LostStatusConfirmationReplaysItsOriginalClosedVersionAfterReopen()
    {
        await using var h=await Harness.Create();using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/loan-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);var path="/product-api/v1/loan-records/"+first.LoanRecordId+"/lifecycle";var request=StatusReview(first);
        h.Authority.DenyAfterEvaluations=8;h.Authority.ResetEvaluations();Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path,request,ContractJson.Options)).StatusCode);
        var closed=await h.Records.ReadAsync("T001",Customer,first.LoanRecordId,default);Assert.Equal("CLOSED",closed!.LifecycleStatus);
        h.Authority.DenyAfterEvaluations=0;Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync(path,StatusReview(closed,"ACTIVE"),ContractJson.Options)).StatusCode);
        var replay=await client.PostAsJsonAsync(path,request,ContractJson.Options);Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Equal(closed,await replay.Content.ReadFromJsonAsync<ReviewedLoanRecord>(ContractJson.Options));
        var latest=await h.Records.ReadAsync("T001",Customer,first.LoanRecordId,default);Assert.Equal("ACTIVE",latest!.LifecycleStatus);Assert.Equal(3,latest.Revision);
    }
}
