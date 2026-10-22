using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class SalarySlipPdfTests
{
    private const string Metadata = "Pages: 1\nEncrypted: no\nJavaScript: no\nForm: none\nPage size: 750 x 380 pts\n";
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static EvidenceContent Source(byte[]? bytes = null)
    {
        bytes ??= Fixture("salary-slip-scan.pdf");
        return new EvidenceContent(new EvidenceReference("pdf-evidence", "pdf-document", "pdf-version", "customer-001",
            "reference://original-pdf", Convert.ToHexStringLower(SHA256.HashData(bytes)), "application/pdf"), "", bytes);
    }
    private static ConfigurationManager NativeConfiguration() => new()
    {
        ["Monergy:DocumentProcessing:OcrExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSERACT") ?? "/usr/bin/tesseract",
        ["Monergy:DocumentProcessing:OcrDataDirectory"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSDATA") ?? "/usr/share/tesseract-ocr/5/tessdata",
        ["Monergy:DocumentProcessing:PdfInfoExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_PDFINFO") ?? "/usr/bin/pdfinfo",
        ["Monergy:DocumentProcessing:PdfRenderExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_PDFTOPPM") ?? "/usr/bin/pdftoppm",
    };

    [Theory, Trait("Category", "NativeOcr")]
    [InlineData("salary-slip-scan.pdf")]
    [InlineData("salary-slip-alternate-scan.pdf")]
    public async Task RealRendererAndOcrReadImageOnlyPdfAndPreserveOriginalPdfProvenance(string fileName)
    {
        using var renderer = new PopplerPdfPageRenderer(NativeConfiguration());
        using var ocr = new TesseractImageTextRecognizer(NativeConfiguration());
        var source = Source(Fixture(fileName));
        var extractor = new StructuredDocumentExtractor(salarySlipPdf: new SalarySlipPdfExtractor(renderer, ocr));
        var fact = Assert.Single(await extractor.ExtractAsync(source, "processing-pdf", default));
        Assert.Equal(52480m, fact.CandidateValue); Assert.Equal("INR", fact.Currency);
        Assert.Equal(new DateOnly(2026, 9, 30), fact.EffectiveDate);
        Assert.Equal("INCOME", fact.FactType); Assert.Equal("Net pay", fact.Label);
        Assert.Equal(source.Reference.EvidenceId, fact.EvidenceId);
        Assert.Equal(source.Reference.CustomerId, fact.CustomerId);
        Assert.Equal(source.Reference.DocumentVersionId, fact.DocumentVersionId);
        Assert.InRange(fact.Confidence, .9m, 1m);
        Assert.StartsWith("Salary slip OCR: page 1, line ", fact.SourceLocation, StringComparison.Ordinal);
        Assert.Equal(SalarySlipPdfExtractor.ExtractionVersion, fact.ExtractionVersion);
        Assert.Equal(fact, Assert.Single(await extractor.ExtractAsync(source, "processing-pdf", default)));
        var replacement = Assert.Single(await extractor.ExtractAsync(source with
        { Reference = source.Reference with { DocumentVersionId = "pdf-replacement-version" } }, "processing-pdf", default));
        Assert.NotEqual(fact.SourceFactId, replacement.SourceFactId);
    }

    [Theory, Trait("Category", "NativeOcr")]
    [InlineData("salary-slip-multipage.pdf")]
    [InlineData("salary-slip-locked.pdf")]
    [InlineData("malformed")]
    public async Task RealRendererRejectsMultiPageEncryptedAndMalformedPdfWithoutReadingPartialFacts(string name)
    {
        using var renderer = new PopplerPdfPageRenderer(NativeConfiguration());
        var recognizer = new RecognizerDouble("");
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => new SalarySlipPdfExtractor(renderer, recognizer)
            .ExtractAsync(Source(name == "malformed" ? "%PDF-1.7\nnot-a-pdf"u8.ToArray() : Fixture(name)), "unsupported", default));
        Assert.Equal(DocumentRecognitionFailure.UnsupportedPdf, failure.Reason); Assert.Equal(0, recognizer.Calls);
    }

    [Fact, Trait("Category", "NativeOcr")]
    public async Task RealScannedInvoiceRemainsUnclassified()
    {
        using var renderer = new PopplerPdfPageRenderer(NativeConfiguration());
        using var ocr = new TesseractImageTextRecognizer(NativeConfiguration());
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => new SalarySlipPdfExtractor(renderer, ocr)
            .ExtractAsync(Source(Fixture("unclassified-scan.pdf")), "unclassified-pdf", default));
        Assert.Equal(DocumentRecognitionFailure.Unclassified, failure.Reason);
    }

    [Fact]
    public async Task InvalidSourceNeverReachesRendererOrOcr()
    {
        var renderer = new RendererDouble(); var ocr = new RecognizerDouble(""); var extractor = new SalarySlipPdfExtractor(renderer, ocr);
        var source = Source();
        foreach (var bad in new[] { source with { Reference = source.Reference with { ContentSha256 = new string('0', 64) } },
            source with { Content = "untrusted text" }, source with { BinaryContent = null }, Source([1, 2, 3]), Source(new byte[1_048_577]) })
            await Assert.ThrowsAsync<DocumentRecognitionException>(() => extractor.ExtractAsync(bad, "invalid-source", default));
        Assert.Equal(0, renderer.Calls); Assert.Equal(0, ocr.Calls);
        using var cancellation = new CancellationTokenSource(); await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync(source, "cancelled", cancellation.Token));
    }

    [Theory]
    [InlineData(89, false, DocumentRecognitionFailure.LowConfidence)]
    [InlineData(99, true, DocumentRecognitionFailure.Incomplete)]
    public async Task PdfUsesTheSameStrictFieldAndConfidenceValidation(int confidence, bool duplicate, DocumentRecognitionFailure reason)
    {
        var text = "SALARY SLIP|Pay Date: 2026-09-30|Currency: INR|Net Pay: 52480.00" + (duplicate ? "|Net Pay: 52480.00" : "");
        var tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
            string.Join('\n', text.Split('|').SelectMany((line, index) => line.Split(' ').Select((word, number) =>
                string.Create(CultureInfo.InvariantCulture, $"5\t1\t1\t1\t{index + 1}\t{number + 1}\t10\t10\t100\t40\t{confidence}\t{word}"))));
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() =>
            new SalarySlipPdfExtractor(new RendererDouble(), new RecognizerDouble(tsv)).ExtractAsync(Source(), "strict-pdf", default));
        Assert.Equal(reason, failure.Reason);
    }

    [Theory]
    [InlineData("Pages: 2\nEncrypted: no\nJavaScript: no\nForm: none\nPage size: 750 x 380 pts\n")]
    [InlineData("Pages: 1\nPages: 1\nEncrypted: no\nJavaScript: no\nForm: none\nPage size: 750 x 380 pts\n")]
    [InlineData("Pages: 1\nEncrypted: yes\nJavaScript: no\nForm: none\nPage size: 750 x 380 pts\n")]
    [InlineData("Pages: 1\nEncrypted: no\nJavaScript: yes\nForm: none\nPage size: 750 x 380 pts\n")]
    [InlineData("Pages: 1\nEncrypted: no\nJavaScript: no\nForm: AcroForm\nPage size: 750 x 380 pts\n")]
    [InlineData("Pages: 1\nEncrypted: no\nJavaScript: no\nForm: none\nPage size: 100000 x 380 pts\n")]
    public async Task ProcessMetadataDoubleRejectsUnsupportedOrSpoofedMetadataBeforeRendering(string metadata)
    {
        using var fixture = new ProcessDouble(metadata);
        using var renderer = new PopplerPdfPageRenderer(fixture.Configuration);
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => renderer.RenderAsync(Source().BinaryContent!, default));
        Assert.Equal(DocumentRecognitionFailure.UnsupportedPdf, failure.Reason); Assert.False(File.Exists(fixture.Ready));
    }

    [Fact]
    public async Task GenericPdfRenderingIncludesEveryPageWhileFinancialExtractionRejectsMultiplePages()
    {
        using var fixture = new ProcessDouble(Metadata.Replace("Pages: 1", "Pages: 2", StringComparison.Ordinal), raster: true);
        using var renderer = new PopplerPdfPageRenderer(fixture.Configuration);
        Assert.Equal(2, (await renderer.RenderPagesAsync(Source().BinaryContent!, default)).Count);
        await Assert.ThrowsAsync<DocumentRecognitionException>(() => renderer.RenderAsync(Source().BinaryContent!, default));
    }
    [Fact]
    public async Task GenericPdfRenderingRejectsExcessPagesBeforeRendering()
    {
        using var fixture = new ProcessDouble(Metadata.Replace("Pages: 1", "Pages: 21", StringComparison.Ordinal));
        using var renderer = new PopplerPdfPageRenderer(fixture.Configuration);
        await Assert.ThrowsAsync<DocumentRecognitionException>(() => renderer.RenderPagesAsync(Source().BinaryContent!, default));
        Assert.False(File.Exists(fixture.Ready));
    }
    [Fact]
    public async Task RendererFailsUnavailableForMissingOrMismatchedNativeVersion()
    {
        using var missing = new PopplerPdfPageRenderer(new ConfigurationManager());
        await Assert.ThrowsAsync<OcrEngineUnavailableException>(() => missing.RenderAsync(Source().BinaryContent!, default));
        using var fixture = new ProcessDouble(Metadata, version: "0.0.0");
        using var renderer = new PopplerPdfPageRenderer(fixture.Configuration);
        await Assert.ThrowsAsync<OcrEngineUnavailableException>(() => renderer.RenderAsync(Source().BinaryContent!, default));
        Assert.False(File.Exists(fixture.Ready));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RendererDeadlineOrInFlightCancellationReapsProcessAndDeletesPrivateInput(bool cancel)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("The native process fixture requires Linux.");
        using var fixture = new ProcessDouble(Metadata, sleep: true);
        using var renderer = new PopplerPdfPageRenderer(fixture.Configuration);
        using var cancellation = new CancellationTokenSource();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var operation = renderer.RenderAsync(Source().BinaryContent!, cancellation.Token);
        while (!File.Exists(fixture.Ready) && timer.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
        Assert.True(File.Exists(fixture.Ready));
        var state = await File.ReadAllLinesAsync(fixture.Ready);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(state[1]));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(state[1])!));
        if (cancel) { await cancellation.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); }
        else await Assert.ThrowsAsync<OcrEngineUnavailableException>(() => operation);
        Assert.False(Directory.Exists(Path.GetDirectoryName(state[1])));
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(int.Parse(state[0], CultureInfo.InvariantCulture)));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task OversizedRasterOutputStopsPromptlyWithoutWaitingForDeadline()
    {
        using var fixture = new ProcessDouble(Metadata, huge: true);
        using var renderer = new PopplerPdfPageRenderer(fixture.Configuration);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => renderer.RenderAsync(Source().BinaryContent!, default));
        Assert.Equal(DocumentRecognitionFailure.UnsupportedPdf, failure.Reason);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task UnsupportedPdfCommitsTypedFailureAndNoSourceFacts()
    {
        var repository = new InMemoryDocumentProcessingRepository();
        var source = Source();
        var app = new DocumentProcessingApplication(repository, new SourceDouble(source with
            { BinaryContent = Fixture("salary-slip-multipage.pdf"), Reference = source.Reference with
                { ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(Fixture("salary-slip-multipage.pdf"))) } }),
            new StructuredDocumentExtractor(salarySlipPdf: new SalarySlipPdfExtractor(new RejectingRenderer(), new RecognizerDouble(""))),
            new ReferenceExecutionPolicy(), new CapturingTelemetry(), TimeProvider.System);
        var result = await app.ProcessDocumentAsync(Vs02TestContext.Request(Vs02ContractNames.ProcessDocument,
            new ProcessDocument("processing-pdf-failure", "pdf-version", "pdf-evidence", "customer-001", DateTimeOffset.UtcNow),
            "pdf-failure", "pdf-failure"));
        Assert.Equal("processing.pdf.unsupported", result.Error?.Code); Assert.False(result.Error?.Retryable);
        var record = await repository.GetAsync("processing-pdf-failure", default);
        Assert.Equal(ProcessingState.Failed, record?.State); Assert.Empty(record!.Facts);
        Assert.IsType<DomainEvent<DocumentProcessingFailedPayload>>(Assert.Single(repository.DrainOutbox()));
    }

    private sealed class SourceDouble(EvidenceContent source) : IEvidenceContentReader
    {
        public Task<EvidenceContent?> ReadAsync(string version, string customer, TrustedSecurityContext security, string correlation, CancellationToken cancellationToken) => Task.FromResult<EvidenceContent?>(source);
    }
    private sealed class RejectingRenderer : IPdfPageRenderer
    {
        public Task<byte[]> RenderAsync(byte[] pdf, CancellationToken cancellationToken) =>
            throw new DocumentRecognitionException(DocumentRecognitionFailure.UnsupportedPdf);
    }

    private sealed class RendererDouble : IPdfPageRenderer
    {
        public int Calls { get; private set; }
        public Task<byte[]> RenderAsync(byte[] pdf, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(Fixture("salary-slip.png")); }
    }
    private sealed class RecognizerDouble(string text) : IImageTextRecognizer
    {
        public int Calls { get; private set; }
        public Task<string> RecognizeAsync(byte[] png, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(text); }
    }
    // Explicit native-process protocol fixture, never PDF rendering or OCR proof.
    private sealed class ProcessDouble : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "monergy-pdf-test-" + Guid.NewGuid().ToString("N"));
        public string Ready => Path.Combine(root, "ready");
        public ConfigurationManager Configuration { get; } = new();
        public ProcessDouble(string metadata, bool sleep = false, bool huge = false, bool raster = false, string version = PopplerPdfPageRenderer.EngineVersion)
        {
            if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("The native process fixture requires Linux.");
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string Literal(string value) => System.Text.Json.JsonSerializer.Serialize(value);
            foreach (var name in new[] { "pdfinfo", "pdftoppm" })
            {
                var path = Path.Combine(root, name);
                var script = "#!/usr/bin/python3\nimport sys, os, time\n" +
                    "if '-v' in sys.argv:\n sys.stderr.write(" + Literal(name + " version " + version + "\n") + ")\n sys.exit(0)\n" +
                    (name == "pdfinfo" ? "sys.stdout.write(" + Literal(metadata) + ")\n" :
                        "with open(" + Literal(Ready + ".tmp") + ", 'w') as f:\n f.write(str(os.getpid()) + '\\n' + sys.argv[-1])\n" +
                        "os.rename(" + Literal(Ready + ".tmp") + ", " + Literal(Ready) + ")\n" +
                        (sleep ? "time.sleep(60)\n" : raster ? "sys.stdout.buffer.write(bytes.fromhex(" + Literal(Convert.ToHexString(Fixture("salary-slip.png"))) + "))\n" : huge ? "sys.stdout.buffer.write(b'x' * 1048577)\nsys.stdout.flush()\ntime.sleep(60)\n" : "sys.exit(1)\n"));
                File.WriteAllText(path, script); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Configuration["Monergy:DocumentProcessing:" + (name == "pdfinfo" ? "PdfInfoExecutable" : "PdfRenderExecutable")] = path;
            }
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
