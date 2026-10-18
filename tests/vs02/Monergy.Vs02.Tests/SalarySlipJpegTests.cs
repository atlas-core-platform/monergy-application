using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class SalarySlipJpegTests
{
    private static byte[] Fixture(string name = "salary-slip.jpg") => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static EvidenceContent Source(byte[]? bytes = null)
    {
        bytes ??= Fixture();
        return new EvidenceContent(new EvidenceReference("jpeg-evidence", "jpeg-document", "jpeg-version", "customer-001",
            "reference://original-jpeg", Convert.ToHexStringLower(SHA256.HashData(bytes)), "image/jpeg"), "", bytes);
    }
    private static ConfigurationManager NativeConfiguration() => new()
    {
        ["Monergy:DocumentProcessing:OcrExecutable"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSERACT") ?? "/usr/bin/tesseract",
        ["Monergy:DocumentProcessing:OcrDataDirectory"] = Environment.GetEnvironmentVariable("MONERGY_TEST_TESSDATA") ?? "/usr/share/tesseract-ocr/5/tessdata",
    };

    [Theory, Trait("Category", "NativeOcr")]
    [InlineData("salary-slip.jpg")]
    [InlineData("salary-slip-alternate.jpg")]
    [InlineData("salary-slip-progressive.jpg")]
    [InlineData("salary-slip-grayscale.jpg")]
    public async Task RealPinnedOcrReadsSupportedJpegEncodingsWithExactOriginalSource(string name)
    {
        using var engine = new TesseractImageTextRecognizer(NativeConfiguration());
        var extractor = new StructuredDocumentExtractor(salarySlipJpeg: new SalarySlipJpegExtractor(engine));
        var source = Source(Fixture(name));
        var fact = Assert.Single(await extractor.ExtractAsync(source, "processing-jpeg", default));
        Assert.Equal(52480m, fact.CandidateValue); Assert.Equal("INR", fact.Currency);
        Assert.Equal(new DateOnly(2026, 9, 30), fact.EffectiveDate);
        Assert.Equal("INCOME", fact.FactType); Assert.Equal("Net pay", fact.Label);
        Assert.Equal(source.Reference.EvidenceId, fact.EvidenceId); Assert.Equal(source.Reference.CustomerId, fact.CustomerId);
        Assert.Equal(source.Reference.DocumentVersionId, fact.DocumentVersionId);
        Assert.InRange(fact.Confidence, .9m, 1m);
        Assert.StartsWith("Salary slip OCR: page 1, line ", fact.SourceLocation, StringComparison.Ordinal);
        Assert.Equal(SalarySlipJpegExtractor.ExtractionVersion, fact.ExtractionVersion);
        Assert.Equal(fact, Assert.Single(await extractor.ExtractAsync(source, "processing-jpeg", default)));
        var next = Assert.Single(await extractor.ExtractAsync(source with
            { Reference = source.Reference with { DocumentVersionId = "replacement-jpeg-version" } }, "processing-jpeg", default));
        Assert.NotEqual(fact.SourceFactId, next.SourceFactId);
    }

    [Theory, Trait("Category", "NativeOcr")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealOcrAcceptsExplicitUprightExifInEitherByteOrder(bool little)
    {
        using var engine = new TesseractImageTextRecognizer(NativeConfiguration());
        var fact = Assert.Single(await new SalarySlipJpegExtractor(engine).ExtractAsync(
            Source(WithExif(Fixture(), Exif(1, little))), "upright-exif", default));
        Assert.Equal(52480m, fact.CandidateValue);
    }

    [Fact, Trait("Category", "NativeOcr")]
    public async Task JpegInvoiceRemainsUnclassified()
    {
        using var engine = new TesseractImageTextRecognizer(NativeConfiguration());
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => new SalarySlipJpegExtractor(engine)
            .ExtractAsync(Source(Fixture("unclassified-image.jpg")), "invoice-jpeg", default));
        Assert.Equal(DocumentRecognitionFailure.Unclassified, failure.Reason);
    }

    [Fact, Trait("Category", "NativeOcr")]
    public async Task NativeDecoderWarningRejectsPrematureEntropyWithoutPartialCandidates()
    {
        var original = Fixture(); var damaged = original[..(original.Length / 2)].Concat(new byte[] { 0xff, 0xd9 }).ToArray();
        SalarySlipJpegExtractor.ValidateJpeg(damaged); // Header/marker bounds alone cannot prove complete compressed pixels.
        using var engine = new TesseractImageTextRecognizer(NativeConfiguration());
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() => new SalarySlipJpegExtractor(engine)
            .ExtractAsync(Source(damaged), "damaged-jpeg", default));
        Assert.Equal(DocumentRecognitionFailure.UnsupportedJpeg, failure.Reason);
    }

    [Theory]
    [InlineData("missing-end")]
    [InlineData("trailing-data")]
    [InlineData("truncated-segment")]
    [InlineData("missing-frame")]
    [InlineData("oversized-raster")]
    [InlineData("unsupported-frame")]
    [InlineData("unsupported-precision")]
    [InlineData("bad-scan")]
    [InlineData("duplicate-frame")]
    [InlineData("cmyk")]
    [InlineData("malformed-exif")]
    [InlineData("rotated-exif")]
    [InlineData("duplicate-exif")]
    [InlineData("bad-ifd-offset")]
    [InlineData("png-bytes")]
    public async Task InvalidHeadersEncodingsOrOrientationNeverReachNativeDecode(string kind)
    {
        var bytes = Fixture(); var frame = Marker(bytes, 0xc0); var scan = Marker(bytes, 0xda);
        switch (kind)
        {
            case "missing-end": bytes = bytes[..^2]; break;
            case "trailing-data": bytes = bytes.Concat(new byte[] { 0 }).ToArray(); break;
            case "truncated-segment": bytes = bytes[..20]; break;
            case "missing-frame": bytes = RemoveSegment(bytes, frame); break;
            case "oversized-raster": BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(frame + 7, 2), 65535); break;
            case "unsupported-frame": bytes[frame + 1] = 0xc3; break;
            case "unsupported-precision": bytes[frame + 4] = 12; break;
            case "bad-scan": bytes[scan + 2] = 0xff; bytes[scan + 3] = 0xff; break;
            case "duplicate-frame":
                var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(frame + 2, 2)) + 2;
                bytes = bytes[..scan].Concat(bytes.AsSpan(frame, segmentLength).ToArray()).Concat(bytes[scan..]).ToArray(); break;
            case "cmyk": bytes = Fixture("salary-slip-cmyk.jpg"); break;
            case "malformed-exif": bytes = WithExif(bytes, "Exif\0\0garbage"u8.ToArray()); break;
            case "rotated-exif": bytes = WithExif(bytes, Exif(6, true)); break;
            case "duplicate-exif": bytes = WithExif(WithExif(bytes, Exif(1, true)), Exif(1, false)); break;
            case "bad-ifd-offset": var exif = Exif(1, true); exif[10] = 0xff; bytes = WithExif(bytes, exif); break;
            case "png-bytes": bytes = Fixture("salary-slip.png"); break;
        }
        var recognizer = new RecognizerDouble();
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() =>
            new SalarySlipJpegExtractor(recognizer).ExtractAsync(Source(bytes), "invalid-jpeg", default));
        Assert.Equal(DocumentRecognitionFailure.UnsupportedJpeg, failure.Reason); Assert.Equal(0, recognizer.Calls);
    }

    [Fact]
    public async Task SourceMismatchOversizeAndCancellationDoNotProduceFacts()
    {
        var recognizer = new RecognizerDouble(); var extractor = new SalarySlipJpegExtractor(recognizer); var source = Source();
        foreach (var bad in new[] { source with { Reference = source.Reference with { ContentSha256 = new string('0', 64) } },
            source with { Reference = source.Reference with { ContentType = "image/png" } }, source with { Content = "text" },
            source with { BinaryContent = null }, Source(new byte[1_048_577]) })
            await Assert.ThrowsAsync<DocumentRecognitionException>(() => extractor.ExtractAsync(bad, "mismatched", default));
        using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync(source, "cancelled", cancelled.Token));
        Assert.Equal(0, recognizer.Calls);
        await Assert.ThrowsAsync<OcrEngineUnavailableException>(() => new StructuredDocumentExtractor().ExtractAsync(source, "unconfigured", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JpegCandidatesRequireTheSameExplicitFieldsAndConfidence(bool lowConfidence)
    {
        var text = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
            (lowConfidence ? "5\t1\t1\t1\t1\t1\t0\t0\t100\t40\t89\tSALARY\n5\t1\t1\t1\t1\t2\t100\t0\t100\t40\t89\tSLIP\n" +
                "5\t1\t1\t1\t2\t1\t0\t50\t100\t40\t99\tPay Date: 2026-09-30\n5\t1\t1\t1\t3\t1\t0\t100\t100\t40\t99\tCurrency: INR\n" +
                "5\t1\t1\t1\t4\t1\t0\t150\t100\t40\t99\tNet Pay: 52480.00\n" :
                "5\t1\t1\t1\t1\t1\t0\t0\t100\t40\t99\tSALARY SLIP\n");
        var failure = await Assert.ThrowsAsync<DocumentRecognitionException>(() =>
            new SalarySlipJpegExtractor(new RecognizerDouble(text)).ExtractAsync(Source(), "strict-jpeg", default));
        Assert.Equal(lowConfidence ? DocumentRecognitionFailure.LowConfidence : DocumentRecognitionFailure.Incomplete, failure.Reason);
    }

    [Fact]
    public async Task UnsupportedJpegCommitsTypedFailureAndZeroFacts()
    {
        var source = Source(WithExif(Fixture(), Exif(6, true)));
        var repository = new InMemoryDocumentProcessingRepository(); var recognizer = new RecognizerDouble();
        var app = new DocumentProcessingApplication(repository, new SourceDouble(source),
            new StructuredDocumentExtractor(salarySlipJpeg: new SalarySlipJpegExtractor(recognizer)),
            new ReferenceExecutionPolicy(), new CapturingTelemetry(), TimeProvider.System);
        var result = await app.ProcessDocumentAsync(Vs02TestContext.Request(Vs02ContractNames.ProcessDocument,
            new ProcessDocument("processing-jpeg-failure", "jpeg-version", "jpeg-evidence", "customer-001", DateTimeOffset.UtcNow), "jpeg-failure", "jpeg-failure"));
        Assert.Equal("processing.jpeg.unsupported", result.Error?.Code); Assert.False(result.Error?.Retryable);
        var record = await repository.GetAsync("processing-jpeg-failure", default);
        Assert.Equal(ProcessingState.Failed, record?.State); Assert.Empty(record!.Facts); Assert.Equal(0, recognizer.Calls);
        Assert.IsType<DomainEvent<DocumentProcessingFailedPayload>>(Assert.Single(repository.DrainOutbox()));
    }

    private static int Marker(byte[] bytes, byte marker)
    {
        for (var index = 2; index < bytes.Length - 1; index++) if (bytes[index] == 0xff && bytes[index + 1] == marker) return index;
        throw new InvalidOperationException("The synthetic JPEG marker is absent.");
    }
    private static byte[] RemoveSegment(byte[] bytes, int at)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 2, 2)) + 2;
        return bytes[..at].Concat(bytes[(at + length)..]).ToArray();
    }
    private static byte[] WithExif(byte[] jpeg, byte[] payload)
    {
        var header = new byte[] { 0xff, 0xe1, 0, 0 };
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), checked((ushort)(payload.Length + 2)));
        return jpeg[..2].Concat(header).Concat(payload).Concat(jpeg[2..]).ToArray();
    }
    private static byte[] Exif(ushort orientation, bool little)
    {
        byte[] bytes = [69, 120, 105, 102, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        bytes[6] = bytes[7] = little ? (byte)'I' : (byte)'M';
        void Write16(int at, ushort value) { if (little) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at, 2), value); else BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at, 2), value); }
        void Write32(int at, uint value) { if (little) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at, 4), value); else BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at, 4), value); }
        Write16(8, 42); Write32(10, 8); Write16(14, 1); Write16(16, 0x0112); Write16(18, 3); Write32(20, 1); Write16(24, orientation);
        return bytes;
    }
    private sealed class RecognizerDouble(string text = "") : IImageTextRecognizer
    {
        public int Calls { get; private set; }
        public Task<string> RecognizeAsync(byte[] image, CancellationToken cancellationToken) { Calls++; return Task.FromResult(text); }
    }
    private sealed class SourceDouble(EvidenceContent content) : IEvidenceContentReader
    {
        public Task<EvidenceContent?> ReadAsync(string version, string customer, TrustedSecurityContext security, string correlation, CancellationToken cancellationToken) => Task.FromResult<EvidenceContent?>(content);
    }
}
