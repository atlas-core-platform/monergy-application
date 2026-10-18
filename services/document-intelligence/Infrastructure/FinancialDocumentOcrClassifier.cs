using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

// Classification and field validation share the exact bounded native TSV parser across all three format adapters.
internal static class FinancialDocumentOcrClassifier
{
    private static readonly Regex SalaryTitle = Pattern(@"^(SALARY\s+SLIP|PAY\s*SLIP)$");
    private static readonly Regex LoanTitle = Pattern(@"^LOAN(?:\s+ACCOUNT)?\s+STATEMENT$");
    private static readonly Regex PayDate = Pattern(@"^(Pay\s+Date|Payment\s+Date)\s*:");
    private static readonly Regex StatementDate = Pattern(@"^(Statement\s+Date|As\s+of\s+Date|Balance\s+As\s+of)\s*:");
    private static readonly Regex NetPay = Pattern(@"^(Net\s+Pay|Net\s+Salary|Take\s+Home\s+Pay)\s*:");
    private static readonly Regex Outstanding = Pattern(@"^(Outstanding(?:\s+Loan)?\s+Balance|Principal\s+Outstanding)\s*:");
    private static readonly Regex CurrencyLabel = Pattern(@"^Currency\s*:");
    private static readonly Regex Money = Pattern(@"^(?:(?<currencyBefore>[A-Z]{3})\s+)?(?<amount>(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)(?:\.[0-9]{1,2})?)(?:\s+(?<currencyAfter>[A-Z]{3}))?$");
    private static readonly HashSet<string> Currencies = new(StringComparer.Ordinal)
    { "INR", "USD", "EUR", "GBP", "CAD", "AUD", "SGD", "AED" };
    private static Regex Pattern(string value) => new(value,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    internal static IReadOnlyList<ValidatedSourceFact> CreateCandidates(
        EvidenceContent content, string processingId, string tsv, string salaryExtractionVersion)
    {
        var lines = ParseTsv(tsv);
        // A second known title anywhere in the page is ambiguous, including mixed salary/loan pages.
        var titles = lines.Where(line => SalaryTitle.IsMatch(line.Text) || LoanTitle.IsMatch(line.Text)).ToArray();
        if (titles.Length != 1 || titles[0].Number > 10)
            throw new DocumentRecognitionException(DocumentRecognitionFailure.Unclassified);
        var loan = LoanTitle.IsMatch(titles[0].Text);
        var failure = loan ? DocumentRecognitionFailure.IncompleteLoan : DocumentRecognitionFailure.Incomplete;
        var date = One(lines, loan ? StatementDate : PayDate, failure);
        var value = One(lines, loan ? Outstanding : NetPay, failure);
        var currencyLines = lines.Where(line => CurrencyLabel.IsMatch(line.Text)).ToArray();
        if (currencyLines.Length > 1) throw new DocumentRecognitionException(failure);
        var selected = new[] { titles[0], date, value }.Concat(currencyLines).ToArray();
        if (selected.Any(line => line.Confidence < 90m))
            throw new DocumentRecognitionException(DocumentRecognitionFailure.LowConfidence);
        if (!DateOnly.TryParseExact(AfterLabel(date.Text), ["yyyy-MM-dd", "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd MMMM yyyy"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var effectiveDate)) throw new DocumentRecognitionException(failure);
        var match = Money.Match(AfterLabel(value.Text));
        if (!match.Success || match.Groups["amount"].Value.Length > 20 || (match.Groups["currencyBefore"].Success && match.Groups["currencyAfter"].Success) ||
            !decimal.TryParse(match.Groups["amount"].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount) || amount < 0) throw new DocumentRecognitionException(failure);
        var inlineCurrency = (match.Groups["currencyBefore"].Success ? match.Groups["currencyBefore"].Value : match.Groups["currencyAfter"].Value).ToUpperInvariant();
        var currency = currencyLines.Length == 1 ? AfterLabel(currencyLines[0].Text).ToUpperInvariant() : inlineCurrency;
        if (!Currencies.Contains(currency) || inlineCurrency.Length > 0 && inlineCurrency != currency)
            throw new DocumentRecognitionException(failure);
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{content.Reference.DocumentVersionId}|{processingId}|{(loan ? "loan-statement:outstanding" : "salary-slip:net-pay")}")));
        var extractionVersion = loan ? salaryExtractionVersion switch
        {
            SalarySlipPngExtractor.ExtractionVersion => "loan-statement-png-ocr/1.0.0",
            SalarySlipJpegExtractor.ExtractionVersion => "loan-statement-jpeg-ocr/1.0.0",
            SalarySlipPdfExtractor.ExtractionVersion => "loan-statement-pdf-ocr/1.0.0",
            _ => throw new DocumentRecognitionException(DocumentRecognitionFailure.Unclassified),
        } : salaryExtractionVersion;
        return [new ValidatedSourceFact("sf-" + identity[..24], content.Reference.CustomerId, loan ? "LOAN" : "INCOME",
            loan ? (value.Text.StartsWith("Principal", StringComparison.OrdinalIgnoreCase) ? "Principal outstanding stated on document" : "Outstanding loan balance stated on document") : "Net pay", amount, currency, effectiveDate,
            $"{(loan ? "Loan statement" : "Salary slip")} OCR: page 1, line {value.Number}", selected.Min(line => line.Confidence) / 100m,
            content.Reference.EvidenceId, content.Reference.DocumentVersionId, extractionVersion, FixtureDocumentExtractor.ValidationVersion)];
    }

    private sealed record OcrLine(int Number, string Text, decimal Confidence);
    private static OcrLine One(IReadOnlyList<OcrLine> lines, Regex label, DocumentRecognitionFailure failure)
    {
        var selected = lines.Where(line => label.IsMatch(line.Text)).ToArray();
        return selected.Length == 1 ? selected[0] : throw new DocumentRecognitionException(failure);
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
