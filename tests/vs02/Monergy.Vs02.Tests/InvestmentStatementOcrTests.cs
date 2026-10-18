using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class InvestmentStatementOcrTests
{
    private const string Valid = "INVESTMENT STATEMENT|Statement Date: 2026-10-01|Currency: INR|Purchase Value: 800,000.00|Current Value: 925,750.25";
    private static EvidenceContent Source(string file = "investment-statement.png", string mime = "image/png")
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        return new(new EvidenceReference("evidence-investment", "document-investment", "version-investment", "customer-001",
            "reference://investment-statement", Convert.ToHexStringLower(SHA256.HashData(bytes)), mime), "", bytes);
    }
    private static ConfigurationManager Configuration() => new()
    {
        ["Monergy:DocumentProcessing:OcrExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSERACT") ?? "/usr/bin/tesseract",
        ["Monergy:DocumentProcessing:OcrDataDirectory"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSDATA") ?? "/usr/share/tesseract-ocr/5/tessdata",
        ["Monergy:DocumentProcessing:PdfInfoExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_PDFINFO") ?? "/usr/bin/pdfinfo",
        ["Monergy:DocumentProcessing:PdfRenderExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_PDFTOPPM") ?? "/usr/bin/pdftoppm",
    };
    [Theory, Trait("Category", "NativeOcr")]
    [InlineData("investment-statement.png", "image/png", "png")]
    [InlineData("investment-statement.jpg", "image/jpeg", "jpeg")]
    [InlineData("investment-statement-scan.pdf", "application/pdf", "pdf")]
    public async Task PinnedNativeOcrReadsBothFiguresWithExactOriginalProvenance(string file, string mime, string format)
    {
        using var engine = new TesseractImageTextRecognizer(Configuration());
        using var renderer = new PopplerPdfPageRenderer(Configuration());
        var extractor = new StructuredDocumentExtractor(new SalarySlipPngExtractor(engine), new SalarySlipPdfExtractor(renderer, engine), new SalarySlipJpegExtractor(engine));
        var source = Source(file, mime);
        var facts = await extractor.ExtractAsync(source, "investment-run", default);
        AssertFacts(facts); Assert.All(facts, fact => {
            Assert.Equal(source.Reference.EvidenceId, fact.EvidenceId); Assert.Equal(source.Reference.DocumentVersionId, fact.DocumentVersionId);
            Assert.Equal(source.Reference.CustomerId, fact.CustomerId); Assert.Equal($"investment-statement-{format}-ocr/1.0.0", fact.ExtractionVersion);
            Assert.StartsWith("Investment statement OCR: page 1, line ", fact.SourceLocation, StringComparison.Ordinal);
            Assert.Contains("Statement Date: 2026-10-01", fact.SourceLocation, StringComparison.Ordinal); Assert.InRange(fact.Confidence, .9m, 1m);
        });
        Assert.Equal(facts, await extractor.ExtractAsync(source, "investment-run", default));
        var replacement = await extractor.ExtractAsync(source with { Reference = source.Reference with { DocumentVersionId = "investment-next" } }, "investment-run", default);
        Assert.All(replacement, fact => Assert.DoesNotContain(facts, previous => previous.SourceFactId == fact.SourceFactId));
    }
    [Theory, Trait("Category", "NativeOcr")]
    [InlineData("investment-statement-incomplete.png", "image/png", DocumentRecognitionFailure.IncompleteInvestment)]
    [InlineData("investment-statement-mixed.jpg", "image/jpeg", DocumentRecognitionFailure.Unclassified)]
    public async Task NativeIncompleteOrMixedInvestmentHasNoCandidatePrefix(string file, string mime, DocumentRecognitionFailure expected)
    {
        using var engine = new TesseractImageTextRecognizer(Configuration());
        var extractor = new StructuredDocumentExtractor(new SalarySlipPngExtractor(engine), salarySlipJpeg: new SalarySlipJpegExtractor(engine));
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => extractor.ExtractAsync(Source(file, mime), "bad-investment", default));
        Assert.Equal(expected, failure.Reason);
    }
    [Theory]
    [InlineData("INVESTMENT STATEMENT|Statement Date: 2026-10-01|Currency: INR|Purchase Value: 800000|Current Value: 925750.25")]
    [InlineData("INVESTMENT STATEMENT|Statement Date: 1 October 2026|Purchase Value: INR 800,000.00|Current Value: 925,750.25 INR")]
    [InlineData("Investment Statement|Statement Date: 01 Oct 2026|Currency: inr|Purchase Value: 800000.00 inr|Current Value: INR 925750.25")]
    public async Task LabelledInvestmentFiguresKeepMeaningWithoutPeriodOrAssetInference(string text)
    {
        var facts = await new SalarySlipPngExtractor(new TextDouble(Tsv(text))).ExtractAsync(Source(), "investment-fields", default);
        AssertFacts(facts); Assert.Equal(2, facts.Select(f => f.SourceFactId).Distinct(StringComparer.Ordinal).Count());
    }
    [Theory]
    [InlineData("Statement Date: 2026-10-01|Currency: INR|Purchase Value: 800000")]
    [InlineData("Statement Date: 2026-10-01|Currency: INR|Current Value: 925750.25")]
    [InlineData("Transaction Date: 2026-10-01|Currency: INR|Purchase Value: 800000|Current Value: 925750.25")]
    [InlineData("Statement Date: 01/10/2026|Currency: INR|Purchase Value: 800000|Current Value: 925750.25")]
    [InlineData("Statement Date: 2026-02-30|Currency: INR|Purchase Value: 800000|Current Value: 925750.25")]
    [InlineData("Statement Date: 2026-10-01|Statement Date: 2026-10-01|Currency: INR|Purchase Value: 800000|Current Value: 925750.25")]
    [InlineData("Statement Date: 2026-10-01|Currency: INR|Purchase Value: 800000|Purchase Value: 800000|Current Value: 925750.25")]
    [InlineData("Statement Date: 2026-10-01|Currency: INR|Purchase Value: 800000|Current Value: 925750.25|Current Value: 925750.25")]
    [InlineData("Statement Date: 2026-10-01|Currency: INR|Currency: INR|Purchase Value: 800000|Current Value: 925750.25")]
    [InlineData("Statement Date: 2026-10-01|Purchase Value: 800000|Current Value: 925750.25")]
    [InlineData("Statement Date: 2026-10-01|Currency: XYZ|Purchase Value: 800000|Current Value: 925750.25")]
    [InlineData("Statement Date: 2026-10-01|Purchase Value: INR 800000|Current Value: USD 925750.25")]
    [InlineData("Statement Date: 2026-10-01|Currency: INR|Purchase Value: USD 800000|Current Value: 925750.25")]
    [InlineData("Statement Date: 2026-10-01|Currency: INR|Purchase Value: 800000|Current Value: USD 925750.25")]
    [InlineData("Statement Date: 2026-10-01|Currency: INR|Purchase Value: -100|Current Value: 925750.25")]
    [InlineData("Statement Date: 2026-10-01|Currency: INR|Purchase Value: 800000|Current Value: -10")]
    [InlineData("Statement Date: 2026-10-01|Currency: INR|Purchase Value: 8O0000|Current Value: 925750.25")]
    [InlineData("Statement Date: 2026-10-01|Currency: INR|Purchase Value: 800000|Current Value: 925,750.250")]
    [InlineData("Statement Date: 2026-10-01|Currency: INR|Purchase Value: 8,00,000|Current Value: 925750.25")]
    [InlineData("Statement Date: 2026-10-01|Currency: INR|Purchase Value: INR 800000 INR|Current Value: 925750.25")]
    [InlineData("Statement Date: 2026-10-01|Currency: INR|Purchase Value: 800000|Market Price: 925750.25")]
    public async Task InvalidOrMissingEitherFigureRejectsEntireInvestment(string fields)
    {
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => new SalarySlipPngExtractor(new TextDouble(
            Tsv("INVESTMENT STATEMENT|" + fields))).ExtractAsync(Source(), "bad-fields", default));
        Assert.Equal(DocumentRecognitionFailure.IncompleteInvestment, failure.Reason);
    }
    [Theory]
    [InlineData("INVESTMENT STATEMENT|SALARY SLIP")]
    [InlineData("SALARY SLIP|INVESTMENT STATEMENT")]
    [InlineData("INVESTMENT STATEMENT|INSURANCE POLICY")]
    [InlineData("INSURANCE POLICY|INVESTMENT STATEMENT")]
    [InlineData("INVESTMENT STATEMENT|LOAN STATEMENT")]
    [InlineData("LOAN STATEMENT|INVESTMENT STATEMENT")]
    [InlineData("INVESTMENT STATEMENT|INVESTMENT STATEMENT")]
    [InlineData("INVESTMENT QUOTATION")]
    [InlineData("Header|Header|Header|Header|Header|Header|Header|Header|Header|Header|INVESTMENT STATEMENT")]
    public async Task UnknownDuplicateMixedOrLateTitlesRemainUnclassified(string title)
    {
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => new SalarySlipPngExtractor(new TextDouble(
            Tsv(title + "|Statement Date: 2026-10-01|Currency: INR|Purchase Value: 800000|Current Value: 925750.25|Pay Date: 2026-10-01|Net Pay: 1|Statement Date: 2026-10-01|Outstanding Balance: 1"))).ExtractAsync(Source(), "mixed", default));
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
            new StructuredDocumentExtractor(new SalarySlipPngExtractor(new TextDouble(Tsv(low ? Valid : "INVESTMENT STATEMENT", low ? 4 : 0)))),
            new ReferenceExecutionPolicy(), new CapturingTelemetry(), TimeProvider.System);
        var result = await app.ProcessDocumentAsync(Vs02TestContext.Request(Vs02ContractNames.ProcessDocument,
            new ProcessDocument("investment-run", "version-investment", "evidence-investment", "customer-001", DateTimeOffset.UtcNow), "investment-fail", "investment-fail"));
        Assert.Equal(low ? "processing.ocr.low-confidence" : "processing.investment-statement.incomplete", result.Error?.Code); Assert.False(result.Error?.Retryable);
        var record = (await repository.GetAsync("investment-run", default))!; Assert.Equal(ProcessingState.Failed, record.State); Assert.Empty(record.Facts);
        Assert.IsType<DomainEvent<DocumentProcessingFailedPayload>>(Assert.Single(repository.DrainOutbox()));
    }
    [Theory]
    [InlineData("0", 0)][InlineData("700000.00", 700000)]
    public async Task ZeroOrLowerCurrentValueRemainsLiteralWithoutInventedGainOrStatus(string current, int expected)
    {
        var facts = await new SalarySlipPngExtractor(new TextDouble(Tsv(
            "INVESTMENT STATEMENT|Statement Date: 2026-10-01|Currency: INR|Purchase Value: 800000|Current Value: " + current)))
            .ExtractAsync(Source(), "literal-values", default);
        Assert.Equal(2, facts.Count); Assert.Equal(800000m, facts[0].CandidateValue); Assert.Equal((decimal)expected, facts[1].CandidateValue);
        Assert.Equal("Current value stated on statement", facts[1].Label); Assert.All(facts, fact => Assert.Equal("INVESTMENT", fact.FactType));
    }
    private static void AssertFacts(IReadOnlyList<ValidatedSourceFact> facts)
    {
        Assert.Equal(2, facts.Count); Assert.All(facts, f => { Assert.Equal("INVESTMENT", f.FactType); Assert.Equal("INR", f.Currency); Assert.Equal(new DateOnly(2026,10,1),f.EffectiveDate); });
        Assert.Equal(800000m, facts[0].CandidateValue); Assert.Equal("Purchase value stated on statement", facts[0].Label);
        Assert.Equal(925750.25m, facts[1].CandidateValue); Assert.Equal("Current value stated on statement", facts[1].Label);
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
