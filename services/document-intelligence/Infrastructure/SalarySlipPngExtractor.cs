using System.Buffers.Binary;
using System.Security.Cryptography;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

public interface IImageTextRecognizer
{
    Task<string> RecognizeAsync(byte[] image, CancellationToken cancellationToken);
}

// Deliberately bounded first scanned-document path. OCR text is transient, never a financial fact by itself.
public sealed class SalarySlipPngExtractor(IImageTextRecognizer recognizer) : IDocumentExtractor
{
    public const string ExtractionVersion = "salary-slip-png-ocr/1.0.0";
    public async Task<IReadOnlyList<ValidatedSourceFact>> ExtractAsync(
        EvidenceContent content, string processingId, CancellationToken cancellationToken)
    {
        var bytes = content.BinaryContent;
        if (content.Reference.ContentType != "image/png" || content.Content.Length != 0 || bytes is null ||
            !string.Equals(content.Reference.ContentSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
            throw Incomplete();
        ValidatePng(bytes);
        var tsv = content.RecognizedTsv ?? await recognizer.RecognizeAsync(bytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return CreateCandidates(content, processingId, tsv, ExtractionVersion);
    }

    internal static IReadOnlyList<ValidatedSourceFact> CreateCandidates(
        EvidenceContent content, string processingId, string tsv, string extractionVersion) =>
        FinancialDocumentOcrClassifier.CreateCandidates(content, processingId, tsv, extractionVersion);

    public static void ValidatePng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length is < 33 or > 1_048_576 || !bytes.AsSpan().StartsWith(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw Incomplete();
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is < 100 or > 4096 || height is < 100 or > 4096 || (ulong)width * height > 10_000_000) throw Incomplete();
    }

    private static DocumentRecognitionException Incomplete() => new(DocumentRecognitionFailure.Incomplete);
}
