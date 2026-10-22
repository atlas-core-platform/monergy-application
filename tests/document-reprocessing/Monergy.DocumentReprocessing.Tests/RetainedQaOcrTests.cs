using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence;
using Monergy.Services.DocumentIntelligence.Application;
using Xunit;
namespace Monergy.DocumentReprocessing.Tests;

public sealed class RetainedQaOcrTests
{
    private static readonly string[] ExpectedTexts = ["First page", "Second page"];
    private static readonly string[] ExpectedLocations = ["page 1, lines 1-1", "page 2, lines 1-1"];
    [Fact]
    public async Task ReadsEveryRetainedPageWithExactHashAndLocationsWithoutNativeDependencies()
    {
        var store = new Store();
        var result = await new QaDocumentReader(new Evidence(), store).Read("customer", "version", Security(), default);
        Assert.Equal("AVAILABLE", result.State); Assert.Equal(QaDocumentReader.RetainedOcrVersion, result.ReaderVersion);
        Assert.Equal(ExpectedTexts, result.Passages.Select(x => x.Text));
        Assert.Equal(ExpectedLocations, result.Passages.Select(x => x.Location));
        Assert.Equal(2, result.Passages.Select(x => x.Id).Distinct().Count());
        Assert.Equal(("T001", "customer", "version"), store.Key);
    }
    [Fact]
    public async Task MissingOutputIsExplicitAndDoesNotStartProcessing()
    {
        var result = await new QaDocumentReader(new Evidence(), new Store { Value = null }).Read("customer", "version", Security(), default);
        Assert.Equal("NOT_PROCESSED", result.State); Assert.Empty(result.Passages);
    }
    [Theory]
    [InlineData("tenant")][InlineData("customer")][InlineData("version")][InlineData("hash")]
    [InlineData("engine")][InlineData("page")][InlineData("state")]
    public async Task ForeignStaleOrMalformedOutputCannotBeQuoted(string mutation)
    {
        var original = Row();
        var row = mutation switch {
            "tenant" => original with { TenantId = "T002" }, "customer" => original with { CustomerId = "foreign" },
            "version" => original with { DocumentVersionId = "old" }, "hash" => original with { SourceHash = new string('b',64) },
            "engine" => original with { EngineVersion = "other" }, "page" => original with { Pages = [new(2,[new(1,"secret",99)])] },
            _ => original with { State = "NO_TEXT" }
        };
        var failure = await Assert.ThrowsAsync<TenantBoundaryException>(() => new QaDocumentReader(new Evidence(),new Store { Value=row }).Read("customer","version",Security(),default));
        Assert.Equal("AI_DOCUMENT_INVALID",failure.Code);
    }
    [Theory]
    [InlineData("UNRELIABLE_TEXT")][InlineData("NO_TEXT")][InlineData("UNSUPPORTED")]
    public async Task UncertainOrEmptyOutputNeverDropsMissingWordsOrPages(string state)
    {
        var row=Row() with { State=state, Pages=state=="UNSUPPORTED"?[]:state=="NO_TEXT"?[new(1,[])]:[new(1,[new(1,"uncertain",50)]),new(2,[])] };
        var result=await new QaDocumentReader(new Evidence(),new Store { Value=row }).Read("customer","version",Security(),default);
        Assert.Equal(state,result.State);Assert.Empty(result.Passages);
    }
    [Fact]
    public async Task OversizedSecondPageRejectsWholeQuotationInsteadOfReturningFirstPage()
    {
        var row=Row() with { Pages=[new(1,[new(1,"First page",99)]),new(2,[new(1,new string('x',1001),99)])] };
        var result=await new QaDocumentReader(new Evidence(),new Store { Value=row }).Read("customer","version",Security(),default);
        Assert.Equal("UNSUPPORTED",result.State);Assert.Empty(result.Passages);
    }
    [Fact]
    public async Task MoreThanTwentyPassagesDoesNotReturnAPrefix()
    {
        var row=Row() with { Pages=Enumerable.Range(1,20).Select(n=>new DocumentOcrPage(n,[new(1,new string('x',600),99),new(2,new string('y',600),99)])).ToArray() };
        var result=await new QaDocumentReader(new Evidence(),new Store { Value=row }).Read("customer","version",Security(),default);
        Assert.Equal("UNSUPPORTED",result.State);Assert.Empty(result.Passages);
    }
    [Fact]
    public async Task StorageFailureIsNotMisrepresentedAsMissingText()
    {
        var failure=await Assert.ThrowsAsync<TenantBoundaryException>(()=>new QaDocumentReader(new Evidence(),new Store { Fail=true }).Read("customer","version",Security(),default));
        Assert.Equal("AI_DOCUMENT_UNAVAILABLE",failure.Code);
    }
    [Fact]
    public async Task InvalidTenantProofRejectsBeforeRepositoryRead()
    {
        var store=new Store();var security=Security() with { Workload=new("ai","forged:T001") };
        await Assert.ThrowsAsync<TenantBoundaryException>(()=>new QaDocumentReader(new Evidence(),store).Read("customer","version",security,default));Assert.Null(store.Key);
    }
    [Fact]
    public async Task CancellationPropagates()
    {
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new QaDocumentReader(new Evidence(),new Store()).Read("customer","version",Security(),cancellation.Token));
    }
    private static DocumentOcrResult Row()=>new("T001","customer","version",new string('a',64),DocumentOcrRules.Version,"AVAILABLE",DateTimeOffset.UtcNow,
        [new(1,[new(1,"First page",99)]),new(2,[new(1,"Second page",99)])]);
    private static TrustedSecurityContext Security()=>new(new("actor","Customer",DateTimeOffset.UtcNow,"session"),new("ai","local-uat-workspace:T001"),new("customer-advice",null,"decision","customer"));
    private sealed class Store:IDocumentOcrRepository
    {
        public DocumentOcrResult? Value { get;init; }=Row();public bool Fail { get;init; }
        public (string Tenant,string Customer,string Version)? Key { get;private set; }
        public Task SaveAsync(DocumentOcrResult result,CancellationToken ct)=>throw new NotSupportedException();
        public Task<DocumentOcrResult?> ReadOcrAsync(string tenant,string customer,string version,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();Key=(tenant,customer,version);if(Fail)throw new InvalidOperationException();return Task.FromResult(Value);}
    }
    private sealed class Evidence:IEvidenceContentReader
    {
        public Task<EvidenceContent?> ReadAsync(string version,string customer,TrustedSecurityContext security,string correlation,CancellationToken ct)=>
            Task.FromResult<EvidenceContent?>(new(new("evidence","document","version","customer","reference://test",new string('a',64),"application/pdf"),"",[1,2,3]));
    }
}
