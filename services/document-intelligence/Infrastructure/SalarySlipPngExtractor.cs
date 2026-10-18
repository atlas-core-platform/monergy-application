using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

public interface IImageTextRecognizer
{
    Task<string> RecognizeAsync(byte[] png, CancellationToken cancellationToken);
}

// Deliberately bounded first scanned-document path. OCR text is transient, never a financial fact by itself.
public sealed class SalarySlipPngExtractor(IImageTextRecognizer recognizer) : IDocumentExtractor
{
    public const string ExtractionVersion = "salary-slip-png-ocr/1.0.0";
    private static readonly Regex Title = Pattern(@"^(SALARY\s+SLIP|PAY\s*SLIP)$");
    private static readonly Regex DateLabel = Pattern(@"^(Pay\s+Date|Payment\s+Date)\s*:");
    private static readonly Regex NetLabel = Pattern(@"^(Net\s+Pay|Net\s+Salary|Take\s+Home\s+Pay)\s*:");
    private static readonly Regex CurrencyLabel = Pattern(@"^Currency\s*:");
    private static readonly Regex Money = Pattern(@"^(?:(?<currency>[A-Z]{3})\s+)?(?<amount>(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)(?:\.[0-9]{1,2})?)(?:\s+(?<currency>[A-Z]{3}))?$");
    private static readonly HashSet<string> Currencies = new(StringComparer.Ordinal)
    { "INR", "USD", "EUR", "GBP", "CAD", "AUD", "SGD", "AED" };
    private static Regex Pattern(string value) => new(value,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    public async Task<IReadOnlyList<ValidatedSourceFact>> ExtractAsync(
        EvidenceContent content, string processingId, CancellationToken cancellationToken)
    {
        var bytes = content.BinaryContent;
        if (content.Reference.ContentType != "image/png" || content.Content.Length != 0 || bytes is null ||
            !string.Equals(content.Reference.ContentSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
            throw Incomplete();
        ValidatePng(bytes);
        var lines = ParseTsv(await recognizer.RecognizeAsync(bytes, cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        var titles = lines.Take(10).Where(line => Title.IsMatch(line.Text)).ToArray();
        if (titles.Length != 1) throw new DocumentRecognitionException(DocumentRecognitionFailure.Unclassified);
        var date = One(lines, DateLabel);
        var net = One(lines, NetLabel);
        var currencyLines = lines.Where(line => CurrencyLabel.IsMatch(line.Text)).ToArray();
        if (currencyLines.Length > 1) throw Incomplete();
        var selected = new[] { titles[0], date, net }.Concat(currencyLines).ToArray();
        if (selected.Any(line => line.Confidence < 90m))
            throw new DocumentRecognitionException(DocumentRecognitionFailure.LowConfidence);
        var dateText = AfterLabel(date.Text);
        if (!DateOnly.TryParseExact(dateText, ["yyyy-MM-dd", "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd MMMM yyyy"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var payDate)) throw Incomplete();
        var match = Money.Match(AfterLabel(net.Text));
        if (!match.Success || match.Groups["amount"].Value.Length > 20 || match.Groups["currency"].Captures.Count > 1 ||
            !decimal.TryParse(match.Groups["amount"].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount) || amount < 0) throw Incomplete();
        var inlineCurrency = match.Groups["currency"].Value.ToUpperInvariant();
        var currency = currencyLines.Length == 1 ? AfterLabel(currencyLines[0].Text).ToUpperInvariant() : inlineCurrency;
        if (!Currencies.Contains(currency) || inlineCurrency.Length > 0 && inlineCurrency != currency) throw Incomplete();
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{content.Reference.DocumentVersionId}|{processingId}|salary-slip:net-pay")));
        return [new ValidatedSourceFact("sf-" + identity[..24], content.Reference.CustomerId, "INCOME", "Net pay",
            amount, currency, payDate, $"Salary slip OCR: page 1, line {net.Number}", selected.Min(line => line.Confidence) / 100m,
            content.Reference.EvidenceId, content.Reference.DocumentVersionId, ExtractionVersion, FixtureDocumentExtractor.ValidationVersion)];
    }

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

    private sealed record OcrLine(int Number, string Text, decimal Confidence);
    private static OcrLine One(IReadOnlyList<OcrLine> lines, Regex label)
    {
        var selected = lines.Where(line => label.IsMatch(line.Text)).ToArray();
        return selected.Length == 1 ? selected[0] : throw Incomplete();
    }
    private static string AfterLabel(string value) => value[(value.IndexOf(':') + 1)..].Trim();
    private static DocumentRecognitionException Incomplete() => new(DocumentRecognitionFailure.Incomplete);

    private static OcrLine[] ParseTsv(string tsv)
    {
        if (tsv.Length > 1_048_576) throw Incomplete();
        using var reader = new StringReader(tsv);
        if (reader.ReadLine() != "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext") throw Incomplete();
        var words = new List<(string Key, int Word, string Text, decimal Confidence)>();
        string? row;
        while ((row = reader.ReadLine()) is not null)
        {
            if (row.Length == 0) continue;
            var cells = row.Split('\t');
            if (cells.Length != 12 || !int.TryParse(cells[0], out var level) || level is < 1 or > 5) throw Incomplete();
            if (level != 5) continue;
            var coordinates = cells.Skip(1).Take(9).Select(cell => int.TryParse(cell, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : -1).ToArray();
            if (coordinates[0] != 1 || coordinates.Any(value => value < 0 || value > 100_000) || coordinates[4] < 1 ||
                !decimal.TryParse(cells[10], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var confidence) ||
                confidence is < 0 or > 100 || cells[11].Length > 300 || cells[11].Any(char.IsControl) || words.Count >= 10_000)
                throw Incomplete();
            if (cells[11].Length != 0) words.Add((string.Join(':', cells.Skip(1).Take(4)), coordinates[4], cells[11], confidence));
        }
        var groups = words.GroupBy(word => word.Key).ToArray();
        if (groups.Length > 1000) throw Incomplete();
        return groups.Select((group, index) =>
        {
            if (group.Select(word => word.Word).Distinct().Count() != group.Count()) throw Incomplete();
            return new OcrLine(index + 1, string.Join(' ', group.OrderBy(word => word.Word).Select(word => word.Text)),
                group.Min(word => word.Confidence));
        }).ToArray();
    }
}
