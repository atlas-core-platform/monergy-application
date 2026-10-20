using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Monergy.UatGateway;
using Xunit;
namespace Monergy.FinancialProfile.Tests;

public sealed partial class ReviewedTaxRecordTests
{
    private static ReviseTaxRecord Correction(ReviewedTaxRecord record, decimal outstanding = 390000m) =>
        new(Guid.NewGuid().ToString("N"),record.Revision,record.Tax with { Amount = outstanding, Name = "Corrected Tax statement" });
    [Fact]
    public async Task ExistingCreationPayloadsWithoutVersionRemainVersionOne()
    {
        var (financial,source)=await Seed();var app=new TaxRecordsApplication(financial,new InMemoryTaxRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        var legacy=JsonSerializer.Serialize(first,ContractJson.Options).Replace(",\"revision\":1}","}",StringComparison.Ordinal);
        Assert.DoesNotContain("\"revision\"",legacy,StringComparison.Ordinal);
        Assert.Equal(first,JsonSerializer.Deserialize<ReviewedTaxRecord>(legacy,ContractJson.Options));
    }
    [Fact]
    public async Task CorrectionsPreserveEveryVersionOriginalSourceAndCreationReceipt()
    {
        var (financial,source)=await Seed(); var app=new TaxRecordsApplication(financial,new InMemoryTaxRecords(),TimeProvider.System);
        var input=Input(source.FinancialFactId); var first=await app.CreateAsync("T001",Customer,"actor-001",input,default);
        var change=Correction(first); var second=await app.ReviseAsync("T001",Customer,"actor-001",first.TaxRecordId,change,default);
        var third=await app.ReviseAsync("T001",Customer,"actor-001",first.TaxRecordId,Correction(second,380000m),default);
        Assert.Equal(3,third.Revision); Assert.Equal(2,second.Revision); Assert.Equal(1,first.Revision);
        Assert.Equal(third,await app.ReadAsync("T001",Customer,first.TaxRecordId,default));
        Assert.Equal(first,await app.ReadAsync("T001",Customer,first.TaxRecordId,default,1));
        Assert.Equal(first,await app.CreateAsync("T001",Customer,"actor-001",input,default));
        Assert.Equal(second,await app.ReviseAsync("T001",Customer,"actor-001",first.TaxRecordId,change,default));
        Assert.Equal(third,Assert.Single((await app.ListAsync("T001",Customer,default)).Items));
        var history=await app.HistoryAsync("T001",Customer,first.TaxRecordId,null,default);
        Assert.Equal([third,second,first],history.Items); Assert.False(history.HasMore); Assert.Null(history.NextBeforeRevision);
        Assert.All(history.Items,x=> { Assert.Equal("CUSTOMER_REVIEWED",x.VerificationStatus); Assert.Equal(first.Currency,x.Currency); Assert.Equal(first.SourceProvenanceId,x.SourceProvenanceId); Assert.Equal(first.DocumentVersionId,x.DocumentVersionId); Assert.Equal(first.EvidenceId,x.EvidenceId); Assert.Equal(first.Tax.SourceRevision,x.Tax.SourceRevision); });
        Assert.Equal(100000m,(await financial.GetFactAsync(source.FinancialFactId,1,default))!.Value);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.TaxRecordId,change with {Tax=change.Tax with {Amount=2}},default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.TaxRecordId,change with {ExpectedRevision=2},default))).Status);
    }
    [Fact]
    public async Task CompetingEditsCannotOverwriteANewerReviewedVersion()
    {
        var (financial,source)=await Seed(); var app=new TaxRecordsApplication(financial,new InMemoryTaxRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        async Task<bool> Attempt(decimal value) {try {await app.ReviseAsync("T001",Customer,"actor-001",first.TaxRecordId,Correction(first,value),default);return true;}catch(TenantBoundaryException e) when(e.Code=="TAX_RECORD_REVISION_CONFLICT"){return false;}}
        var attempts=await Task.WhenAll(Task.Run(()=>Attempt(10)),Task.Run(()=>Attempt(20)));
        Assert.Single(attempts,x=>x); Assert.Equal(2,(await app.HistoryAsync("T001",Customer,first.TaxRecordId,null,default)).Items.Count);
    }
    [Theory,InlineData(0),InlineData(-1),InlineData(int.MaxValue)]
    public async Task InvalidCorrectionVersionsCannotWrite(int expected)
    {
        var (financial,source)=await Seed();var app=new TaxRecordsApplication(financial,new InMemoryTaxRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.TaxRecordId,Correction(first) with {ExpectedRevision=expected},default))).Status);
        Assert.Single((await app.HistoryAsync("T001",Customer,first.TaxRecordId,null,default)).Items);
    }
    [Theory,InlineData(true),InlineData(false)]
    public async Task CorrectionsCannotRebindTheOriginalSource(bool identity)
    {
        var (financial,source)=await Seed();var app=new TaxRecordsApplication(financial,new InMemoryTaxRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        var change=Correction(first);change=change with {Tax=identity ? change.Tax with {SourceFinancialFactId="ff-other"}:change.Tax with {SourceRevision=2}};
        Assert.Equal("TAX_RECORD_SOURCE_IMMUTABLE",(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.TaxRecordId,change,default))).Code);
        Assert.Equal(first,await app.ReadAsync("T001",Customer,first.TaxRecordId,default));
    }
    [Fact]
    public async Task ScopedOperationsCannotBeReusedAcrossRecordsOrCreationAndCorrection()
    {
        var (financial,source)=await Seed();var app=new TaxRecordsApplication(financial,new InMemoryTaxRecords(),TimeProvider.System);
        var input=Input(source.FinancialFactId); var first=await app.CreateAsync("T001",Customer,"actor-001",input,default);
        var other=await app.CreateAsync("T001",Customer,"actor-001",input with {OperationId=Guid.NewGuid().ToString("N")},default);
        var change=Correction(first);await app.ReviseAsync("T001",Customer,"actor-001",first.TaxRecordId,change,default);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",other.TaxRecordId,change,default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",other.TaxRecordId,Correction(other) with {OperationId=input.OperationId},default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input with {OperationId=change.OperationId},default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T002",Customer,"actor-001",first.TaxRecordId,change,default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.HistoryAsync("T001","other",first.TaxRecordId,null,default))).Status);
    }
    [Fact]
    public async Task HistoryIsBoundedAndCursorPagesRetainTheOriginalVersion()
    {
        var (financial,source)=await Seed();var app=new TaxRecordsApplication(financial,new InMemoryTaxRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);var current=first;
        for(var n=0;n<54;n++) current=await app.ReviseAsync("T001",Customer,"actor-001",first.TaxRecordId,Correction(current,n),default);
        var recent=await app.HistoryAsync("T001",Customer,first.TaxRecordId,null,default);Assert.Equal(50,recent.Items.Count);Assert.True(recent.HasMore);Assert.Equal(6,recent.NextBeforeRevision);
        var earlier=await app.HistoryAsync("T001",Customer,first.TaxRecordId,recent.NextBeforeRevision,default);Assert.Equal(5,earlier.Items.Count);Assert.False(earlier.HasMore);Assert.Equal(first,earlier.Items[^1]);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.HistoryAsync("T001",Customer,first.TaxRecordId,0,default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReadAsync("T001",Customer,first.TaxRecordId,default,56))).Status);
    }
    [Fact]
    public async Task ActualRoutesCorrectReadHistoryAndReplayCreationAfterCorrection()
    {
        await using var h=await Harness.Create();using var client=h.Client();var input=Input(h.Source.FinancialFactId);
        var first=(await (await client.PostAsJsonAsync("/product-api/v1/tax-records",input,ContractJson.Options)).Content.ReadFromJsonAsync<ReviewedTaxRecord>(ContractJson.Options))!;
        var path="/product-api/v1/tax-records/"+first.TaxRecordId;var change=Correction(first);
        var response=await client.PostAsJsonAsync(path+"/corrections",change,ContractJson.Options);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var second=await response.Content.ReadFromJsonAsync<ReviewedTaxRecord>(ContractJson.Options);Assert.Equal(2,second!.Revision);Assert.Equal(change.Tax,second.Tax);
        var list=await client.GetFromJsonAsync<ProductTaxRecordList>("/product-api/v1/tax-records",ContractJson.Options);Assert.Equal(second,Assert.Single(list!.Items));
        var history=await client.GetFromJsonAsync<TaxRecordHistory>(path+"/history",ContractJson.Options);Assert.Equal([second,first],history!.Items);
        Assert.Equal(first,await (await client.PostAsJsonAsync("/product-api/v1/tax-records",input,ContractJson.Options)).Content.ReadFromJsonAsync<ReviewedTaxRecord>(ContractJson.Options));
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync(path+"/corrections",Correction(first),ContractJson.Options)).StatusCode);
    }
    [Theory,InlineData("deny",403),InlineData("advisor",403),InlineData("unavailable",503),InlineData("update",403),InlineData("evidence",403),InlineData("read",403),InlineData("missing",401),InlineData("tenant",401)]
    public async Task CorrectionRechecksCurrentAuthorityWithoutChangingTerms(string failure,int status)
    {
        await using var h=await Harness.Create();using var original=h.Client();await original.PostAsJsonAsync("/product-api/v1/tax-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);h.Authority.Failure=failure;
        using var client=h.Client(failure=="missing"?null:failure=="tenant"?"T002":"T001");
        var response=await client.PostAsJsonAsync("/product-api/v1/tax-records/"+first.TaxRecordId+"/corrections",Correction(first),ContractJson.Options);
        Assert.Equal(status,(int)response.StatusCode);Assert.Equal(first,await h.Records.ReadAsync("T001",Customer,first.TaxRecordId,default));
    }
    [Theory,InlineData(true,"duplicate"),InlineData(false,"duplicate"),InlineData(true,"currency"),InlineData(false,"recordedBy"),InlineData(true,"expected"),InlineData(false,"missing")]
    public async Task CorrectionIngressRejectsDuplicateUnknownOrMissingFields(bool product,string field)
    {
        await using var h=await Harness.Create();using var original=h.Client();await original.PostAsJsonAsync("/product-api/v1/tax-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);var change=Correction(first);
        var json=JsonSerializer.Serialize(change,ContractJson.Options);
        json=field switch {"duplicate"=>json.Insert(1,"\"EXPECTEDREVISION\":1,"),"missing"=>json.Replace("\"expectedRevision\":1,","",StringComparison.Ordinal),"expected"=>json.Replace("\"expectedRevision\":1","\"expectedRevision\":0",StringComparison.Ordinal),_=>json.Insert(1,"\""+field+"\":\"forged\",")};
        using var client=product?h.Client():h.OwnerClient();var path=product?"/product-api/v1/tax-records/":"/local/v1/customers/"+Customer+"/tax-records/";
        using var body=new StringContent(json,Encoding.UTF8,"application/json");Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync(path+first.TaxRecordId+"/corrections",body)).StatusCode);
        Assert.Equal(first,await h.Records.ReadAsync("T001",Customer,first.TaxRecordId,default));
    }
    [Fact]
    public async Task ForeignTenantCannotReadHistoryOrCorrectACollidingCustomerRecord()
    {
        await using var h=await Harness.Create();using var original=h.Client();await original.PostAsJsonAsync("/product-api/v1/tax-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);h.Authority.AllowForeignTenant=true;using var foreign=h.OwnerClient("T002");var path="/local/v1/customers/"+Customer+"/tax-records/"+first.TaxRecordId;
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync(path+"/history")).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync(path+"?revision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.PostAsJsonAsync(path+"/corrections",Correction(first),ContractJson.Options)).StatusCode);Assert.Equal(first,await h.Records.ReadAsync("T001",Customer,first.TaxRecordId,default));
    }
    [Fact]
    public async Task LostCorrectionConfirmationReplaysTheOriginalVersionAfterLaterChanges()
    {
        await using var h=await Harness.Create();using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/tax-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);var path="/product-api/v1/tax-records/"+first.TaxRecordId+"/corrections";var change=Correction(first);
        h.Authority.DenyAfterEvaluations=8;h.Authority.ResetEvaluations();
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path,change,ContractJson.Options)).StatusCode);
        var second=await h.Records.ReadAsync("T001",Customer,first.TaxRecordId,default);Assert.Equal(2,second!.Revision);
        h.Authority.DenyAfterEvaluations=0;Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync(path,Correction(second,370000),ContractJson.Options)).StatusCode);
        var replay=await client.PostAsJsonAsync(path,change,ContractJson.Options);Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Equal(second,await replay.Content.ReadFromJsonAsync<ReviewedTaxRecord>(ContractJson.Options));
        Assert.Equal(3,(await h.Records.ReadAsync("T001",Customer,first.TaxRecordId,default))!.Revision);
    }
    [Theory,InlineData("currency"),InlineData("recordedBy"),InlineData("operationId"),InlineData("source"),InlineData("revision"),InlineData("amount"),InlineData("verificationStatus"),InlineData("readback")]
    public async Task GatewayDoesNotConfirmAChangedCorrectionReceiptOrWrongFreshVersion(string variation)
    {
        await using var h=await Harness.Create("correction-"+variation);using var client=h.Client();
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/product-api/v1/tax-records",Input(h.Source.FinancialFactId),ContractJson.Options)).StatusCode);
        var original=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
        var response=await client.PostAsJsonAsync("/product-api/v1/tax-records/"+original.TaxRecordId+"/corrections",Correction(original),ContractJson.Options);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
        Assert.Equal(2,(await h.Records.ReadAsync("T001",Customer,original.TaxRecordId,default))!.Revision);
    }
    [Fact]
    public async Task HistoryRequiresCurrentReadAndDocumentAuthorityButNotUpdateAuthority()
    {
        await using var h=await Harness.Create();using var client=h.Client();
        await client.PostAsJsonAsync("/product-api/v1/tax-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);var path="/product-api/v1/tax-records/"+first.TaxRecordId;
        h.Authority.Failure="update";
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(path+"/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path+"/corrections",Correction(first),ContractJson.Options)).StatusCode);
        h.Authority.Failure="evidence";Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(path+"/history")).StatusCode);
        h.Authority.Failure="read";Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(path+"/history")).StatusCode);
        Assert.Equal(first,await h.Records.ReadAsync("T001",Customer,first.TaxRecordId,default));
    }
    [Theory,InlineData("amount"),InlineData("precision"),InlineData("large"),InlineData("type"),InlineData("year"),InlineData("amountKind"),InlineData("order"),InlineData("date")]
    public async Task InvalidCorrectedTaxValuesCannotAppendAVersion(string variation)
    {
        var (financial,source)=await Seed();var app=new TaxRecordsApplication(financial,new InMemoryTaxRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);var change=Correction(first);
        var draft=change.Tax;
        draft=variation switch {"amount"=>draft with {Amount=-1},"precision"=>draft with {Amount=1.001m},"large"=>draft with {Amount=1000000000000m},"type"=>draft with {TaxType=" "},"year"=>draft with {TaxYearLabel=" "},"amountKind"=>draft with {AmountKind="GUESS"},"order"=>draft with {PeriodEnd=draft.PeriodStart.AddDays(-1)},_=>draft with {PeriodEnd=new(2099,1,1)}};
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.TaxRecordId,change with {Tax=draft},default))).Status);
        Assert.Equal(first,await app.ReadAsync("T001",Customer,first.TaxRecordId,default));
    }

    [Theory,InlineData("PAID"),InlineData("DUE"),InlineData("WITHHELD")]
    public async Task CorrectedPeriodScheduleAndReportedYearRemainExplicitCustomerStatements(string amountKind)
    {
        var (financial,source)=await Seed();var app=new TaxRecordsApplication(financial,new InMemoryTaxRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        var correction=Correction(first);correction=correction with {Tax=correction.Tax with {Amount=1234.56m,Jurisdiction=" Revised payer ",TaxType=" Revised type ",AmountKind=amountKind,TaxYearLabel=" Year ended September 2026 ",PeriodStart=new(2026,8,1),PeriodEnd=new(2026,9,30)}};
        var revised=await app.ReviseAsync("T001",Customer,"actor-001",first.TaxRecordId,correction,default);
        Assert.Equal(1234.56m,revised.Tax.Amount);Assert.Equal(amountKind,revised.Tax.AmountKind);Assert.Equal("Year ended September 2026",revised.Tax.TaxYearLabel);
        Assert.Equal("Revised payer",revised.Tax.Jurisdiction);Assert.Equal("Revised type",revised.Tax.TaxType);Assert.Equal(new DateOnly(2026,8,1),revised.Tax.PeriodStart);
        Assert.Equal("CUSTOMER_REVIEWED",revised.VerificationStatus);Assert.Equal(first,await app.ReadAsync("T001",Customer,first.TaxRecordId,default,1));
        Assert.Equal(revised,await app.ReviseAsync("T001",Customer,"actor-001",first.TaxRecordId,correction,default));
    }    [Theory,InlineData("currency"),InlineData("source"),InlineData("evidence"),InlineData("null"),InlineData("gap"),InlineData("empty"),InlineData("cursor"),InlineData("tail")]
    public async Task TaxHistoryBindsTheOriginalSourceAndCompleteVersionWindow(string variation)
    {
        await using var h=await Harness.Create("history-"+variation);using var client=h.Client();
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/product-api/v1/tax-records",Input(h.Source.FinancialFactId),ContractJson.Options)).StatusCode);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
        var path="/product-api/v1/tax-records/"+first.TaxRecordId;
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync(path+"/corrections",Correction(first),ContractJson.Options)).StatusCode);
        var second=(await h.Records.ReadAsync("T001",Customer,first.TaxRecordId,default))!;
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync(path+"/corrections",Correction(second,380000),ContractJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.GetAsync(path+"/history")).StatusCode);
        Assert.Equal(3,(await h.Records.HistoryAsync("T001",Customer,first.TaxRecordId,null,default)).Items.Count);
    }

    [Theory,InlineData("PAID"),InlineData("DUE"),InlineData("WITHHELD")]
    public async Task ExplicitTaxMeaningAndPeriodSurviveCorrectionReplayAndHistory(string kind)
    {
        await using var h=await Harness.Create();using var client=h.Client();var input=Input(h.Source.FinancialFactId);
        input=input with {Tax=input.Tax with {AmountKind=kind,Jurisdiction=" India ",TaxType=" Income tax ",TaxYearLabel=" Custom tax period ",Amount=0m,PeriodStart=new(2025,4,1),PeriodEnd=new(2026,3,31)}};
        var first=await (await client.PostAsJsonAsync("/product-api/v1/tax-records",input,ContractJson.Options)).Content.ReadFromJsonAsync<ReviewedTaxRecord>(ContractJson.Options);Assert.NotNull(first);Assert.Equal(kind,first.Tax.AmountKind);Assert.Equal("India",first.Tax.Jurisdiction);Assert.Equal("Custom tax period",first.Tax.TaxYearLabel);Assert.Equal(0m,first.Tax.Amount);
        var change=Correction(first) with {Tax=first.Tax with {AmountKind=kind=="DUE"?"PAID":"DUE",Amount=123.45m,PeriodStart=new(2024,1,1),PeriodEnd=new(2024,12,31)}};var path="/product-api/v1/tax-records/"+first.TaxRecordId;
        var second=await (await client.PostAsJsonAsync(path+"/corrections",change,ContractJson.Options)).Content.ReadFromJsonAsync<ReviewedTaxRecord>(ContractJson.Options);Assert.NotNull(second);Assert.Equal(change.Tax,second.Tax);
        Assert.Equal(second,await (await client.PostAsJsonAsync(path+"/corrections",change,ContractJson.Options)).Content.ReadFromJsonAsync<ReviewedTaxRecord>(ContractJson.Options));
        var history=await client.GetFromJsonAsync<TaxRecordHistory>(path+"/history",ContractJson.Options);Assert.Equal([second,first],history!.Items);Assert.Equal(first.Currency,second.Currency);Assert.Equal(first.SourceProvenanceId,second.SourceProvenanceId);Assert.Equal("CUSTOMER_REVIEWED",second.VerificationStatus);
    }
    [Theory,InlineData("MONTHLY"),InlineData("paid"),InlineData("REFUND"),InlineData("")]
    public async Task UnsupportedOrAmbiguousAmountKindsCannotCreateRecords(string kind)
    {
        var(financial,source)=await Seed();var app=new TaxRecordsApplication(financial,new InMemoryTaxRecords(),TimeProvider.System);var input=Input(source.FinancialFactId);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input with {Tax=input.Tax with {AmountKind=kind}},default))).Status);Assert.Empty((await app.ListAsync("T001",Customer,default)).Items);
    }

}
