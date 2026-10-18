using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class LoanStatementOcrTests
{
    private static EvidenceContent Source(string file = "loan-statement.png", string mime = "image/png")
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        return new EvidenceContent(new EvidenceReference("evidence-loan", "document-loan", "version-loan", "customer-001",
            "reference://loan-statement", Convert.ToHexStringLower(SHA256.HashData(bytes)), mime), "", bytes);
    }
    private static ConfigurationManager Configuration() => new()
    {
        ["Monergy:DocumentProcessing:OcrExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSERACT") ?? "/usr/bin/tesseract",
        ["Monergy:DocumentProcessing:OcrDataDirectory"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSDATA") ?? "/usr/share/tesseract-ocr/5/tessdata",
        ["Monergy:DocumentProcessing:PdfInfoExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_PDFINFO") ?? "/usr/bin/pdfinfo",
        ["Monergy:DocumentProcessing:PdfRenderExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_PDFTOPPM") ?? "/usr/bin/pdftoppm",
    };
    [Theory, Trait("Category", "NativeOcr")]
    [InlineData("loan-statement.png", "image/png", "png")]
    [InlineData("loan-statement.jpg", "image/jpeg", "jpeg")]
    [InlineData("loan-statement-scan.pdf", "application/pdf", "pdf")]
    public async Task RealPinnedOcrClassifiesLoanAndPreservesOriginalDocumentProvenance(string file, string mime, string format)
    {
        using var engine = new TesseractImageTextRecognizer(Configuration());
        using var renderer = new PopplerPdfPageRenderer(Configuration());
        var extractor = new StructuredDocumentExtractor(new SalarySlipPngExtractor(engine), new SalarySlipPdfExtractor(renderer, engine), new SalarySlipJpegExtractor(engine));
        var source = Source(file, mime);
        var fact = Assert.Single(await extractor.ExtractAsync(source, "processing-loan", default));
        Assert.Equal("LOAN", fact.FactType);
        Assert.Equal("Outstanding loan balance stated on document", fact.Label);
        Assert.Equal(176500.75m, fact.CandidateValue); // Sanctioned amount on the page is not adopted as another balance.
        Assert.Equal("INR", fact.Currency);
        Assert.Equal(new DateOnly(2026, 9, 30), fact.EffectiveDate);
        Assert.Equal(source.Reference.CustomerId, fact.CustomerId);
        Assert.Equal(source.Reference.DocumentVersionId, fact.DocumentVersionId);
        Assert.Equal(source.Reference.EvidenceId, fact.EvidenceId);
        Assert.Equal($"loan-statement-{format}-ocr/1.0.0", fact.ExtractionVersion);
        Assert.StartsWith("Loan statement OCR: page 1, line ", fact.SourceLocation, StringComparison.Ordinal);
        Assert.InRange(fact.Confidence, .9m, 1m);
        Assert.Equal(fact, Assert.Single(await extractor.ExtractAsync(source, "processing-loan", default)));
        var nextVersion = source with { Reference = source.Reference with { DocumentVersionId = "version-next" } };
        Assert.NotEqual(fact.SourceFactId, Assert.Single(await extractor.ExtractAsync(nextVersion, "processing-loan", default)).SourceFactId);
    }

    [Theory]
    [InlineData("LOAN STATEMENT|Statement Date: 2026-09-30|Currency: INR|Outstanding Balance: 176500.75", "Outstanding loan balance stated on document")]
    [InlineData("LOAN ACCOUNT STATEMENT|As of Date: 30 September 2026|Outstanding Loan Balance: INR 176,500.75", "Outstanding loan balance stated on document")]
    [InlineData("LOAN STATEMENT|Balance As of: 30 Sep 2026|Principal Outstanding: 176500.75 INR", "Principal outstanding stated on document")]
    public async Task ExplicitLabelsRetainSourceMeaningWithoutDebtArithmetic(string text, string label)
    {
        var fact = Assert.Single(await new SalarySlipPngExtractor(new TextDouble(Tsv(text))).ExtractAsync(Source(), "loan-fields", default));
        Assert.Equal(label, fact.Label); Assert.Equal("LOAN", fact.FactType);
        Assert.Equal(176500.75m, fact.CandidateValue); Assert.Equal("INR", fact.Currency);
        Assert.Equal(new DateOnly(2026, 9, 30), fact.EffectiveDate);
    }

    [Theory]
    [InlineData("Statement Date: 2026-09-30|Currency: INR|Sanctioned Amount: 500000.00|EMI: 12000.00")]
    [InlineData("Statement Date: 2026-09-30|Currency: INR|Outstanding Balance: 1|Principal Outstanding: 1")]
    [InlineData("Statement Date: 2026-09-30|Currency: INR|Outstanding Balance: 1|Outstanding Balance: 2")]
    [InlineData("Statement Date: 2026-09-30|Statement Date: 2026-09-30|Currency: INR|Outstanding Balance: 1")]
    [InlineData("Statement Date: 09/10/2026|Currency: INR|Outstanding Balance: 1")]
    [InlineData("Statement Date: 2026-02-30|Currency: INR|Outstanding Balance: 1")]
    [InlineData("Pay Date: 2026-09-30|Currency: INR|Outstanding Balance: 1")]
    [InlineData("Statement Date: 2026-09-30|Outstanding Balance: 1")]
    [InlineData("Statement Date: 2026-09-30|Currency: INR|Currency: INR|Outstanding Balance: 1")]
    [InlineData("Statement Date: 2026-09-30|Currency: INR|Outstanding Balance: USD 1")]
    [InlineData("Statement Date: 2026-09-30|Currency: XYZ|Outstanding Balance: 1")]
    [InlineData("Statement Date: 2026-09-30|Currency: INR|Outstanding Balance: -1")]
    [InlineData("Statement Date: 2026-09-30|Currency: INR|Outstanding Balance: 1O0.00")]
    [InlineData("Statement Date: 2026-09-30|Currency: INR|Outstanding Balance: 1,00,000.00")]
    [InlineData("Statement Date: 2026-09-30|Currency: INR|Outstanding Balance: 1.234")]
    [InlineData("Statement Date: 2026-09-30|Currency: INR|Outstanding Balance: INR 1 INR")]
    public async Task MissingDuplicateAmbiguousOrInvalidFieldsNeverProduceCandidates(string fields)
    {
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => new SalarySlipPngExtractor(
            new TextDouble(Tsv("LOAN STATEMENT|" + fields))).ExtractAsync(Source(), "invalid", default));
        Assert.Equal(DocumentRecognitionFailure.IncompleteLoan, failure.Reason);
    }
    [Theory]
    [InlineData("LOAN STATEMENT|SALARY SLIP")]
    [InlineData("SALARY SLIP|LOAN STATEMENT")]
    [InlineData("LOAN STATEMENT|LOAN ACCOUNT STATEMENT")]
    [InlineData("LOAN SANCTION LETTER")]
    [InlineData("INVOICE")]
    [InlineData("Header|Header|Header|Header|Header|Header|Header|Header|Header|Header|LOAN STATEMENT")]
    public async Task UnknownOrMultipleKnownTitlesFailClassification(string titles)
    {
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => new SalarySlipPngExtractor(new TextDouble(Tsv(
            titles + "|Statement Date: 2026-09-30|Currency: INR|Outstanding Balance: 1|Pay Date: 2026-09-30|Net Pay: 1"))).ExtractAsync(Source(), "class", default));
        Assert.Equal(DocumentRecognitionFailure.Unclassified, failure.Reason);
    }
    [Fact]
    public async Task LowConfidenceLoanAndIncompleteLoanCommitTypedTerminalFailureWithZeroFacts()
    {
        foreach (var lowConfidence in new[] { false, true })
        {
            var repository = new InMemoryDocumentProcessingRepository();
            var text = lowConfidence ? "LOAN STATEMENT|Statement Date: 2026-09-30|Currency: INR|Outstanding Balance: 1" : "LOAN STATEMENT";
            var app = new DocumentProcessingApplication(repository, new SourceDouble(),
                new StructuredDocumentExtractor(new SalarySlipPngExtractor(new TextDouble(Tsv(text, lowConfidence ? 89 : 99)))),
                new ReferenceExecutionPolicy(), new CapturingTelemetry(), TimeProvider.System);
            var result = await app.ProcessDocumentAsync(Vs02TestContext.Request(Vs02ContractNames.ProcessDocument,
                new ProcessDocument("processing-loan", "version-loan", "evidence-loan", "customer-001", DateTimeOffset.UtcNow), "loan", "loan"));
            Assert.Equal(lowConfidence ? "processing.ocr.low-confidence" : "processing.loan-statement.incomplete", result.Error?.Code);
            Assert.False(result.Error?.Retryable);
            var record = (await repository.GetAsync("processing-loan", default))!;
            Assert.Equal(ProcessingState.Failed, record.State); Assert.Empty(record.Facts);
            Assert.IsType<DomainEvent<DocumentProcessingFailedPayload>>(Assert.Single(repository.DrainOutbox()));
        }
    }
    private static string Tsv(string text, int confidence = 99) =>
        "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
        string.Join('\n', text.Split('|').SelectMany((line, index) => line.Split(' ').Select((word, number) =>
            string.Create(CultureInfo.InvariantCulture, $"5\t1\t1\t1\t{index + 1}\t{number + 1}\t10\t10\t100\t40\t{confidence}\t{word}"))));
    private sealed class TextDouble(string text) : IImageTextRecognizer
    {
        public Task<string> RecognizeAsync(byte[] image, CancellationToken cancellationToken) => Task.FromResult(text);
    }
    private sealed class SourceDouble : IEvidenceContentReader
    {
        public Task<EvidenceContent?> ReadAsync(string version, string customer, TrustedSecurityContext security, string correlation, CancellationToken cancellationToken) => Task.FromResult<EvidenceContent?>(Source());
    }
}
