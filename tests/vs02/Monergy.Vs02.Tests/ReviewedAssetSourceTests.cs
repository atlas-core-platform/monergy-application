using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Services.FinancialProfile;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class ReviewedAssetSourceTests
{
    [Theory]
    [InlineData("text", "PROPERTY")]
    [InlineData("text", "VEHICLE")]
    [InlineData("text", "BANK_ACCOUNT")]
    [InlineData("csv", "PROPERTY")]
    [InlineData("csv", "VEHICLE")]
    [InlineData("csv", "BANK_ACCOUNT")]
    [InlineData("xlsx", "PROPERTY")]
    [InlineData("xlsx", "VEHICLE")]
    [InlineData("xlsx", "BANK_ACCOUNT")]
    public async Task ExplicitAssetTypeSurvivesOwnerHttpAdoptionReplayConflictAndReadback(string format, string ownerType)
    {
        var original = Assert.Single(await new StructuredDocumentExtractor().ExtractAsync(FinancialSourceCategoryTests.Source(format,"ASSET"),"source-run",default));
        var repository = new InMemoryFinancialProfileRepository();
        var application = new FinancialProfileApplication(repository,new CapturingTelemetry(),TimeProvider.System);
        var builder = WebApplication.CreateSlimBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(application);
        await using var host = builder.Build(); host.MapFinancialProfileContracts(); await host.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
            var selection = new[] { (original.SourceFactId, original.FactType, (string?)original.ExtractionVersion) };
            var types = FinancialSourceFactBoundary.ReviewedTypes(selection, [(original.SourceFactId, ownerType)]);
            var selected = original with { FactType = types[original.SourceFactId] };
            var request = Vs02TestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
                new NormalizeSourceFacts("category-profile","customer-001","source-run",[selected],"reviewed-document-facts/1.0.0",DateTimeOffset.UtcNow),"category-http","category-http");
            var created = await Send<NormalizeSourceFacts,NormalizationResult>(client,"cid-031",request);
            Assert.Equal(ContractOutcome.Success,created.Outcome);
            var saved = Assert.Single(created.Data!.Facts); Assert.Equal(ownerType,saved.FactType); Assert.True(saved.Created);
            var replay = await Send<NormalizeSourceFacts,NormalizationResult>(client,"cid-031",request);
            Assert.Equal(ContractOutcome.Success,replay.Outcome); Assert.False(Assert.Single(replay.Data!.Facts).Created);
            var changed = FinancialSourceFactBoundary.ReviewedTypes(selection, [(original.SourceFactId, ownerType == "PROPERTY" ? "VEHICLE" : "PROPERTY")]);
            var conflict = await Send<NormalizeSourceFacts,NormalizationResult>(client,"cid-031",request with { Payload = request.Payload with { Facts = [original with { FactType = changed[original.SourceFactId] }] } });
            Assert.Equal(ContractErrorCategory.Conflict, conflict.Error?.Category);
            var changedOperation = await Send<NormalizeSourceFacts,NormalizationResult>(client,"cid-031",request with {
                RequestId = "changed-type-request", IdempotencyKey = "changed-type-operation",
                Payload = request.Payload with { Facts = [original with { FactType = changed[original.SourceFactId] }] } });
            Assert.Equal(ContractErrorCategory.Conflict, changedOperation.Error?.Category);
            var profile = await Send<GetFinancialProfile,FinancialProfileDetails>(client,"cid-030",Vs02TestContext.Request(
                Vs02ContractNames.GetFinancialProfile,new GetFinancialProfile("category-profile","customer-001"),"category-profile-read"));
            var fact = Assert.Single(profile.Data!.Facts); Assert.Equal(1,profile.Data.Revision); Assert.Equal(ownerType,fact.FactType);
            Assert.Equal("ASSET",FinancialSourceFactBoundary.ForCustomer(fact.FactType)); Assert.Equal(ownerType,FinancialSourceFactBoundary.CustomerAssetType(fact.FactType));
            Assert.Equal(original.Label,fact.Label); Assert.Equal(original.CandidateValue,fact.Value); Assert.Equal(original.Currency,fact.Currency); Assert.Equal(original.EffectiveDate,fact.EffectiveDate);
            var detail = await Send<GetFinancialFact,AuthoritativeFinancialFact>(client,"cid-032",Vs02TestContext.Request(
                Vs02ContractNames.GetFinancialFact,new GetFinancialFact(fact.FinancialFactId,"customer-001",1),"category-detail"));
            Assert.Equal(fact,detail.Data);
            var provenance = await Send<GetFinancialProvenance,FinancialProvenance>(client,"cid-033",Vs02TestContext.Request(
                Vs02ContractNames.GetFinancialProvenance,new GetFinancialProvenance(fact.FinancialProvenanceId,"customer-001"),"category-provenance"));
            Assert.Equal(original.DocumentVersionId,provenance.Data!.DocumentVersionId); Assert.Equal(original.EvidenceId,provenance.Data.EvidenceId);
            Assert.Equal(original.SourceFactId,provenance.Data.SourceFactId); Assert.Equal(original.ValidationVersion,provenance.Data.ValidationVersion); Assert.Equal(original.ExtractionVersion,provenance.Data.ExtractionVersion);
            var hidden = await Send<GetFinancialFact,AuthoritativeFinancialFact>(client,"cid-032",Vs02TestContext.Request(
                Vs02ContractNames.GetFinancialFact,new GetFinancialFact(fact.FinancialFactId,"customer-002",1),"category-hidden",security:Vs02TestContext.Security("customer-002")));
            Assert.Equal(ContractErrorCategory.NotFound,hidden.Error?.Category);
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData("fixture-extractor/1.0.0")]
    [InlineData("financial-csv/1.0.0")]
    [InlineData("financial-xlsx/1.0.0")]
    public void GenericAssetNeedsAnExplicitType(string version)
    {
        var error=Assert.Throws<FinancialSourceSelectionException>(()=>FinancialSourceFactBoundary.ReviewedTypes([("asset","ASSET",version)],null));
        Assert.Equal("FINANCIAL_REVIEW_ASSET_TYPE_REQUIRED",error.Code);
    }
    [Theory]
    [InlineData("unknown", "PROPERTY", "ASSET", "financial-csv/1.0.0")]
    [InlineData("asset", "INVESTMENT", "ASSET", "financial-csv/1.0.0")]
    [InlineData("asset", "PROPERTY", "INCOME", "financial-csv/1.0.0")]
    [InlineData("asset", "VEHICLE", "ASSET", "property-sale-deed-png-ocr/1.0.0")]
    [InlineData("asset", "PROPERTY", "ASSET", "vehicle-purchase-invoice-png-ocr/1.0.0")]
    [InlineData("asset", "PROPERTY", "ASSET", "unknown/1.0.0")]
    public void UnknownUnselectedUnsupportedOrDocumentDerivedChoicesAreRejected(string choiceId,string choice,string type,string version)
    {
        Assert.Throws<FinancialSourceSelectionException>(()=>FinancialSourceFactBoundary.ReviewedTypes([("asset",type,version)],[(choiceId,choice)]));
    }
    [Fact]
    public void DuplicateSourceOrChoiceIdsAndOversizedSelectionsAreRejected()
    {
        Assert.Throws<FinancialSourceSelectionException>(()=>FinancialSourceFactBoundary.ReviewedTypes([("asset","ASSET","financial-csv/1.0.0")],[("asset","PROPERTY"),("asset","VEHICLE")]));
        Assert.Throws<FinancialSourceSelectionException>(()=>FinancialSourceFactBoundary.ReviewedTypes([("asset","INCOME",null),("asset","INCOME",null)],null));
        Assert.Throws<FinancialSourceSelectionException>(()=>FinancialSourceFactBoundary.ReviewedTypes(Enumerable.Range(0,51).Select(i=>(i.ToString(System.Globalization.CultureInfo.InvariantCulture),"INCOME",(string?)null)).ToArray(),null));
        Assert.Throws<FinancialSourceSelectionException>(()=>FinancialSourceFactBoundary.ReviewedTypes([("asset","ASSET","financial-csv/1.0.0")],[("","PROPERTY")]));
    }
    [Fact]
    public void UnknownAssetVersionsRemainUnsupportedAndExistingCategoriesKeepTheirTypes()
    {
        var error=Assert.Throws<FinancialSourceSelectionException>(()=>FinancialSourceFactBoundary.ReviewedTypes([("asset","ASSET","unknown/1.0.0")],null));
        Assert.Equal("FINANCIAL_REVIEW_ASSET_SOURCE_UNSUPPORTED",error.Code);
        var mapped=FinancialSourceFactBoundary.ReviewedTypes([("income","INCOME",null),("insurance","INSURANCE",null),("tax","TAX",null),("property","ASSET","property-sale-deed-png-ocr/1.0.0"),("vehicle","ASSET","vehicle-purchase-invoice-png-ocr/1.0.0")],null);
        Assert.Equal("INCOME",mapped["income"]); Assert.Equal("INSURANCE_POLICY",mapped["insurance"]); Assert.Equal("TAX_RECORD",mapped["tax"]); Assert.Equal("PROPERTY",mapped["property"]);Assert.Equal("VEHICLE",mapped["vehicle"]);
    }
    [Fact]
    public void DirectCanonicalBankAccountRemainsCanonicalWithoutAReviewChoice()
    {
        var mapped=FinancialSourceFactBoundary.ReviewedTypes([("account","BANK_ACCOUNT","bank-statement/1.0.0")],null);
        Assert.Equal("BANK_ACCOUNT",mapped["account"]);
        Assert.Equal("ASSET",FinancialSourceFactBoundary.ForCustomer(mapped["account"]));
        Assert.Equal("BANK_ACCOUNT",FinancialSourceFactBoundary.CustomerAssetType(mapped["account"]));
    }
    private static async Task<ContractResult<TData>> Send<TPayload,TData>(HttpClient client,string cid,ContractRequest<TPayload> request)
    {
        using var response = await client.PostAsJsonAsync("/contracts/"+cid+"/v1",request,ContractJson.Options);
        return (await response.Content.ReadFromJsonAsync<ContractResult<TData>>(ContractJson.Options))!;
    }
}
