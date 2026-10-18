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

public sealed class FinancialSourceCategoryTests
{
    [Theory]
    [InlineData("INSURANCE", "INSURANCE_POLICY")]
    [InlineData("TAX", "TAX_RECORD")]
    public async Task CustomerSourceCategoriesReachCanonicalOwnerTypes(string category, string ownerType)
    {
        var source = new EvidenceContent(new("category-evidence", "category-document", "category-version", "customer-001",
            "reference://category", new string('a',64), "text/plain"), category + "|Stated source figure|100.00|INR|2026-10-01|page:1|0.98");
        var fact = Assert.Single(await new FixtureDocumentExtractor().ExtractAsync(source,"category-run",default));
        var app = new FinancialProfileApplication(new InMemoryFinancialProfileRepository(), new CapturingTelemetry(), TimeProvider.System);
        var result = await app.NormalizeSourceFactsAsync(Vs02TestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts("category-profile","customer-001","category-run",
                [fact with { FactType = FinancialSourceFactBoundary.ForOwner(fact.FactType,fact.ExtractionVersion) }],
                "reviewed-document-facts/1.0.0",DateTimeOffset.UtcNow),"category-review","category-review"));
        Assert.Equal(ContractOutcome.Success,result.Outcome);
        Assert.Equal(ownerType,Assert.Single(result.Data!.Facts).FactType);
        Assert.Equal(category,FinancialSourceFactBoundary.ForCustomer(ownerType));
    }
    [Theory]
    [InlineData("text", "INSURANCE", "INSURANCE_POLICY")]
    [InlineData("text", "TAX", "TAX_RECORD")]
    [InlineData("csv", "INSURANCE", "INSURANCE_POLICY")]
    [InlineData("csv", "TAX", "TAX_RECORD")]
    [InlineData("xlsx", "INSURANCE", "INSURANCE_POLICY")]
    [InlineData("xlsx", "TAX", "TAX_RECORD")]
    public async Task ExtractedCategorySurvivesOwnerHttpAdoptionReplayReadbackAndProvenance(string format, string category, string ownerType)
    {
        var original = Assert.Single(await new StructuredDocumentExtractor().ExtractAsync(Source(format,category),"source-run",default));
        var repository = new InMemoryFinancialProfileRepository();
        var application = new FinancialProfileApplication(repository,new CapturingTelemetry(),TimeProvider.System);
        var builder = WebApplication.CreateSlimBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(application);
        await using var host = builder.Build(); host.MapFinancialProfileContracts(); await host.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
            var selected = original with { FactType = FinancialSourceFactBoundary.ForOwner(original.FactType,original.ExtractionVersion) };
            var request = Vs02TestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
                new NormalizeSourceFacts("category-profile","customer-001","source-run",[selected],"reviewed-document-facts/1.0.0",DateTimeOffset.UtcNow),"category-http","category-http");
            var created = await Send<NormalizeSourceFacts,NormalizationResult>(client,"cid-031",request);
            Assert.Equal(ContractOutcome.Success,created.Outcome);
            var saved = Assert.Single(created.Data!.Facts); Assert.Equal(ownerType,saved.FactType); Assert.True(saved.Created);
            var replay = await Send<NormalizeSourceFacts,NormalizationResult>(client,"cid-031",request);
            Assert.Equal(ContractOutcome.Success,replay.Outcome); Assert.False(Assert.Single(replay.Data!.Facts).Created);
            var profile = await Send<GetFinancialProfile,FinancialProfileDetails>(client,"cid-030",Vs02TestContext.Request(
                Vs02ContractNames.GetFinancialProfile,new GetFinancialProfile("category-profile","customer-001"),"category-profile-read"));
            var fact = Assert.Single(profile.Data!.Facts); Assert.Equal(1,profile.Data.Revision); Assert.Equal(ownerType,fact.FactType);
            Assert.Equal(category,FinancialSourceFactBoundary.ForCustomer(fact.FactType));
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
    [Fact]
    public async Task AnUntypedGenericAssetRejectsTheWholeMixedBatchWithoutInventingPropertyOrVehicle()
    {
        var source = Assert.Single(await new StructuredDocumentExtractor().ExtractAsync(Source("text","INSURANCE"),"mixed-run",default));
        var known = source with { FactType = FinancialSourceFactBoundary.ForOwner(source.FactType,source.ExtractionVersion) };
        var unknown = source with { SourceFactId="untyped-asset",FactType=FinancialSourceFactBoundary.ForOwner("ASSET",source.ExtractionVersion) };
        Assert.Equal("ASSET",unknown.FactType);
        var repository = new InMemoryFinancialProfileRepository();
        var app = new FinancialProfileApplication(repository,new CapturingTelemetry(),TimeProvider.System);
        var result = await app.NormalizeSourceFactsAsync(Vs02TestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts("category-profile","customer-001","mixed-run",[known,unknown],"reviewed-document-facts/1.0.0",DateTimeOffset.UtcNow),"category-mixed","category-mixed"));
        Assert.Equal("financial.normalization.invalid",result.Error?.Code); Assert.Null(await repository.GetProfileAsync("category-profile",default)); Assert.Empty(repository.DrainOutbox());
        Assert.Equal("BANK_ACCOUNT",FinancialSourceFactBoundary.ForCustomer("BANK_ACCOUNT"));
    }
    private static async Task<ContractResult<TData>> Send<TPayload,TData>(HttpClient client,string cid,ContractRequest<TPayload> request)
    {
        using var response = await client.PostAsJsonAsync("/contracts/"+cid+"/v1",request,ContractJson.Options);
        return (await response.Content.ReadFromJsonAsync<ContractResult<TData>>(ContractJson.Options))!;
    }
    internal static EvidenceContent Source(string format,string category)
    {
        var text = format=="csv" ? "Category,Description,Amount,Currency,Date,Source\n"+category+",Stated source figure,125000.00,INR,2026-10-01,page:1" : category+"|Stated source figure|125000.00|INR|2026-10-01|page:1|0.98";
        var bytes = Encoding.UTF8.GetBytes(text); var mime = format=="csv"?"text/csv":"text/plain";
        if (format=="xlsx")
        {
            using var input = new MemoryStream(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","financial-information.xlsx")));
            using var original = new ZipArchive(input); using var output = new MemoryStream();
            using (var zip = new ZipArchive(output,ZipArchiveMode.Create,true))
            {
                foreach (var entry in original.Entries)
                {
                    using var source = entry.Open(); using var target = zip.CreateEntry(entry.FullName).Open();
                    if (entry.FullName!="xl/worksheets/sheet1.xml") { source.CopyTo(target); continue; }
                    var sheet = XDocument.Load(source); XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                    foreach (var (address,value) in new[]{("A2",category),("B2","Stated source figure")})
                    { var cell=sheet.Descendants(n+"c").Single(x=>(string?)x.Attribute("r")==address); cell.SetAttributeValue("t","inlineStr");cell.RemoveNodes();cell.Add(new XElement(n+"is",new XElement(n+"t",value))); }
                    sheet.Save(target);
                }
            }
            bytes=output.ToArray();text="";mime=XlsxFinancialDocumentExtractor.ContentType;
        }
        return new(new("category-evidence","category-document","category-version","customer-001","reference://category",Convert.ToHexStringLower(SHA256.HashData(bytes)),mime),text,bytes);
    }

}
