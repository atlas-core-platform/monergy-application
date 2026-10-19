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

public sealed partial class ReviewedInsuranceRecordTests
{
    private static ReviseInsuranceRecord Correction(ReviewedInsuranceRecord record, decimal outstanding = 390000m) =>
        new(Guid.NewGuid().ToString("N"),record.Revision,record.Insurance with { PremiumAmount = outstanding, Name = "Corrected family policy" });
    [Fact]
    public async Task ExistingCreationPayloadsWithoutVersionRemainVersionOne()
    {
        var (financial,source)=await Seed();var app=new InsuranceRecordsApplication(financial,new InMemoryInsuranceRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        var legacy=JsonSerializer.Serialize(first,ContractJson.Options).Replace(",\"revision\":1}","}",StringComparison.Ordinal);
        Assert.DoesNotContain("\"revision\"",legacy,StringComparison.Ordinal);
        Assert.Equal(first,JsonSerializer.Deserialize<ReviewedInsuranceRecord>(legacy,ContractJson.Options));
    }
    [Fact]
    public async Task CorrectionsPreserveEveryVersionOriginalSourceAndCreationReceipt()
    {
        var (financial,source)=await Seed(); var app=new InsuranceRecordsApplication(financial,new InMemoryInsuranceRecords(),TimeProvider.System);
        var input=Input(source.FinancialFactId); var first=await app.CreateAsync("T001",Customer,"actor-001",input,default);
        var change=Correction(first); var second=await app.ReviseAsync("T001",Customer,"actor-001",first.InsuranceRecordId,change,default);
        var third=await app.ReviseAsync("T001",Customer,"actor-001",first.InsuranceRecordId,Correction(second,380000m),default);
        Assert.Equal(3,third.Revision); Assert.Equal(2,second.Revision); Assert.Equal(1,first.Revision);
        Assert.Equal(third,await app.ReadAsync("T001",Customer,first.InsuranceRecordId,default));
        Assert.Equal(first,await app.ReadAsync("T001",Customer,first.InsuranceRecordId,default,1));
        Assert.Equal(first,await app.CreateAsync("T001",Customer,"actor-001",input,default));
        Assert.Equal(second,await app.ReviseAsync("T001",Customer,"actor-001",first.InsuranceRecordId,change,default));
        Assert.Equal(third,Assert.Single((await app.ListAsync("T001",Customer,default)).Items));
        var history=await app.HistoryAsync("T001",Customer,first.InsuranceRecordId,null,default);
        Assert.Equal([third,second,first],history.Items); Assert.False(history.HasMore); Assert.Null(history.NextBeforeRevision);
        Assert.All(history.Items,x=> { Assert.Equal(first.Currency,x.Currency); Assert.Equal(first.SourceProvenanceId,x.SourceProvenanceId); Assert.Equal(first.DocumentVersionId,x.DocumentVersionId); Assert.Equal(first.EvidenceId,x.EvidenceId); Assert.Equal(first.Insurance.SourceRevision,x.Insurance.SourceRevision); });
        Assert.Equal(100000m,(await financial.GetFactAsync(source.FinancialFactId,1,default))!.Value);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.InsuranceRecordId,change with {Insurance=change.Insurance with {SumAssured=2}},default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.InsuranceRecordId,change with {ExpectedRevision=2},default))).Status);
    }
    [Fact]
    public async Task CompetingEditsCannotOverwriteANewerReviewedVersion()
    {
        var (financial,source)=await Seed(); var app=new InsuranceRecordsApplication(financial,new InMemoryInsuranceRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        async Task<bool> Attempt(decimal value) {try {await app.ReviseAsync("T001",Customer,"actor-001",first.InsuranceRecordId,Correction(first,value),default);return true;}catch(TenantBoundaryException e) when(e.Code=="INSURANCE_POLICY_REVISION_CONFLICT"){return false;}}
        var attempts=await Task.WhenAll(Task.Run(()=>Attempt(10)),Task.Run(()=>Attempt(20)));
        Assert.Single(attempts,x=>x); Assert.Equal(2,(await app.HistoryAsync("T001",Customer,first.InsuranceRecordId,null,default)).Items.Count);
    }
    [Theory,InlineData(0),InlineData(-1),InlineData(int.MaxValue)]
    public async Task InvalidCorrectionVersionsCannotWrite(int expected)
    {
        var (financial,source)=await Seed();var app=new InsuranceRecordsApplication(financial,new InMemoryInsuranceRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.InsuranceRecordId,Correction(first) with {ExpectedRevision=expected},default))).Status);
        Assert.Single((await app.HistoryAsync("T001",Customer,first.InsuranceRecordId,null,default)).Items);
    }
    [Theory,InlineData(true),InlineData(false)]
    public async Task CorrectionsCannotRebindTheOriginalSource(bool identity)
    {
        var (financial,source)=await Seed();var app=new InsuranceRecordsApplication(financial,new InMemoryInsuranceRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        var change=Correction(first);change=change with {Insurance=identity ? change.Insurance with {SourceFinancialFactId="ff-other"}:change.Insurance with {SourceRevision=2}};
        Assert.Equal("INSURANCE_POLICY_SOURCE_IMMUTABLE",(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.InsuranceRecordId,change,default))).Code);
        Assert.Equal(first,await app.ReadAsync("T001",Customer,first.InsuranceRecordId,default));
    }
    [Fact]
    public async Task ScopedOperationsCannotBeReusedAcrossRecordsOrCreationAndCorrection()
    {
        var (financial,source)=await Seed();var app=new InsuranceRecordsApplication(financial,new InMemoryInsuranceRecords(),TimeProvider.System);
        var input=Input(source.FinancialFactId); var first=await app.CreateAsync("T001",Customer,"actor-001",input,default);
        var other=await app.CreateAsync("T001",Customer,"actor-001",input with {OperationId=Guid.NewGuid().ToString("N")},default);
        var change=Correction(first);await app.ReviseAsync("T001",Customer,"actor-001",first.InsuranceRecordId,change,default);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",other.InsuranceRecordId,change,default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",other.InsuranceRecordId,Correction(other) with {OperationId=input.OperationId},default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input with {OperationId=change.OperationId},default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T002",Customer,"actor-001",first.InsuranceRecordId,change,default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.HistoryAsync("T001","other",first.InsuranceRecordId,null,default))).Status);
    }
    [Fact]
    public async Task HistoryIsBoundedAndCursorPagesRetainTheOriginalVersion()
    {
        var (financial,source)=await Seed();var app=new InsuranceRecordsApplication(financial,new InMemoryInsuranceRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);var current=first;
        for(var n=0;n<54;n++) current=await app.ReviseAsync("T001",Customer,"actor-001",first.InsuranceRecordId,Correction(current,n),default);
        var recent=await app.HistoryAsync("T001",Customer,first.InsuranceRecordId,null,default);Assert.Equal(50,recent.Items.Count);Assert.True(recent.HasMore);Assert.Equal(6,recent.NextBeforeRevision);
        var earlier=await app.HistoryAsync("T001",Customer,first.InsuranceRecordId,recent.NextBeforeRevision,default);Assert.Equal(5,earlier.Items.Count);Assert.False(earlier.HasMore);Assert.Equal(first,earlier.Items[^1]);
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.HistoryAsync("T001",Customer,first.InsuranceRecordId,0,default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReadAsync("T001",Customer,first.InsuranceRecordId,default,56))).Status);
    }
    [Fact]
    public async Task ActualRoutesCorrectReadHistoryAndReplayCreationAfterCorrection()
    {
        await using var h=await Harness.Create();using var client=h.Client();var input=Input(h.Source.FinancialFactId);
        var first=(await (await client.PostAsJsonAsync("/product-api/v1/insurance-records",input,ContractJson.Options)).Content.ReadFromJsonAsync<ReviewedInsuranceRecord>(ContractJson.Options))!;
        var path="/product-api/v1/insurance-records/"+first.InsuranceRecordId;var change=Correction(first);
        var response=await client.PostAsJsonAsync(path+"/corrections",change,ContractJson.Options);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var second=await response.Content.ReadFromJsonAsync<ReviewedInsuranceRecord>(ContractJson.Options);Assert.Equal(2,second!.Revision);Assert.Equal(change.Insurance,second.Insurance);
        var list=await client.GetFromJsonAsync<ProductInsuranceRecordList>("/product-api/v1/insurance-records",ContractJson.Options);Assert.Equal(second,Assert.Single(list!.Items));
        var history=await client.GetFromJsonAsync<InsuranceRecordHistory>(path+"/history",ContractJson.Options);Assert.Equal([second,first],history!.Items);
        Assert.Equal(first,await (await client.PostAsJsonAsync("/product-api/v1/insurance-records",input,ContractJson.Options)).Content.ReadFromJsonAsync<ReviewedInsuranceRecord>(ContractJson.Options));
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync(path+"/corrections",Correction(first),ContractJson.Options)).StatusCode);
    }
    [Theory,InlineData("deny",403),InlineData("advisor",403),InlineData("unavailable",503),InlineData("update",403),InlineData("evidence",403),InlineData("read",403),InlineData("missing",401),InlineData("tenant",401)]
    public async Task CorrectionRechecksCurrentAuthorityWithoutChangingTerms(string failure,int status)
    {
        await using var h=await Harness.Create();using var original=h.Client();await original.PostAsJsonAsync("/product-api/v1/insurance-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);h.Authority.Failure=failure;
        using var client=h.Client(failure=="missing"?null:failure=="tenant"?"T002":"T001");
        var response=await client.PostAsJsonAsync("/product-api/v1/insurance-records/"+first.InsuranceRecordId+"/corrections",Correction(first),ContractJson.Options);
        Assert.Equal(status,(int)response.StatusCode);Assert.Equal(first,await h.Records.ReadAsync("T001",Customer,first.InsuranceRecordId,default));
    }
    [Theory,InlineData(true,"duplicate"),InlineData(false,"duplicate"),InlineData(true,"currency"),InlineData(false,"recordedBy"),InlineData(true,"expected"),InlineData(false,"missing")]
    public async Task CorrectionIngressRejectsDuplicateUnknownOrMissingFields(bool product,string field)
    {
        await using var h=await Harness.Create();using var original=h.Client();await original.PostAsJsonAsync("/product-api/v1/insurance-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);var change=Correction(first);
        var json=JsonSerializer.Serialize(change,ContractJson.Options);
        json=field switch {"duplicate"=>json.Insert(1,"\"EXPECTEDREVISION\":1,"),"missing"=>json.Replace("\"expectedRevision\":1,","",StringComparison.Ordinal),"expected"=>json.Replace("\"expectedRevision\":1","\"expectedRevision\":0",StringComparison.Ordinal),_=>json.Insert(1,"\""+field+"\":\"forged\",")};
        using var client=product?h.Client():h.OwnerClient();var path=product?"/product-api/v1/insurance-records/":"/local/v1/customers/"+Customer+"/insurance-records/";
        using var body=new StringContent(json,Encoding.UTF8,"application/json");Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync(path+first.InsuranceRecordId+"/corrections",body)).StatusCode);
        Assert.Equal(first,await h.Records.ReadAsync("T001",Customer,first.InsuranceRecordId,default));
    }
    [Fact]
    public async Task ForeignTenantCannotReadHistoryOrCorrectACollidingCustomerRecord()
    {
        await using var h=await Harness.Create();using var original=h.Client();await original.PostAsJsonAsync("/product-api/v1/insurance-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);h.Authority.AllowForeignTenant=true;using var foreign=h.OwnerClient("T002");var path="/local/v1/customers/"+Customer+"/insurance-records/"+first.InsuranceRecordId;
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync(path+"/history")).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync(path+"?revision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.PostAsJsonAsync(path+"/corrections",Correction(first),ContractJson.Options)).StatusCode);Assert.Equal(first,await h.Records.ReadAsync("T001",Customer,first.InsuranceRecordId,default));
    }
    [Fact]
    public async Task LostCorrectionConfirmationReplaysTheOriginalVersionAfterLaterChanges()
    {
        await using var h=await Harness.Create();using var client=h.Client();await client.PostAsJsonAsync("/product-api/v1/insurance-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);var path="/product-api/v1/insurance-records/"+first.InsuranceRecordId+"/corrections";var change=Correction(first);
        h.Authority.DenyAfterEvaluations=8;h.Authority.ResetEvaluations();
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path,change,ContractJson.Options)).StatusCode);
        var second=await h.Records.ReadAsync("T001",Customer,first.InsuranceRecordId,default);Assert.Equal(2,second!.Revision);
        h.Authority.DenyAfterEvaluations=0;Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync(path,Correction(second,370000),ContractJson.Options)).StatusCode);
        var replay=await client.PostAsJsonAsync(path,change,ContractJson.Options);Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Equal(second,await replay.Content.ReadFromJsonAsync<ReviewedInsuranceRecord>(ContractJson.Options));
        Assert.Equal(3,(await h.Records.ReadAsync("T001",Customer,first.InsuranceRecordId,default))!.Revision);
    }
    [Theory,InlineData("currency"),InlineData("recordedBy"),InlineData("operationId"),InlineData("source"),InlineData("revision"),InlineData("amount"),InlineData("readback")]
    public async Task GatewayDoesNotConfirmAChangedCorrectionReceiptOrWrongFreshVersion(string variation)
    {
        await using var h=await Harness.Create("correction-"+variation);using var client=h.Client();
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/product-api/v1/insurance-records",Input(h.Source.FinancialFactId),ContractJson.Options)).StatusCode);
        var original=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
        var response=await client.PostAsJsonAsync("/product-api/v1/insurance-records/"+original.InsuranceRecordId+"/corrections",Correction(original),ContractJson.Options);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
        Assert.Equal(2,(await h.Records.ReadAsync("T001",Customer,original.InsuranceRecordId,default))!.Revision);
    }
    [Fact]
    public async Task HistoryRequiresCurrentReadAndDocumentAuthorityButNotUpdateAuthority()
    {
        await using var h=await Harness.Create();using var client=h.Client();
        await client.PostAsJsonAsync("/product-api/v1/insurance-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);var path="/product-api/v1/insurance-records/"+first.InsuranceRecordId;
        h.Authority.Failure="update";
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(path+"/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path+"/corrections",Correction(first),ContractJson.Options)).StatusCode);
        h.Authority.Failure="evidence";Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(path+"/history")).StatusCode);
        h.Authority.Failure="read";Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(path+"/history")).StatusCode);
        Assert.Equal(first,await h.Records.ReadAsync("T001",Customer,first.InsuranceRecordId,default));
    }
    [Theory,InlineData("amount"),InlineData("precision"),InlineData("type"),InlineData("date"),InlineData("provider"),InlineData("control")]
    public async Task InvalidCorrectedInsuranceValuesCannotAppendAVersion(string variation)
    {
        var (financial,source)=await Seed();var app=new InsuranceRecordsApplication(financial,new InMemoryInsuranceRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);var change=Correction(first);
        var draft=change.Insurance;
        draft=variation switch {"amount"=>draft with {PremiumAmount=-1},"precision"=>draft with {SumAssured=1.001m},"type"=>draft with {PolicyType=" "},"provider"=>draft with {Provider=new string('x',121)},"control"=>draft with {Provider="a\nb"},_=>draft with {PolicyAsOfDate=new(2099,1,1)}};
        Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.InsuranceRecordId,change with {Insurance=draft},default))).Status);
        Assert.Equal(first,await app.ReadAsync("T001",Customer,first.InsuranceRecordId,default));
    }

    [Theory,InlineData("currency"),InlineData("source"),InlineData("evidence"),InlineData("null"),InlineData("gap"),InlineData("empty"),InlineData("cursor"),InlineData("tail")]
    public async Task InsuranceHistoryBindsTheOriginalSourceAndCompleteVersionWindow(string variation)
    {
        await using var h=await Harness.Create("history-"+variation);using var client=h.Client();
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/product-api/v1/insurance-records",Input(h.Source.FinancialFactId),ContractJson.Options)).StatusCode);
        var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
        var path="/product-api/v1/insurance-records/"+first.InsuranceRecordId;
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync(path+"/corrections",Correction(first),ContractJson.Options)).StatusCode);
        var second=(await h.Records.ReadAsync("T001",Customer,first.InsuranceRecordId,default))!;
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync(path+"/corrections",Correction(second,380000),ContractJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.GetAsync(path+"/history")).StatusCode);
        Assert.Equal(3,(await h.Records.HistoryAsync("T001",Customer,first.InsuranceRecordId,null,default)).Items.Count);
    }
    [Fact]
    public async Task ExplicitInsuranceDetailsAndValuesCanBeCorrectedWithoutChangingTheOriginalSourceOrDateMeaning()
    {
        var (financial,source)=await Seed();var app=new InsuranceRecordsApplication(financial,new InMemoryInsuranceRecords(),TimeProvider.System);
        var first=await app.CreateAsync("T001",Customer,"actor-001",Input(source.FinancialFactId),default);
        var change=Correction(first) with {Insurance=first.Insurance with {Name=" Corrected policy ",Provider=" New provider ",PolicyType=" Term ",SumAssured=0m,PremiumAmount=123m,PolicyAsOfDate=new(2020,1,1)}};
        var second=await app.ReviseAsync("T001",Customer,"actor-001",first.InsuranceRecordId,change,default);
        Assert.Equal("Corrected policy",second.Insurance.Name);Assert.Equal("New provider",second.Insurance.Provider);Assert.Equal("Term",second.Insurance.PolicyType);
        Assert.Equal(0m,second.Insurance.SumAssured);Assert.Equal(123m,second.Insurance.PremiumAmount);Assert.Equal(new DateOnly(2020,1,1),second.Insurance.PolicyAsOfDate);
        Assert.Equal(first.Currency,second.Currency);Assert.Equal(first.SourceProvenanceId,second.SourceProvenanceId);Assert.Equal(first.Insurance.SourceRevision,second.Insurance.SourceRevision);
        Assert.Equal(first,await app.ReadAsync("T001",Customer,first.InsuranceRecordId,default,1));
    }

    [Theory,InlineData(null),InlineData("2050-12-31"),InlineData("2020-01-01")]
    public async Task MaturityIsExplicitOptionalAndDoesNotDetermineCoverageOrPremiumMeaning(string? date)
    {
        await using var h=await Harness.Create();using var client=h.Client();var input=Input(h.Source.FinancialFactId);
        input=input with {Insurance=input.Insurance with {SumAssured=0,PremiumAmount=123.45m,MaturityDate=date is null?null:DateOnly.ParseExact(date,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture)}};
        var first=await (await client.PostAsJsonAsync("/product-api/v1/insurance-records",input,ContractJson.Options)).Content.ReadFromJsonAsync<ReviewedInsuranceRecord>(ContractJson.Options);Assert.NotNull(first);
        Assert.Equal(input.Insurance,first.Insurance);Assert.Equal(0m,first.Insurance.SumAssured);Assert.Equal(123.45m,first.Insurance.PremiumAmount);
        var path="/product-api/v1/insurance-records/"+first.InsuranceRecordId;var change=Correction(first) with {Insurance=first.Insurance with {MaturityDate=new(2045,6,30)}};
        var second=await (await client.PostAsJsonAsync(path+"/corrections",change,ContractJson.Options)).Content.ReadFromJsonAsync<ReviewedInsuranceRecord>(ContractJson.Options);Assert.NotNull(second);Assert.Equal(new DateOnly(2045,6,30),second.Insurance.MaturityDate);
        var cleared=await (await client.PostAsJsonAsync(path+"/corrections",Correction(second) with {Insurance=second.Insurance with {MaturityDate=null}},ContractJson.Options)).Content.ReadFromJsonAsync<ReviewedInsuranceRecord>(ContractJson.Options);Assert.NotNull(cleared);Assert.Null(cleared.Insurance.MaturityDate);
        Assert.Equal(second,await (await client.PostAsJsonAsync(path+"/corrections",change,ContractJson.Options)).Content.ReadFromJsonAsync<ReviewedInsuranceRecord>(ContractJson.Options));
        var history=await client.GetFromJsonAsync<InsuranceRecordHistory>(path+"/history",ContractJson.Options);Assert.Equal([cleared,second,first],history!.Items);
        Assert.Equal(first.Currency,cleared.Currency);Assert.Equal(first.SourceProvenanceId,cleared.SourceProvenanceId);
    }
    [Theory,InlineData(true),InlineData(false)]
    public async Task MaturityBefore1900CannotCreateOrAppend(bool create)
    {
        var(financial,source)=await Seed();var app=new InsuranceRecordsApplication(financial,new InMemoryInsuranceRecords(),TimeProvider.System);var input=Input(source.FinancialFactId);
        var first=await app.CreateAsync("T001",Customer,"actor-001",input,default);var invalid=input.Insurance with {MaturityDate=new(1899,12,31)};
        var error=create?await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input with {OperationId=Guid.NewGuid().ToString("N"),Insurance=invalid},default)):
            await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.ReviseAsync("T001",Customer,"actor-001",first.InsuranceRecordId,Correction(first) with {Insurance=invalid},default));
        Assert.Equal(400,error.Status);Assert.Single((await app.HistoryAsync("T001",Customer,first.InsuranceRecordId,null,default)).Items);
    }
    [Theory,InlineData(true),InlineData(false)]
    public async Task OptionalMaturityMustStillBeExplicitInStrictRequests(bool product)
    {
        await using var h=await Harness.Create();using var client=product?h.Client():h.OwnerClient();var json=JsonSerializer.Serialize(Input(h.Source.FinancialFactId),ContractJson.Options).Replace("\"maturityDate\":null,","",StringComparison.Ordinal);
        using var body=new StringContent(json,Encoding.UTF8,"application/json");var path=product?"/product-api/v1/insurance-records":"/local/v1/customers/"+Customer+"/insurance-records";
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync(path,body)).StatusCode);Assert.Empty((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    [Theory,InlineData(true),InlineData(false)]
    public async Task GatewayCannotConfirmASubstitutedMaturityReceipt(bool correction)
    {
        await using var h=await Harness.Create(correction?"correction-maturity":"maturity");using var client=h.Client();var created=await client.PostAsJsonAsync("/product-api/v1/insurance-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        if(!correction)Assert.Equal(HttpStatusCode.ServiceUnavailable,created.StatusCode);
        else {Assert.Equal(HttpStatusCode.OK,created.StatusCode);var first=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.PostAsJsonAsync("/product-api/v1/insurance-records/"+first.InsuranceRecordId+"/corrections",Correction(first),ContractJson.Options)).StatusCode);}
    }

}
