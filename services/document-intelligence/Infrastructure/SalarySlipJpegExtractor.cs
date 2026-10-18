using System.Buffers.Binary;
using System.Security.Cryptography;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

public sealed class SalarySlipJpegExtractor(IImageTextRecognizer recognizer) : IDocumentExtractor
{
    public const string ExtractionVersion = "salary-slip-jpeg-ocr/1.0.0";

    public async Task<IReadOnlyList<ValidatedSourceFact>> ExtractAsync(
        EvidenceContent content, string processingId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = content.BinaryContent;
        if (content.Reference.ContentType != "image/jpeg" || content.Content.Length != 0 || bytes is null ||
            !string.Equals(content.Reference.ContentSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
            throw Unsupported();
        ValidateJpeg(bytes);
        var tsv = await recognizer.RecognizeAsync(bytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return SalarySlipPngExtractor.CreateCandidates(content, processingId, tsv, ExtractionVersion);
    }

    // This preflight bounds native decoding and supports a deliberate encoding subset; native decoding remains authoritative.
    public static void ValidateJpeg(byte[] bytes)
    {
        if (bytes.Length is < 20 or > 1_048_576 || bytes[0] != 0xff || bytes[1] != 0xd8) throw Unsupported();
        var at = 2;
        var frames = 0;
        var scans = 0;
        var segments = 0;
        var exifSeen = false;
        var components = new HashSet<byte>();
        while (at < bytes.Length)
        {
            if (++segments > 1024 || bytes[at++] != 0xff) throw Unsupported();
            while (at < bytes.Length && bytes[at] == 0xff) at++;
            if (at >= bytes.Length) throw Unsupported();
            var marker = bytes[at++];
            if (marker == 0xd9)
            {
                if (at != bytes.Length || frames != 1 || scans == 0) throw Unsupported();
                return;
            }
            if (!(marker is 0xc0 or 0xc2 or 0xc4 or 0xda or 0xdb or 0xdd or 0xfe || marker is >= 0xe0 and <= 0xef) || at + 2 > bytes.Length)
                throw Unsupported();
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at, 2));
            if (length < 2 || at + length > bytes.Length) throw Unsupported();
            var payload = bytes.AsSpan(at + 2, length - 2);
            at += length;
            if (marker is 0xc0 or 0xc2)
            {
                if (++frames != 1 || scans != 0 || payload.Length < 6 || payload[0] != 8) throw Unsupported();
                var height = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(1, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(3, 2));
                var count = payload[5];
                if (width is < 100 or > 4096 || height is < 100 or > 4096 || (long)width * height > 10_000_000 ||
                    count is not (1 or 3) || payload.Length != 6 + 3 * count) throw Unsupported();
                for (var index = 0; index < count; index++)
                {
                    var entry = payload.Slice(6 + 3 * index, 3);
                    if (!components.Add(entry[0]) || (entry[1] >> 4) is < 1 or > 4 ||
                        (entry[1] & 15) is < 1 or > 4 || entry[2] > 3) throw Unsupported();
                }
            }
            else if (marker == 0xe1 && payload.StartsWith("Exif\0\0"u8))
            {
                if (exifSeen) throw Unsupported();
                exifSeen = true;
                ValidateExif(payload[6..]);
            }
            else if (marker == 0xdd && payload.Length != 2) throw Unsupported();
            else if (marker == 0xda)
            {
                if (frames != 1 || ++scans > 64 || payload.Length < 6 || payload[0] is < 1 or > 3 ||
                    payload.Length != 4 + 2 * payload[0]) throw Unsupported();
                var scanComponents = new HashSet<byte>();
                for (var index = 0; index < payload[0]; index++)
                    if (!components.Contains(payload[1 + 2 * index]) || !scanComponents.Add(payload[1 + 2 * index])) throw Unsupported();
                var data = 0;
                while (at < bytes.Length)
                {
                    if (bytes[at] != 0xff) { data++; at++; continue; }
                    var next = at + 1;
                    while (next < bytes.Length && bytes[next] == 0xff) next++;
                    if (next >= bytes.Length) throw Unsupported();
                    if (bytes[next] == 0 || bytes[next] is >= 0xd0 and <= 0xd7)
                    { data++; at = next + 1; continue; }
                    break;
                }
                if (data == 0) throw Unsupported();
            }
        }
        throw Unsupported();
    }

    private static void ValidateExif(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) throw Unsupported();
        var little = tiff.StartsWith("II"u8);
        if (!little && !tiff.StartsWith("MM"u8) || Read16(tiff, 2, little) != 42) throw Unsupported();
        var offset = Read32(tiff, 4, little);
        if (offset < 8 || offset > tiff.Length - 2) throw Unsupported();
        var start = (int)offset;
        var count = Read16(tiff, start, little);
        if (count > 128 || start + 2 + 12 * count + 4 > tiff.Length) throw Unsupported();
        var orientationSeen = false;
        for (var index = 0; index < count; index++)
        {
            var entry = start + 2 + 12 * index;
            if (Read16(tiff, entry, little) != 0x0112) continue;
            if (orientationSeen || Read16(tiff, entry + 2, little) != 3 || Read32(tiff, entry + 4, little) != 1 ||
                Read16(tiff, entry + 8, little) != 1) throw Unsupported();
            orientationSeen = true;
        }
    }
    private static ushort Read16(ReadOnlySpan<byte> bytes, int at, bool little) => little
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(at, 2)) : BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(at, 2));
    private static uint Read32(ReadOnlySpan<byte> bytes, int at, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at, 4)) : BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(at, 4));
    internal static DocumentRecognitionException Unsupported() => new(DocumentRecognitionFailure.UnsupportedJpeg);
}
