using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Monergy.Services.Evidence;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class SalarySlipOcrTests
{
    private static byte[] Fixture(string name = "salary-slip.png") => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static EvidenceContent Source(byte[]? bytes = null)
    {
        bytes ??= Fixture();
        return new EvidenceContent(new EvidenceReference("evidence-001", "document-001", "document-version-001",
            "customer-001", "reference://salary-slip", Convert.ToHexStringLower(SHA256.HashData(bytes)), "image/png"), "", bytes);
    }
    private static ConfigurationManager NativeConfiguration() => new()
    {
        ["Monergy:DocumentProcessing:OcrExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSERACT") ?? "/usr/bin/tesseract",
        ["Monergy:DocumentProcessing:OcrDataDirectory"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSDATA") ?? "/usr/share/tesseract-ocr/5/tessdata",
        ["Monergy:DocumentProcessing:PdfInfoExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_PDFINFO") ?? "/usr/bin/pdfinfo",
        ["Monergy:DocumentProcessing:PdfRenderExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_PDFTOPPM") ?? "/usr/bin/pdftoppm",
    };

    [Theory, Trait("Category", "NativeOcr")]
    [InlineData("salary-slip.png")]
    [InlineData("salary-slip-alternate.png")]
    public async Task RealPinnedEngineReadsDifferentPrintedLayoutsWithExactProvenance(string name)
    {
        using var engine = new TesseractImageTextRecognizer(NativeConfiguration());
        var extractor = new StructuredDocumentExtractor(new SalarySlipPngExtractor(engine));
        var source = Source(Fixture(name));
        var fact = Assert.Single(await extractor.ExtractAsync(source, "processing-native", default));
        Assert.Equal(52480m, fact.CandidateValue);
        Assert.Equal("INR", fact.Currency);
        Assert.Equal(new DateOnly(2026, 9, 30), fact.EffectiveDate);
        Assert.Equal("INCOME", fact.FactType);
        Assert.Equal("Net pay", fact.Label);
        Assert.Equal(source.Reference.EvidenceId, fact.EvidenceId);
        Assert.Equal(source.Reference.CustomerId, fact.CustomerId);
        Assert.Equal(source.Reference.DocumentVersionId, fact.DocumentVersionId);
        Assert.InRange(fact.Confidence, .9m, 1m);
        Assert.StartsWith("Salary slip OCR: page 1, line ", fact.SourceLocation, StringComparison.Ordinal);
        Assert.Equal(SalarySlipPngExtractor.ExtractionVersion, fact.ExtractionVersion);
        Assert.Equal(fact, Assert.Single(await extractor.ExtractAsync(source, "processing-native", default)));
    }

    [Fact, Trait("Category", "NativeOcr")]
    public async Task RealEngineDoesNotTurnAnInvoiceIntoSalary()
    {
        using var engine = new TesseractImageTextRecognizer(NativeConfiguration());
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() =>
            new SalarySlipPngExtractor(engine).ExtractAsync(Source(Fixture("unclassified-image.png")), "invoice", default));
        Assert.Equal(DocumentRecognitionFailure.Unclassified, failure.Reason);
    }

    [Theory]
    [InlineData("SALARY SLIP|Pay Date: 2026-09-30|Currency: INR|Net Pay: 52480.00|Net Salary: 52480.00")]
    [InlineData("SALARY SLIP|Pay Date: 09/10/2026|Currency: INR|Net Pay: 52480.00")]
    [InlineData("SALARY SLIP|Pay Date: 2026-02-30|Currency: INR|Net Pay: 52480.00")]
    [InlineData("SALARY SLIP|Pay Period: September 2026|Currency: INR|Net Pay: 52480.00")]
    [InlineData("SALARY SLIP|Pay Date: 2026-09-30|Net Pay: 52480.00")]
    [InlineData("SALARY SLIP|Pay Date: 2026-09-30|Currency: USD|Net Pay: INR 52480.00")]
    [InlineData("SALARY SLIP|Pay Date: 2026-09-30|Currency: INR|Currency: INR|Net Pay: 52480.00")]
    [InlineData("SALARY SLIP|Pay Date: 2026-09-30|Currency: INR|Net Pay: 52,48O.00")]
    [InlineData("SALARY SLIP|Pay Date: 2026-09-30|Currency: INR|Net Pay: -10.00")]
    [InlineData("SALARY SLIP|Pay Date: 2026-09-30|Currency: INR|Net Pay: 5,24,800.00")]
    [InlineData("SALARY SLIP|Pay Date: 2026-09-30|Currency: INR|Gross Pay: 60000|Deductions: 7520")]
    [InlineData("SALARY SLIP|Pay Date: 2026-09-30|Currency: XYZ|Net Pay: 52480.00")]
    [InlineData("SALARY SLIP|Pay Date: 2026-09-30|Currency: INR|Net Pay: INR 52480.00 INR")]
    public async Task IncompleteAmbiguousOrUnlabelledImagesProduceNoCandidates(string text)
    {
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() =>
            new SalarySlipPngExtractor(new TextDouble(Tsv(text))).ExtractAsync(Source(), "invalid", default));
        Assert.Equal(DocumentRecognitionFailure.Incomplete, failure.Reason);
    }

    [Theory]
    [InlineData("SALARY SLIP|Pay Date: 2026-09-30|Currency: INR|Net Pay: 52480.00", 89, DocumentRecognitionFailure.LowConfidence)]
    [InlineData("INVOICE|Pay Date: 2026-09-30|Currency: INR|Net Pay: 52480.00", 99, DocumentRecognitionFailure.Unclassified)]
    public async Task TitleAndWordConfidenceAreRequired(string text, int confidence, DocumentRecognitionFailure reason)
    {
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() =>
            new SalarySlipPngExtractor(new TextDouble(Tsv(text, confidence))).ExtractAsync(Source(), "unclear", default));
        Assert.Equal(reason, failure.Reason);
    }

    [Fact]
    public async Task RejectsMismatchedBytesAndOversizedRasterBeforeCallingNativeEngine()
    {
        var engine = new TextDouble(""); var extractor = new SalarySlipPngExtractor(engine);
        await Assert.ThrowsAsync<DocumentRecognitionException>(() => extractor.ExtractAsync(
            Source() with { Reference = Source().Reference with { ContentSha256 = new string('0', 64) } }, "wrong-hash", default));
        var oversized = Fixture(); BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(16, 4), 65536);
        await Assert.ThrowsAsync<DocumentRecognitionException>(() => extractor.ExtractAsync(Source(oversized), "large", default));
        await Assert.ThrowsAsync<DocumentRecognitionException>(() => extractor.ExtractAsync(Source([1, 2, 3]), "bad", default));
        Assert.Equal(0, engine.Calls);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n5\t2\t1\t1\t1\t1\t0\t0\t100\t40\t99\tSALARY")]
    public async Task InvalidNativeOutputFailsClosed(string tsv) => await Assert.ThrowsAsync<DocumentRecognitionException>(() =>
        new SalarySlipPngExtractor(new TextDouble(tsv)).ExtractAsync(Source(), "bad-tsv", default));

    [Fact]
    public async Task NativeUnavailableAndCancellationAreDistinctAndNeverReturnCandidates()
    {
        using var unavailable = new TesseractImageTextRecognizer(new ConfigurationManager());
        await Assert.ThrowsAsync<OcrEngineUnavailableException>(() => unavailable.RecognizeAsync(Fixture(), default));
        using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
        using var native = new TesseractImageTextRecognizer(NativeConfiguration());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => native.RecognizeAsync(Fixture(), cancelled.Token));
    }

    [Theory]
    [InlineData(false, "processing.document.unclassified", false)]
    [InlineData(true, "processing.ocr.unavailable", true)]
    public async Task ProcessingRecordsTypedFailureAndNoFacts(bool unavailable, string code, bool retryable)
    {
        var repository = new InMemoryDocumentProcessingRepository();
        var engine = new TextDouble(Tsv("INVOICE"), unavailable);
        var app = new DocumentProcessingApplication(repository, new SourceDouble(Source()),
            new StructuredDocumentExtractor(new SalarySlipPngExtractor(engine)), new ReferenceExecutionPolicy(),
            new CapturingTelemetry(), TimeProvider.System);
        var result = await app.ProcessDocumentAsync(Vs02TestContext.Request(Vs02ContractNames.ProcessDocument,
            new ProcessDocument("processing-ocr", "document-version-001", "evidence-001", "customer-001", DateTimeOffset.UtcNow), "process-ocr", "process-ocr"));
        Assert.Equal(code, result.Error?.Code); Assert.Equal(retryable, result.Error?.Retryable);
        var record = await repository.GetAsync("processing-ocr", default);
        Assert.Equal(ProcessingState.Failed, record?.State); Assert.Empty(record!.Facts);
        Assert.IsType<DomainEvent<DocumentProcessingFailedPayload>>(Assert.Single(repository.DrainOutbox()));
    }

    [Theory, Trait("Category", "NativeOcr")]
    [InlineData("salary-slip.png", "image/png")]
    [InlineData("salary-slip-scan.pdf", "application/pdf")]
    [InlineData("salary-slip.jpg", "image/jpeg")]
    [InlineData("loan-statement.png", "image/png")]
    [InlineData("loan-statement.jpg", "image/jpeg")]
    [InlineData("loan-statement-scan.pdf", "application/pdf")]
    [InlineData("insurance-policy.png", "image/png")]
    [InlineData("insurance-policy.jpg", "image/jpeg")]
    [InlineData("insurance-policy-scan.pdf", "application/pdf")]
    [InlineData("investment-statement.png", "image/png")]
    [InlineData("investment-statement.jpg", "image/jpeg")]
    [InlineData("investment-statement-scan.pdf", "application/pdf")]
    [InlineData("loan-sanction.png", "image/png")]
    [InlineData("loan-sanction.jpg", "image/jpeg")]
    [InlineData("loan-sanction-scan.pdf", "application/pdf")]
    [InlineData("vehicle-purchase-invoice.png", "image/png")]
    [InlineData("vehicle-purchase-invoice.jpg", "image/jpeg")]
    [InlineData("vehicle-purchase-invoice-scan.pdf", "application/pdf")]
    [InlineData("property-registration.png", "image/png")]
    [InlineData("property-registration.jpg", "image/jpeg")]
    [InlineData("property-registration-scan.pdf", "application/pdf")]
    [InlineData("property-sale-deed.png", "image/png")]
    [InlineData("property-sale-deed.jpg", "image/jpeg")]
    [InlineData("property-sale-deed-scan.pdf", "application/pdf")]
    public async Task OwnerHttpBoundaryDeliversExactBinaryToRealOcrAndCustomerBoundProcessing(string fileName, string contentType)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monergy:ReferenceAdapters"] = "true", ["Monergy:ExecutionZone"] = "LOCAL",
            ["Monergy:ProductIntake:EvidenceToken"] = new string('a', 64),
            ["Monergy:ProductIntake:ProcessingToken"] = new string('b', 64),
            ["Monergy:TenantBoundary:CustomerTenants:customer-001"] = "T001",
        });
        var store = new ReferenceEvidenceContentStore(); var repository = new InMemoryEvidenceRepository();
        builder.Services.AddSingleton(store); builder.Services.AddSingleton<IEvidenceRepository>(repository);
        builder.Services.AddSingleton<IEvidenceMalwareScanner, FixtureMalwareScanner>();
        await using var host = builder.Build(); host.MapEvidenceReferenceIntake(builder.Configuration); await host.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
        client.DefaultRequestHeaders.Add("X-Monergy-Product-Token", new string('a', 64));
        var bytes = Fixture(fileName);
        using var staged = await client.PostAsJsonAsync("/internal/local/v1/evidence/content", new ReferenceEvidenceContentRequest(
            new string('c', 32), Convert.ToBase64String(bytes), contentType));
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        var descriptor = (await staged.Content.ReadFromJsonAsync<EvidenceContentDescriptor>())!;
        var evidence = new EvidenceApplication(repository, store, new CapturingTelemetry(), TimeProvider.System);
        var created = await evidence.CreateDocumentVersionAsync(Vs02TestContext.Request(Vs02ContractNames.CreateDocumentVersion,
            Vs02TestContext.CreateVersionPayload() with { OriginalFileName = fileName, DeclaredContentType = contentType,
                ContentReference = descriptor.Reference, ContentSha256 = descriptor.Sha256 }, "create-png", "create-png"));
        Assert.Equal(ContractOutcome.Success, created.Outcome);
        var config = new ConfigurationManager { ["Monergy:DocumentProcessing:EvidenceUrl"] = client.BaseAddress.AbsoluteUri,
            ["Monergy:DocumentProcessing:EvidenceToken"] = new string('b', 64) };
        using var reader = new HttpEvidenceContentReader(config);
        var security = Vs02TestContext.Security() with { Workload = new WorkloadContext("job-management", "job-management:T001") };
        var source = await reader.ReadAsync("document-version-001", "customer-001", security, "correlation-ocr", default);
        Assert.Equal(bytes, source?.BinaryContent); Assert.Equal("", source?.Content);
        var exposed = await store.ReadBytesAsync(descriptor.Reference, default); exposed![0] = 0;
        Assert.Equal(bytes, await store.ReadBytesAsync(descriptor.Reference, default));
        // Unknown customers are resolved against immutable evidence ownership;
        // a mismatched owner is hidden as not-found (registered customers need no fixture binding).
        Assert.Null(await reader.ReadAsync("document-version-001", "customer-other", security, "correlation-ocr", default));
        Assert.Null(await reader.ReadAsync("missing-version", "customer-001", security, "correlation-ocr", default));
        await Assert.ThrowsAsync<EvidenceDependencyException>(() => reader.ReadAsync("document-version-001", "customer-001",
            security with { Workload = new WorkloadContext("job-management", "job-management:T002") }, "wrong-tenant", default));
        using var engine = new TesseractImageTextRecognizer(NativeConfiguration());
        using var pdfRenderer = new PopplerPdfPageRenderer(NativeConfiguration());
        var processingRepository = new InMemoryDocumentProcessingRepository();
        var processing = new DocumentProcessingApplication(processingRepository, reader,
            new StructuredDocumentExtractor(new SalarySlipPngExtractor(engine), new SalarySlipPdfExtractor(pdfRenderer, engine), new SalarySlipJpegExtractor(engine)), new ReferenceExecutionPolicy(), new CapturingTelemetry(), TimeProvider.System);
        var result = await processing.ProcessDocumentAsync(Vs02TestContext.Request(Vs02ContractNames.ProcessDocument,
            new ProcessDocument("processing-png", "document-version-001", "evidence-001", "customer-001", DateTimeOffset.UtcNow),
            "process-png", "process-png", security));
        Assert.Equal(ProcessingState.Completed, result.Data?.State);
        var record = (await processingRepository.GetAsync("processing-png", default))!;
        var vehicle = fileName.StartsWith("vehicle-purchase-invoice", StringComparison.Ordinal);
        var registration = fileName.StartsWith("property-registration", StringComparison.Ordinal);
        var property = registration || fileName.StartsWith("property-sale-deed", StringComparison.Ordinal);
        var sanction = fileName.StartsWith("loan-sanction", StringComparison.Ordinal);
        var investment = fileName.StartsWith("investment-", StringComparison.Ordinal);
        var insurance = fileName.StartsWith("insurance-", StringComparison.Ordinal);
        Assert.Equal(insurance || investment || sanction ? 2 : 1, record.Facts.Count);
        var fact = record.Facts[0];
        if (insurance)
        {
            Assert.Equal(24500.50m, record.Facts[1].CandidateValue);
            Assert.Equal("Premium stated on policy (frequency not established)", record.Facts[1].Label);
            Assert.All(record.Facts, f => Assert.Equal("INSURANCE", f.FactType));
        }
        if (investment)
        {
            Assert.Equal(925750.25m, record.Facts[1].CandidateValue);
            Assert.Equal("Current value stated on statement", record.Facts[1].Label);
            Assert.All(record.Facts, f => Assert.Equal("INVESTMENT", f.FactType));
        }
        if (sanction)
        {
            Assert.Equal(14500.75m, record.Facts[1].CandidateValue);
            Assert.Equal("EMI stated on sanction letter", record.Facts[1].Label);
            Assert.All(record.Facts, f => Assert.Equal("LOAN", f.FactType));
        }
        var loan = fileName.StartsWith("loan-", StringComparison.Ordinal);
        Assert.Equal(vehicle ? 680000m : property ? 3500000m : sanction ? 650000m : investment ? 800000m : insurance ? 1000000m : loan ? 176500.75m : 52480m, fact.CandidateValue);
        Assert.Equal(vehicle || property ? "ASSET" : sanction ? "LOAN" : investment ? "INVESTMENT" : insurance ? "INSURANCE" : loan ? "LOAN" : "INCOME", fact.FactType);
        Assert.Equal(source!.Reference.DocumentVersionId, fact.DocumentVersionId);
        Assert.Equal(source.Reference.CustomerId, fact.CustomerId);
        Assert.Equal(source.Reference.EvidenceId, fact.EvidenceId);
        Assert.IsType<DomainEvent<ValidatedSourceFactsProducedPayload>>(Assert.Single(processingRepository.DrainOutbox()));
        if (insurance || investment || sanction || property || vehicle)
        {
            var financial = new Monergy.Services.FinancialProfile.Application.FinancialProfileApplication(
                new Monergy.Services.FinancialProfile.Infrastructure.InMemoryFinancialProfileRepository(), new CapturingTelemetry(), TimeProvider.System);
            var reviewedTypes = Monergy.Platform.FinancialSourceFactBoundary.ReviewedTypes(record.Facts.Select(f => (f.SourceFactId, f.FactType, (string?)f.ExtractionVersion)).ToArray(), null);
            var adopted = await financial.NormalizeSourceFactsAsync(Vs02TestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
                new NormalizeSourceFacts("profile-insurance", "customer-001", "processing-png", record.Facts.Select(f => f with { FactType = reviewedTypes[f.SourceFactId] }).ToArray(),
                    "provider-neutral-normalization/1.0.0", DateTimeOffset.UtcNow), "review-insurance", "review-insurance"));
            Assert.Equal(ContractOutcome.Success, adopted.Outcome); Assert.Equal(vehicle || property ? 1 : 2, adopted.Data?.Facts.Count);
            Assert.All(adopted.Data!.Facts, item => Assert.Equal(vehicle ? FinancialObjectTypes.Vehicle : property ? FinancialObjectTypes.Property : sanction ? FinancialObjectTypes.Loan : investment ? FinancialObjectTypes.Investment : FinancialObjectTypes.InsurancePolicy, item.FactType));
            foreach (var item in adopted.Data.Facts)
            {
                var provenance = await financial.GetFinancialProvenanceAsync(Vs02TestContext.Request(Vs02ContractNames.GetFinancialProvenance,
                    new GetFinancialProvenance(item.FinancialProvenanceId, "customer-001"), "insurance-source"));
                Assert.Equal("document-version-001", provenance.Data?.DocumentVersionId);
                Assert.StartsWith(vehicle ? "vehicle-purchase-invoice-" : registration ? "property-registration-" : property ? "property-sale-deed-" : sanction ? "loan-sanction-" : investment ? "investment-statement-" : "insurance-policy-", provenance.Data!.ExtractionVersion, StringComparison.Ordinal);
            }
        }
        // This path publishes candidates only. The existing Financial Profile review/adoption API remains the sole writer.
    }

    [Theory, Trait("Category", "NativeOcr")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeProcessDeadlineAndInFlightCancellationReapProcessAndRemovePrivateInput(bool cancel)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("This LOCAL native lifecycle check requires Linux.");
        var root = Path.Combine(Path.GetTempPath(), "monergy-ocr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var executable = Path.Combine(root, "engine-double");
            var ready = Path.Combine(root, "ready");
            // Explicit process-protocol double: it never purports to recognize document text.
            var script = "#!/usr/bin/python3\nimport sys, time, os\n" +
                "if '--version' in sys.argv:\n print('tesseract 5.3.4')\n sys.exit(0)\n" +
                "with open(" + System.Text.Json.JsonSerializer.Serialize(ready) + ", 'w') as f:\n f.write(str(os.getpid()) + '\\n' + sys.argv[1])\n" +
                "time.sleep(60)\n";
            await File.WriteAllTextAsync(executable, script);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var config = NativeConfiguration(); config["Monergy:DocumentProcessing:OcrExecutable"] = executable;
            using var engine = new TesseractImageTextRecognizer(config);
            using var cancellation = new CancellationTokenSource();
            var operation = engine.RecognizeAsync(Fixture(), cancellation.Token);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(ready) && timer.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
            Assert.True(File.Exists(ready), "The process double did not reach its input phase.");
            var state = await File.ReadAllLinesAsync(ready);
            Assert.Equal(2, state.Length);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(state[1]));
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            }
            else await Assert.ThrowsAsync<OcrEngineUnavailableException>(() => operation);
            Assert.False(Directory.Exists(Path.GetDirectoryName(state[1])));
            Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(int.Parse(state[0], CultureInfo.InvariantCulture)));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(12));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Tsv(string text, int confidence = 99) =>
        "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
        string.Join('\n', text.Split('|').SelectMany((line, index) => line.Split(' ').Select((word, number) =>
            string.Create(CultureInfo.InvariantCulture, $"5\t1\t1\t1\t{index + 1}\t{number + 1}\t10\t10\t100\t40\t{confidence}\t{word}"))));
    private sealed class TextDouble(string text, bool unavailable = false) : IImageTextRecognizer
    {
        public int Calls { get; private set; }
        public Task<string> RecognizeAsync(byte[] png, CancellationToken cancellationToken)
        { Calls++; return unavailable ? throw new OcrEngineUnavailableException() : Task.FromResult(text); }
    }
    private sealed class SourceDouble(EvidenceContent content) : IEvidenceContentReader
    {
        public Task<EvidenceContent?> ReadAsync(string version, string customer, TrustedSecurityContext security, string correlation, CancellationToken cancellationToken) => Task.FromResult<EvidenceContent?>(content);
    }
}
