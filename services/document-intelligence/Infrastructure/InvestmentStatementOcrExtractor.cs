using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using static Monergy.Services.DocumentIntelligence.Infrastructure.FinancialDocumentOcrClassifier;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

// Literal dated statement figures only: no live price, gain, return, verified position or asset valuation.
internal static class InvestmentStatementOcrExtractor
{
    private static readonly Regex StatementDate = Pattern(@"^Statement\s+Date\s*:");
    private static readonly Regex PurchaseValue = Pattern(@"^Purchase\s+Value\s*:");
    private static readonly Regex CurrentValue = Pattern(@"^Current\s+Value\s*:");
    private static readonly Regex CurrencyLabel = Pattern(@"^Currency\s*:");
    private static Regex Pattern(string value) => new(value,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    internal static IReadOnlyList<ValidatedSourceFact> CreateCandidates(EvidenceContent content, string processingId,
        IReadOnlyList<OcrLine> lines, OcrLine title, string salaryExtractionVersion)
    {
        const DocumentRecognitionFailure failure = DocumentRecognitionFailure.IncompleteInvestment;
        var date = One(lines, StatementDate, failure);
        var purchase = One(lines, PurchaseValue, failure);
        var current = One(lines, CurrentValue, failure);
        var currencyLines = lines.Where(line => CurrencyLabel.IsMatch(line.Text)).ToArray();
        if (currencyLines.Length > 1) throw new DocumentRecognitionException(failure);
        var selected = new[] { title, date, purchase, current }.Concat(currencyLines).ToArray();
        if (selected.Any(line => line.Confidence < 90m)) throw new DocumentRecognitionException(DocumentRecognitionFailure.LowConfidence);
        if (!DateOnly.TryParseExact(AfterLabel(date.Text), ["yyyy-MM-dd", "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd MMMM yyyy"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var statementDate)) throw new DocumentRecognitionException(failure);
        var currencyLabel = currencyLines.Length == 1 ? AfterLabel(currencyLines[0].Text) : null;
        var cost = ReadMoney(AfterLabel(purchase.Text), currencyLabel, failure);
        var value = ReadMoney(AfterLabel(current.Text), currencyLabel, failure);
        if (cost.Currency != value.Currency) throw new DocumentRecognitionException(failure);
        var version = salaryExtractionVersion switch
        {
            SalarySlipPngExtractor.ExtractionVersion => "investment-statement-png-ocr/1.0.0",
            SalarySlipJpegExtractor.ExtractionVersion => "investment-statement-jpeg-ocr/1.0.0",
            SalarySlipPdfExtractor.ExtractionVersion => "investment-statement-pdf-ocr/1.0.0",
            _ => throw new DocumentRecognitionException(DocumentRecognitionFailure.Unclassified),
        };
        var confidence = selected.Min(line => line.Confidence) / 100m;
        ValidatedSourceFact Fact(string field, string label, decimal amount, OcrLine line)
        {
            var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{content.Reference.DocumentVersionId}|{processingId}|investment-statement:{field}")));
            return new("sf-" + identity[..24], content.Reference.CustomerId, "INVESTMENT", label, amount, cost.Currency, statementDate,
                FormattableString.Invariant($"Investment statement OCR: page 1, line {line.Number}; Statement Date: {statementDate:yyyy-MM-dd}"), confidence,
                content.Reference.EvidenceId, content.Reference.DocumentVersionId, version, FixtureDocumentExtractor.ValidationVersion);
        }
        // Validate both amounts before publishing either; one invalid field yields no candidates.
        return [Fact("purchase-value", "Purchase value stated on statement", cost.Amount, purchase),
            Fact("current-value", "Current value stated on statement", value.Amount, current)];
    }
}
