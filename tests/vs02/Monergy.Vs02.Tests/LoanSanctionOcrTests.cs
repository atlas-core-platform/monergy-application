using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class LoanSanctionOcrTests
{
    private const string Valid = "LOAN SANCTION LETTER|Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: 650,000.00|EMI: 14,500.75";
    private static EvidenceContent Source(string file = "loan-sanction.png", string mime = "image/png")
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        return new(new EvidenceReference("evidence-sanction", "document-sanction", "version-sanction", "customer-001",
            "reference://loan-sanction", Convert.ToHexStringLower(SHA256.HashData(bytes)), mime), "", bytes);
    }
    private static ConfigurationManager Configuration() => new()
    {
        ["Monergy:DocumentProcessing:OcrExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSERACT") ?? "/usr/bin/tesseract",
        ["Monergy:DocumentProcessing:OcrDataDirectory"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSDATA") ?? "/usr/share/tesseract-ocr/5/tessdata",
        ["Monergy:DocumentProcessing:PdfInfoExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_PDFINFO") ?? "/usr/bin/pdfinfo",
        ["Monergy:DocumentProcessing:PdfRenderExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_PDFTOPPM") ?? "/usr/bin/pdftoppm",
    };
    [Theory, Trait("Category", "NativeOcr")]
    [InlineData("loan-sanction.png", "image/png", "png")]
    [InlineData("loan-sanction.jpg", "image/jpeg", "jpeg")]
    [InlineData("loan-sanction-scan.pdf", "application/pdf", "pdf")]
    public async Task PinnedNativeOcrReadsBothFiguresWithExactOriginalProvenance(string file, string mime, string format)
    {
        using var engine = new TesseractImageTextRecognizer(Configuration());
        using var renderer = new PopplerPdfPageRenderer(Configuration());
        var extractor = new StructuredDocumentExtractor(new SalarySlipPngExtractor(engine), new SalarySlipPdfExtractor(renderer, engine), new SalarySlipJpegExtractor(engine));
        var source = Source(file, mime);
        var facts = await extractor.ExtractAsync(source, "sanction-run", default);
        AssertFacts(facts); Assert.All(facts, fact => {
            Assert.Equal(source.Reference.EvidenceId, fact.EvidenceId); Assert.Equal(source.Reference.DocumentVersionId, fact.DocumentVersionId);
            Assert.Equal(source.Reference.CustomerId, fact.CustomerId); Assert.Equal($"loan-sanction-{format}-ocr/1.0.0", fact.ExtractionVersion);
            Assert.StartsWith("Loan sanction letter OCR: page 1, line ", fact.SourceLocation, StringComparison.Ordinal);
            Assert.Contains("Sanction Date: 2026-10-01", fact.SourceLocation, StringComparison.Ordinal); Assert.InRange(fact.Confidence, .9m, 1m);
        });
        Assert.Equal(facts, await extractor.ExtractAsync(source, "sanction-run", default));
        var replacement = await extractor.ExtractAsync(source with { Reference = source.Reference with { DocumentVersionId = "sanction-next" } }, "sanction-run", default);
        Assert.All(replacement, fact => Assert.DoesNotContain(facts, previous => previous.SourceFactId == fact.SourceFactId));
    }
    [Theory, Trait("Category", "NativeOcr")]
    [InlineData("loan-sanction-incomplete.png", "image/png", DocumentRecognitionFailure.IncompleteSanction)]
    [InlineData("loan-sanction-mixed.jpg", "image/jpeg", DocumentRecognitionFailure.Unclassified)]
    public async Task NativeIncompleteOrMixedSanctionHasNoCandidatePrefix(string file, string mime, DocumentRecognitionFailure expected)
    {
        using var engine = new TesseractImageTextRecognizer(Configuration());
        var extractor = new StructuredDocumentExtractor(new SalarySlipPngExtractor(engine), salarySlipJpeg: new SalarySlipJpegExtractor(engine));
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => extractor.ExtractAsync(Source(file, mime), "bad-sanction", default));
        Assert.Equal(expected, failure.Reason);
    }
    [Theory]
    [InlineData("LOAN SANCTION LETTER|Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: 650000|EMI: 14500.75")]
    [InlineData("LOAN SANCTION LETTER|Sanction Date: 1 October 2026|Sanction Amount: INR 650,000.00|EMI Amount: 14,500.75 INR")]
    [InlineData("Loan Sanction Letter|Sanction Date: 01 Oct 2026|Currency: inr|Sanctioned Amount: 650000.00 inr|EMI: INR 14500.75")]
    public async Task LabelledSanctionFiguresKeepMeaningWithoutPeriodOrAssetInference(string text)
    {
        var facts = await new SalarySlipPngExtractor(new TextDouble(Tsv(text))).ExtractAsync(Source(), "sanction-fields", default);
        AssertFacts(facts); Assert.Equal(2, facts.Select(f => f.SourceFactId).Distinct(StringComparer.Ordinal).Count());
    }
    [Theory]
    [InlineData("Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: 650000")]
    [InlineData("Sanction Date: 2026-10-01|Currency: INR|EMI: 14500.75")]
    [InlineData("Disbursement Date: 2026-10-01|Currency: INR|Sanctioned Amount: 650000|EMI: 14500.75")]
    [InlineData("Sanction Date: 01/10/2026|Currency: INR|Sanctioned Amount: 650000|EMI: 14500.75")]
    [InlineData("Sanction Date: 2026-02-30|Currency: INR|Sanctioned Amount: 650000|EMI: 14500.75")]
    [InlineData("Sanction Date: 2026-10-01|Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: 650000|EMI: 14500.75")]
    [InlineData("Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: 650000|Sanction Amount: 650000|EMI: 14500.75")]
    [InlineData("Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: 650000|EMI: 14500.75|EMI Amount: 14500.75")]
    [InlineData("Sanction Date: 2026-10-01|Currency: INR|Currency: INR|Sanctioned Amount: 650000|EMI: 14500.75")]
    [InlineData("Sanction Date: 2026-10-01|Sanctioned Amount: 650000|EMI: 14500.75")]
    [InlineData("Sanction Date: 2026-10-01|Currency: XYZ|Sanctioned Amount: 650000|EMI: 14500.75")]
    [InlineData("Sanction Date: 2026-10-01|Sanctioned Amount: INR 650000|EMI: USD 14500.75")]
    [InlineData("Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: USD 650000|EMI: 14500.75")]
    [InlineData("Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: 650000|EMI: USD 14500.75")]
    [InlineData("Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: -100|EMI: 14500.75")]
    [InlineData("Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: 650000|EMI: -10")]
    [InlineData("Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: 65O000|EMI: 14500.75")]
    [InlineData("Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: 650000|EMI: 14,500.750")]
    [InlineData("Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: 6,50,000|EMI: 14500.75")]
    [InlineData("Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: INR 650000 INR|EMI: 14500.75")]
    [InlineData("Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: 650000|Outstanding Balance: 14500.75")]
    public async Task InvalidOrMissingEitherFigureRejectsEntireSanction(string fields)
    {
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => new SalarySlipPngExtractor(new TextDouble(
            Tsv("LOAN SANCTION LETTER|" + fields))).ExtractAsync(Source(), "bad-fields", default));
        Assert.Equal(DocumentRecognitionFailure.IncompleteSanction, failure.Reason);
    }
    [Theory]
    [InlineData("LOAN SANCTION LETTER|SALARY SLIP")]
    [InlineData("SALARY SLIP|LOAN SANCTION LETTER")]
    [InlineData("LOAN SANCTION LETTER|INVESTMENT STATEMENT")]
    [InlineData("INVESTMENT STATEMENT|LOAN SANCTION LETTER")]
    [InlineData("LOAN SANCTION LETTER|INSURANCE POLICY")]
    [InlineData("INSURANCE POLICY|LOAN SANCTION LETTER")]
    [InlineData("LOAN SANCTION LETTER|LOAN STATEMENT")]
    [InlineData("LOAN STATEMENT|LOAN SANCTION LETTER")]
    [InlineData("LOAN SANCTION LETTER|LOAN SANCTION LETTER")]
    [InlineData("LOAN OFFER")]
    [InlineData("Header|Header|Header|Header|Header|Header|Header|Header|Header|Header|LOAN SANCTION LETTER")]
    public async Task UnknownDuplicateMixedOrLateTitlesRemainUnclassified(string title)
    {
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => new SalarySlipPngExtractor(new TextDouble(
            Tsv(title + "|Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: 650000|EMI: 14500.75|Pay Date: 2026-10-01|Net Pay: 1|Sanction Date: 2026-10-01|Outstanding Balance: 1"))).ExtractAsync(Source(), "mixed", default));
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
            new StructuredDocumentExtractor(new SalarySlipPngExtractor(new TextDouble(Tsv(low ? Valid : "LOAN SANCTION LETTER", low ? 4 : 0)))),
            new ReferenceExecutionPolicy(), new CapturingTelemetry(), TimeProvider.System);
        var result = await app.ProcessDocumentAsync(Vs02TestContext.Request(Vs02ContractNames.ProcessDocument,
            new ProcessDocument("sanction-run", "version-sanction", "evidence-sanction", "customer-001", DateTimeOffset.UtcNow), "sanction-fail", "sanction-fail"));
        Assert.Equal(low ? "processing.ocr.low-confidence" : "processing.loan-sanction.incomplete", result.Error?.Code); Assert.False(result.Error?.Retryable);
        var record = (await repository.GetAsync("sanction-run", default))!; Assert.Equal(ProcessingState.Failed, record.State); Assert.Empty(record.Facts);
        Assert.IsType<DomainEvent<DocumentProcessingFailedPayload>>(Assert.Single(repository.DrainOutbox()));
    }
    [Theory]
    [InlineData("0", 0)][InlineData("12500.00", 12500)]
    public async Task LiteralEmiNeverCreatesAnOutstandingBalanceOrRepaymentStatus(string emi, int expected)
    {
        var facts = await new SalarySlipPngExtractor(new TextDouble(Tsv(
            "LOAN SANCTION LETTER|Sanction Date: 2026-10-01|Currency: INR|Sanctioned Amount: 650000|EMI: " + emi)))
            .ExtractAsync(Source(), "literal-values", default);
        Assert.Equal(2, facts.Count); Assert.Equal(650000m, facts[0].CandidateValue); Assert.Equal((decimal)expected, facts[1].CandidateValue);
        Assert.Equal("EMI stated on sanction letter", facts[1].Label); Assert.All(facts, fact => Assert.Equal("LOAN", fact.FactType));
    }
    [Fact]
    public async Task ReviewedSanctionTermsDoNotReplaceThePreviouslyReviewedOutstandingBalance()
    {
        var terms = await new SalarySlipPngExtractor(new TextDouble(Tsv(Valid))).ExtractAsync(Source(), "terms-run", default);
        var balance = terms[0] with { SourceFactId = "prior-outstanding", Label = "Outstanding loan balance stated on document",
            CandidateValue = 111000m, EffectiveDate = new DateOnly(2026,9,30), EvidenceId = "prior-evidence", DocumentVersionId = "prior-version",
            ExtractionVersion = "loan-statement-png-ocr/1.0.0", SourceLocation = "Loan statement OCR: page 1, line 4" };
        var financial = new Monergy.Services.FinancialProfile.Application.FinancialProfileApplication(
            new Monergy.Services.FinancialProfile.Infrastructure.InMemoryFinancialProfileRepository(), new CapturingTelemetry(), TimeProvider.System);
        var old = await financial.NormalizeSourceFactsAsync(Vs02TestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts("profile-loan", "customer-001", "balance-run", [balance], "provider-neutral-normalization/1.0.0", DateTimeOffset.UtcNow), "review-balance", "review-balance"));
        Assert.Equal(ContractOutcome.Success, old.Outcome);
        var adopted = await financial.NormalizeSourceFactsAsync(Vs02TestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts("profile-loan", "customer-001", "terms-run", terms, "provider-neutral-normalization/1.0.0", DateTimeOffset.UtcNow), "review-terms", "review-terms"));
        Assert.Equal(ContractOutcome.Success, adopted.Outcome);
        var profile = await financial.GetFinancialProfileAsync(Vs02TestContext.Request(Vs02ContractNames.GetFinancialProfile,
            new GetFinancialProfile("profile-loan", "customer-001"), "read-loan"));
        Assert.Equal(3, profile.Data!.Facts.Count);
        var retained = Assert.Single(profile.Data.Facts, f => f.Label == balance.Label);
        Assert.Equal(111000m, retained.Value); Assert.Equal(new DateOnly(2026,9,30), retained.EffectiveDate); Assert.Equal(1, retained.Revision);
        Assert.Equal(650000m, Assert.Single(profile.Data.Facts, f => f.Label == terms[0].Label).Value);
        Assert.Equal(14500.75m, Assert.Single(profile.Data.Facts, f => f.Label == terms[1].Label).Value);
    }
    private static void AssertFacts(IReadOnlyList<ValidatedSourceFact> facts)
    {
        Assert.Equal(2, facts.Count); Assert.All(facts, f => { Assert.Equal("LOAN", f.FactType); Assert.Equal("INR", f.Currency); Assert.Equal(new DateOnly(2026,10,1),f.EffectiveDate); });
        Assert.Equal(650000m, facts[0].CandidateValue); Assert.Equal("Sanctioned loan amount stated on letter", facts[0].Label);
        Assert.Equal(14500.75m, facts[1].CandidateValue); Assert.Equal("EMI stated on sanction letter", facts[1].Label);
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
