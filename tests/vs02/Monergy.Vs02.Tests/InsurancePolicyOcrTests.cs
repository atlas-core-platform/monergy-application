using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class InsurancePolicyOcrTests
{
    private const string Valid = "INSURANCE POLICY|Policy Date: 2026-10-01|Currency: INR|Sum Assured: 1,000,000.00|Premium: 24,500.50";
    private static EvidenceContent Source(string file = "insurance-policy.png", string mime = "image/png")
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        return new(new EvidenceReference("evidence-insurance", "document-insurance", "version-insurance", "customer-001",
            "reference://insurance-policy", Convert.ToHexStringLower(SHA256.HashData(bytes)), mime), "", bytes);
    }
    private static ConfigurationManager Configuration() => new()
    {
        ["Monergy:DocumentProcessing:OcrExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSERACT") ?? "/usr/bin/tesseract",
        ["Monergy:DocumentProcessing:OcrDataDirectory"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSDATA") ?? "/usr/share/tesseract-ocr/5/tessdata",
        ["Monergy:DocumentProcessing:PdfInfoExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_PDFINFO") ?? "/usr/bin/pdfinfo",
        ["Monergy:DocumentProcessing:PdfRenderExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_PDFTOPPM") ?? "/usr/bin/pdftoppm",
    };
    [Theory, Trait("Category", "NativeOcr")]
    [InlineData("insurance-policy.png", "image/png", "png")]
    [InlineData("insurance-policy.jpg", "image/jpeg", "jpeg")]
    [InlineData("insurance-policy-scan.pdf", "application/pdf", "pdf")]
    public async Task PinnedNativeOcrReadsBothFiguresWithExactOriginalProvenance(string file, string mime, string format)
    {
        using var engine = new TesseractImageTextRecognizer(Configuration());
        using var renderer = new PopplerPdfPageRenderer(Configuration());
        var extractor = new StructuredDocumentExtractor(new SalarySlipPngExtractor(engine), new SalarySlipPdfExtractor(renderer, engine), new SalarySlipJpegExtractor(engine));
        var source = Source(file, mime);
        var facts = await extractor.ExtractAsync(source, "policy-run", default);
        AssertFacts(facts); Assert.All(facts, fact => {
            Assert.Equal(source.Reference.EvidenceId, fact.EvidenceId); Assert.Equal(source.Reference.DocumentVersionId, fact.DocumentVersionId);
            Assert.Equal(source.Reference.CustomerId, fact.CustomerId); Assert.Equal($"insurance-policy-{format}-ocr/1.0.0", fact.ExtractionVersion);
            Assert.StartsWith("Insurance policy OCR: page 1, line ", fact.SourceLocation, StringComparison.Ordinal);
            Assert.Contains("Policy Date: 2026-10-01", fact.SourceLocation, StringComparison.Ordinal); Assert.InRange(fact.Confidence, .9m, 1m);
        });
        Assert.Equal(facts, await extractor.ExtractAsync(source, "policy-run", default));
        var replacement = await extractor.ExtractAsync(source with { Reference = source.Reference with { DocumentVersionId = "policy-next" } }, "policy-run", default);
        Assert.All(replacement, fact => Assert.DoesNotContain(facts, previous => previous.SourceFactId == fact.SourceFactId));
    }
    [Theory, Trait("Category", "NativeOcr")]
    [InlineData("insurance-policy-incomplete.png", "image/png", DocumentRecognitionFailure.IncompleteInsurance)]
    [InlineData("insurance-policy-mixed.jpg", "image/jpeg", DocumentRecognitionFailure.Unclassified)]
    public async Task NativeIncompleteOrMixedPolicyHasNoCandidatePrefix(string file, string mime, DocumentRecognitionFailure expected)
    {
        using var engine = new TesseractImageTextRecognizer(Configuration());
        var extractor = new StructuredDocumentExtractor(new SalarySlipPngExtractor(engine), salarySlipJpeg: new SalarySlipJpegExtractor(engine));
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => extractor.ExtractAsync(Source(file, mime), "bad-policy", default));
        Assert.Equal(expected, failure.Reason);
    }
    [Theory]
    [InlineData("INSURANCE POLICY|Policy Date: 2026-10-01|Currency: INR|Sum Assured: 1000000|Premium: 24500.50")]
    [InlineData("LIFE INSURANCE POLICY|Policy Date: 1 October 2026|Sum Assured: INR 1,000,000.00|Premium Amount: 24,500.50 INR")]
    [InlineData("Insurance Policy|Policy Date: 01 Oct 2026|Currency: inr|Sum Assured: 1000000.00 inr|Premium: INR 24500.50")]
    public async Task LabelledPolicyFiguresKeepMeaningWithoutPeriodOrAssetInference(string text)
    {
        var facts = await new SalarySlipPngExtractor(new TextDouble(Tsv(text))).ExtractAsync(Source(), "policy-fields", default);
        AssertFacts(facts); Assert.Equal(2, facts.Select(f => f.SourceFactId).Distinct(StringComparer.Ordinal).Count());
    }
    [Theory]
    [InlineData("Policy Date: 2026-10-01|Currency: INR|Sum Assured: 1000000")]
    [InlineData("Policy Date: 2026-10-01|Currency: INR|Premium: 24500.50")]
    [InlineData("Maturity Date: 2026-10-01|Currency: INR|Sum Assured: 1000000|Premium: 24500.50")]
    [InlineData("Policy Date: 01/10/2026|Currency: INR|Sum Assured: 1000000|Premium: 24500.50")]
    [InlineData("Policy Date: 2026-02-30|Currency: INR|Sum Assured: 1000000|Premium: 24500.50")]
    [InlineData("Policy Date: 2026-10-01|Policy Date: 2026-10-01|Currency: INR|Sum Assured: 1000000|Premium: 24500.50")]
    [InlineData("Policy Date: 2026-10-01|Currency: INR|Sum Assured: 1000000|Sum Assured: 1000000|Premium: 24500.50")]
    [InlineData("Policy Date: 2026-10-01|Currency: INR|Sum Assured: 1000000|Premium: 24500.50|Premium Amount: 24500.50")]
    [InlineData("Policy Date: 2026-10-01|Currency: INR|Currency: INR|Sum Assured: 1000000|Premium: 24500.50")]
    [InlineData("Policy Date: 2026-10-01|Sum Assured: 1000000|Premium: 24500.50")]
    [InlineData("Policy Date: 2026-10-01|Currency: XYZ|Sum Assured: 1000000|Premium: 24500.50")]
    [InlineData("Policy Date: 2026-10-01|Sum Assured: INR 1000000|Premium: USD 24500.50")]
    [InlineData("Policy Date: 2026-10-01|Currency: INR|Sum Assured: USD 1000000|Premium: 24500.50")]
    [InlineData("Policy Date: 2026-10-01|Currency: INR|Sum Assured: 1000000|Premium: USD 24500.50")]
    [InlineData("Policy Date: 2026-10-01|Currency: INR|Sum Assured: -100|Premium: 24500.50")]
    [InlineData("Policy Date: 2026-10-01|Currency: INR|Sum Assured: 1000000|Premium: -10")]
    [InlineData("Policy Date: 2026-10-01|Currency: INR|Sum Assured: 1O00000|Premium: 24500.50")]
    [InlineData("Policy Date: 2026-10-01|Currency: INR|Sum Assured: 1000000|Premium: 24,500.500")]
    [InlineData("Policy Date: 2026-10-01|Currency: INR|Sum Assured: 10,00,000|Premium: 24500.50")]
    [InlineData("Policy Date: 2026-10-01|Currency: INR|Sum Assured: INR 1000000 INR|Premium: 24500.50")]
    [InlineData("Policy Date: 2026-10-01|Currency: INR|Sum Assured: 1000000|Annual Premium: 24500.50")]
    public async Task InvalidOrMissingEitherFigureRejectsEntirePolicy(string fields)
    {
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => new SalarySlipPngExtractor(new TextDouble(
            Tsv("INSURANCE POLICY|" + fields))).ExtractAsync(Source(), "bad-fields", default));
        Assert.Equal(DocumentRecognitionFailure.IncompleteInsurance, failure.Reason);
    }
    [Theory]
    [InlineData("INSURANCE POLICY|SALARY SLIP")]
    [InlineData("SALARY SLIP|INSURANCE POLICY")]
    [InlineData("INSURANCE POLICY|LOAN STATEMENT")]
    [InlineData("LOAN STATEMENT|INSURANCE POLICY")]
    [InlineData("INSURANCE POLICY|LIFE INSURANCE POLICY")]
    [InlineData("INSURANCE QUOTATION")]
    [InlineData("Header|Header|Header|Header|Header|Header|Header|Header|Header|Header|INSURANCE POLICY")]
    public async Task UnknownDuplicateMixedOrLateTitlesRemainUnclassified(string title)
    {
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => new SalarySlipPngExtractor(new TextDouble(
            Tsv(title + "|Policy Date: 2026-10-01|Currency: INR|Sum Assured: 1000000|Premium: 24500.50|Pay Date: 2026-10-01|Net Pay: 1|Statement Date: 2026-10-01|Outstanding Balance: 1"))).ExtractAsync(Source(), "mixed", default));
        Assert.Equal(DocumentRecognitionFailure.Unclassified, failure.Reason);
    }
    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public async Task AnyRequiredLowConfidenceLineRejectsAllFigures(int line)
    {
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => new SalarySlipPngExtractor(new TextDouble(Tsv(Valid, line)))
            .ExtractAsync(Source(), "low-confidence", default));
        Assert.Equal(DocumentRecognitionFailure.LowConfidence, failure.Reason);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task IncompleteAndLowConfidenceCommitTypedZeroFactTerminalFailures(bool low)
    {
        var repository = new InMemoryDocumentProcessingRepository();
        var app = new DocumentProcessingApplication(repository, new SourceDouble(),
            new StructuredDocumentExtractor(new SalarySlipPngExtractor(new TextDouble(Tsv(low ? Valid : "INSURANCE POLICY", low ? 4 : 0)))),
            new ReferenceExecutionPolicy(), new CapturingTelemetry(), TimeProvider.System);
        var result = await app.ProcessDocumentAsync(Vs02TestContext.Request(Vs02ContractNames.ProcessDocument,
            new ProcessDocument("policy-run", "version-insurance", "evidence-insurance", "customer-001", DateTimeOffset.UtcNow), "policy-fail", "policy-fail"));
        Assert.Equal(low ? "processing.ocr.low-confidence" : "processing.insurance-policy.incomplete", result.Error?.Code); Assert.False(result.Error?.Retryable);
        var record = (await repository.GetAsync("policy-run", default))!; Assert.Equal(ProcessingState.Failed, record.State); Assert.Empty(record.Facts);
        Assert.IsType<DomainEvent<DocumentProcessingFailedPayload>>(Assert.Single(repository.DrainOutbox()));
    }
    private static void AssertFacts(IReadOnlyList<ValidatedSourceFact> facts)
    {
        Assert.Equal(2, facts.Count); Assert.All(facts, f => { Assert.Equal("INSURANCE", f.FactType); Assert.Equal("INR", f.Currency); Assert.Equal(new DateOnly(2026,10,1),f.EffectiveDate); });
        Assert.Equal(1000000m, facts[0].CandidateValue); Assert.Equal("Sum assured stated on policy", facts[0].Label);
        Assert.Equal(24500.50m, facts[1].CandidateValue); Assert.Equal("Premium stated on policy (frequency not established)", facts[1].Label);
    }
    private static string Tsv(string text, int lowLine = 0) =>
        "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
        string.Join('\n', text.Split('|').SelectMany((line, index) => line.Split(' ').Select((word, number) =>
            string.Create(CultureInfo.InvariantCulture, $"5\t1\t1\t1\t{index+1}\t{number+1}\t10\t10\t100\t40\t{(index+1 == lowLine ? 89 : 99)}\t{word}"))));
    private sealed class TextDouble(string text) : IImageTextRecognizer
    {
        public Task<string> RecognizeAsync(byte[] image, CancellationToken cancellationToken) => Task.FromResult(text);
    }
    private sealed class SourceDouble : IEvidenceContentReader
    {
        public Task<EvidenceContent?> ReadAsync(string version, string customer, TrustedSecurityContext security, string correlation, CancellationToken cancellationToken) => Task.FromResult<EvidenceContent?>(Source());
    }
}
