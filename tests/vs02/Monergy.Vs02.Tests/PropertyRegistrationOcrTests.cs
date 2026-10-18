using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class PropertyRegistrationOcrTests
{
    private const string Valid = "PROPERTY REGISTRATION DOCUMENT|Registration Date: 2026-10-01|Currency: INR|Purchase Value: 3,500,000.00";
    private static EvidenceContent Source(string file = "property-registration.png", string mime = "image/png")
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        return new(new EvidenceReference("evidence-property", "document-property", "version-property", "customer-001",
            "reference://property-registration", Convert.ToHexStringLower(SHA256.HashData(bytes)), mime), "", bytes);
    }
    private static ConfigurationManager Configuration() => new()
    {
        ["Monergy:DocumentProcessing:OcrExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSERACT") ?? "/usr/bin/tesseract",
        ["Monergy:DocumentProcessing:OcrDataDirectory"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSDATA") ?? "/usr/share/tesseract-ocr/5/tessdata",
        ["Monergy:DocumentProcessing:PdfInfoExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_PDFINFO") ?? "/usr/bin/pdfinfo",
        ["Monergy:DocumentProcessing:PdfRenderExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_PDFTOPPM") ?? "/usr/bin/pdftoppm",
    };
    [Theory, Trait("Category", "NativeOcr")]
    [InlineData("property-registration.png", "image/png", "png")]
    [InlineData("property-registration.jpg", "image/jpeg", "jpeg")]
    [InlineData("property-registration-scan.pdf", "application/pdf", "pdf")]
    public async Task RealOcrKeepsPurchaseMeaningDateAndOriginalProvenance(string file, string mime, string format)
    {
        using var engine = new TesseractImageTextRecognizer(Configuration());
        using var renderer = new PopplerPdfPageRenderer(Configuration());
        var extractor = new StructuredDocumentExtractor(new SalarySlipPngExtractor(engine), new SalarySlipPdfExtractor(renderer, engine), new SalarySlipJpegExtractor(engine));
        var source = Source(file, mime);
        var facts = await extractor.ExtractAsync(source, "property-run", default);
        var fact = Assert.Single(facts); AssertFact(fact);
        Assert.Equal(source.Reference.EvidenceId, fact.EvidenceId); Assert.Equal(source.Reference.DocumentVersionId, fact.DocumentVersionId);
        Assert.Equal(source.Reference.CustomerId, fact.CustomerId); Assert.Equal($"property-registration-{format}-ocr/1.0.0", fact.ExtractionVersion);
        Assert.StartsWith("Property registration document OCR: page 1, line ", fact.SourceLocation, StringComparison.Ordinal);
        Assert.Contains("Registration Date: 2026-10-01", fact.SourceLocation, StringComparison.Ordinal); Assert.InRange(fact.Confidence, .9m, 1m);
        Assert.Equal(facts, await extractor.ExtractAsync(source, "property-run", default));
        var replacement = Assert.Single(await extractor.ExtractAsync(source with { Reference = source.Reference with { DocumentVersionId = "property-next" } }, "property-run", default));
        Assert.NotEqual(fact.SourceFactId, replacement.SourceFactId);
        Assert.NotEqual(fact.SourceFactId, Assert.Single(await extractor.ExtractAsync(source, "property-other-run", default)).SourceFactId);
    }
    [Theory, Trait("Category", "NativeOcr")]
    [InlineData("property-registration-incomplete.png", "image/png", DocumentRecognitionFailure.IncompletePropertyRegistration)]
    [InlineData("property-registration-mixed.jpg", "image/jpeg", DocumentRecognitionFailure.Unclassified)]
    public async Task RealIncompleteOrMixedRegistrationProducesNoFacts(string file, string mime, DocumentRecognitionFailure expected)
    {
        using var engine = new TesseractImageTextRecognizer(Configuration());
        var extractor = new StructuredDocumentExtractor(new SalarySlipPngExtractor(engine), salarySlipJpeg: new SalarySlipJpegExtractor(engine));
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => extractor.ExtractAsync(Source(file,mime), "invalid-native-property", default));
        Assert.Equal(expected, failure.Reason);
    }
    [Theory]
    [InlineData("PROPERTY REGISTRATION DOCUMENT|Registration Date: 2026-10-01|Currency: INR|Purchase Value: 3500000")]
    [InlineData("PROPERTY REGISTRATION DOCUMENT|Date of Registration: 1 October 2026|Purchase Value: INR 3,500,000.00")]
    [InlineData("Property Registration Document|Registration Date: 01 Oct 2026|Currency: inr|Purchase Value: 3500000.00 inr")]
    public async Task ExplicitAliasesKeepDatedPurchaseFigure(string text)
    {
        var fact = Assert.Single(await new SalarySlipPngExtractor(new TextDouble(Tsv(text))).ExtractAsync(Source(), "property-fields", default)); AssertFact(fact);
    }
    [Theory]
    [InlineData("Registration Date: 2026-10-01|Currency: INR")]
    [InlineData("Currency: INR|Purchase Value: 3500000")]
    [InlineData("Deed Date: 2026-10-01|Currency: INR|Purchase Value: 3500000")]
    [InlineData("Registration Date: 01/10/2026|Currency: INR|Purchase Value: 3500000")]
    [InlineData("Registration Date: 2026-02-30|Currency: INR|Purchase Value: 3500000")]
    [InlineData("Registration Date: 2026-10-01|Date of Registration: 2026-10-01|Currency: INR|Purchase Value: 3500000")]
    [InlineData("Registration Date: 2026-10-01|Currency: INR|Purchase Value: 3500000|Purchase Value: 3500000")]
    [InlineData("Registration Date: 2026-10-01|Currency: INR|Currency: INR|Purchase Value: 3500000")]
    [InlineData("Registration Date: 2026-10-01|Purchase Value: 3500000")]
    [InlineData("Registration Date: 2026-10-01|Currency: XYZ|Purchase Value: 3500000")]
    [InlineData("Registration Date: 2026-10-01|Currency: INR|Purchase Value: USD 3500000")]
    [InlineData("Registration Date: 2026-10-01|Currency: INR|Purchase Value: -1")]
    [InlineData("Registration Date: 2026-10-01|Currency: INR|Purchase Value: 35,00,000")]
    [InlineData("Registration Date: 2026-10-01|Currency: INR|Purchase Value: 3500000.001")]
    [InlineData("Registration Date: 2026-10-01|Currency: INR|Purchase Value: 35O0000")]
    [InlineData("Registration Date: 2026-10-01|Currency: INR|Purchase Value: INR 3500000 INR")]
    [InlineData("Registration Date: 2026-10-01|Currency: INR|Market Value: 3500000")]
    [InlineData("Registration Date: 2026-10-01|Currency: INR|Estimated Value: 3500000")]
    public async Task MissingDuplicateConflictingOrInvalidFieldsRejectRegistration(string fields)
    {
        var failure=await Assert.ThrowsAsync<DocumentRecognitionException>(()=>new SalarySlipPngExtractor(new TextDouble(Tsv("PROPERTY REGISTRATION DOCUMENT|"+fields))).ExtractAsync(Source(), "bad-property", default));
        Assert.Equal(DocumentRecognitionFailure.IncompletePropertyRegistration, failure.Reason);
    }
    [Theory]
    [InlineData("PROPERTY REGISTRATION DOCUMENT|SALARY SLIP")]
    [InlineData("SALARY SLIP|PROPERTY REGISTRATION DOCUMENT")]
    [InlineData("PROPERTY REGISTRATION DOCUMENT|LOAN STATEMENT")]
    [InlineData("LOAN STATEMENT|PROPERTY REGISTRATION DOCUMENT")]
    [InlineData("PROPERTY REGISTRATION DOCUMENT|LOAN SANCTION LETTER")]
    [InlineData("LOAN SANCTION LETTER|PROPERTY REGISTRATION DOCUMENT")]
    [InlineData("PROPERTY REGISTRATION DOCUMENT|INSURANCE POLICY")]
    [InlineData("INSURANCE POLICY|PROPERTY REGISTRATION DOCUMENT")]
    [InlineData("PROPERTY REGISTRATION DOCUMENT|INVESTMENT STATEMENT")]
    [InlineData("INVESTMENT STATEMENT|PROPERTY REGISTRATION DOCUMENT")]
    [InlineData("PROPERTY REGISTRATION DOCUMENT|PROPERTY REGISTRATION DOCUMENT")]
    [InlineData("PROPERTY REGISTRATION DOCUMENT|PROPERTY SALE DEED")]
    [InlineData("PROPERTY SALE DEED|PROPERTY REGISTRATION DOCUMENT")]
    [InlineData("PROPERTY VALUATION")]
    [InlineData("REGISTRATION DOCUMENT")]
    [InlineData("Header|Header|Header|Header|Header|Header|Header|Header|Header|Header|PROPERTY REGISTRATION DOCUMENT")]
    public async Task MixedDuplicateLateOrDifferentDocumentTitlesRemainUnclassified(string title)
    {
        var failure=await Assert.ThrowsAsync<DocumentRecognitionException>(()=>new SalarySlipPngExtractor(new TextDouble(Tsv(title+"|Registration Date: 2026-10-01|Currency: INR|Purchase Value: 3500000|Pay Date: 2026-10-01|Net Pay: 1|Statement Date: 2026-10-01|Outstanding Balance: 1"))).ExtractAsync(Source(), "mixed-property", default));
        Assert.Equal(DocumentRecognitionFailure.Unclassified,failure.Reason);
    }
    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
    public async Task UnclearRequiredLineRejectsPurchaseFigure(int line)
    {
        var failure=await Assert.ThrowsAsync<DocumentRecognitionException>(()=>new SalarySlipPngExtractor(new TextDouble(Tsv(Valid,line))).ExtractAsync(Source(),"unclear-property",default));
        Assert.Equal(DocumentRecognitionFailure.LowConfidence,failure.Reason);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task IncompleteOrUnclearRegistrationCommitsTypedTerminalFailureAndNoFinancialFacts(bool low)
    {
        var repository=new InMemoryDocumentProcessingRepository();
        var app=new DocumentProcessingApplication(repository,new SourceDouble(),new StructuredDocumentExtractor(new SalarySlipPngExtractor(new TextDouble(Tsv(low?Valid:"PROPERTY REGISTRATION DOCUMENT",low?4:0)))),new ReferenceExecutionPolicy(),new CapturingTelemetry(),TimeProvider.System);
        var result=await app.ProcessDocumentAsync(Vs02TestContext.Request(Vs02ContractNames.ProcessDocument,new ProcessDocument("property-run","version-property","evidence-property","customer-001",DateTimeOffset.UtcNow),"property-fail","property-fail"));
        Assert.Equal(low?"processing.ocr.low-confidence":"processing.property-registration.incomplete",result.Error?.Code); Assert.False(result.Error?.Retryable);
        var record=(await repository.GetAsync("property-run",default))!; Assert.Equal(ProcessingState.Failed,record.State); Assert.Empty(record.Facts);
        Assert.IsType<DomainEvent<DocumentProcessingFailedPayload>>(Assert.Single(repository.DrainOutbox()));
    }
    [Fact]
    public async Task LiteralZeroDoesNotCreateCurrentValuationOrOwnershipFact()
    {
        var fact=Assert.Single(await new SalarySlipPngExtractor(new TextDouble(Tsv(Valid.Replace("3,500,000.00","0",StringComparison.Ordinal)+"|Market Value: INR 9000000|Ownership: unverified"))).ExtractAsync(Source(),"literal-zero",default));
        Assert.Equal(0m,fact.CandidateValue); Assert.Equal("Purchase value stated on property registration document",fact.Label); Assert.Equal("ASSET",fact.FactType);
    }
    [Fact]
    public async Task RegistrationDateDoesNotBecomePurchaseDateOrCurrentValue()
    {
        var fact = Assert.Single(await new SalarySlipPngExtractor(new TextDouble(Tsv(Valid + "|Deed Date: 2026-09-25|Market Value: INR 9000000"))).ExtractAsync(Source(), "registration-date", default));
        AssertFact(fact);
        Assert.Contains("Registration Date: 2026-10-01", fact.SourceLocation, StringComparison.Ordinal);
        Assert.Equal("PROPERTY", Monergy.Platform.FinancialSourceFactBoundary.ForOwner(fact.FactType, fact.ExtractionVersion));
    }
    private static void AssertFact(ValidatedSourceFact fact)
    {
        Assert.Equal("ASSET",fact.FactType); Assert.Equal("INR",fact.Currency); Assert.Equal(new DateOnly(2026,10,1),fact.EffectiveDate);
        Assert.Equal(3500000m,fact.CandidateValue); Assert.Equal("Purchase value stated on property registration document",fact.Label);
    }
    [Theory]
    [InlineData("ASSET", "financial-csv/1.0.0")]
    [InlineData("ASSET", "property-registration-png-ocr/9.0.0")]
    [InlineData("VEHICLE", "property-registration-png-ocr/1.0.0")]
    public void OwnerMappingDoesNotReclassifyOtherSourcesOrObjectTypes(string type, string version)
    {
        Assert.Equal(type, Monergy.Platform.FinancialSourceFactBoundary.ForOwner(type, version));
    }
    private static string Tsv(string text,int lowLine=0)=>"level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"+
        string.Join('\n',text.Split('|').SelectMany((line,index)=>line.Split(' ').Select((word,number)=>string.Create(CultureInfo.InvariantCulture,$"5\t1\t1\t1\t{index+1}\t{number+1}\t10\t10\t100\t40\t{(index+1==lowLine?89:99)}\t{word}"))));
    private sealed class TextDouble(string text):IImageTextRecognizer
    { public Task<string> RecognizeAsync(byte[] image,CancellationToken cancellationToken)=>Task.FromResult(text); }
    private sealed class SourceDouble:IEvidenceContentReader
    { public Task<EvidenceContent?> ReadAsync(string version,string customer,TrustedSecurityContext security,string correlation,CancellationToken cancellationToken)=>Task.FromResult<EvidenceContent?>(Source()); }
}
