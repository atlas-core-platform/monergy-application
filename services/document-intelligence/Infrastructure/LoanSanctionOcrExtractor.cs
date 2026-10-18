using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using static Monergy.Services.DocumentIntelligence.Infrastructure.FinancialDocumentOcrClassifier;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

// Literal sanction terms only: no verified offer, disbursement, current debt or repayment status.
internal static class LoanSanctionOcrExtractor
{
    private static readonly Regex SanctionDate = Pattern(@"^Sanction\s+Date\s*:");
    private static readonly Regex SanctionedAmount = Pattern(@"^Sanction(?:ed)?\s+Amount\s*:");
    private static readonly Regex Emi = Pattern(@"^EMI(?:\s+Amount)?\s*:");
    private static readonly Regex CurrencyLabel = Pattern(@"^Currency\s*:");
    private static Regex Pattern(string value) => new(value,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    internal static IReadOnlyList<ValidatedSourceFact> CreateCandidates(EvidenceContent content, string processingId,
        IReadOnlyList<OcrLine> lines, OcrLine title, string salaryExtractionVersion)
    {
        const DocumentRecognitionFailure failure = DocumentRecognitionFailure.IncompleteSanction;
        var date = One(lines, SanctionDate, failure);
        var sanctioned = One(lines, SanctionedAmount, failure);
        var emi = One(lines, Emi, failure);
        var currencyLines = lines.Where(line => CurrencyLabel.IsMatch(line.Text)).ToArray();
        if (currencyLines.Length > 1) throw new DocumentRecognitionException(failure);
        var selected = new[] { title, date, sanctioned, emi }.Concat(currencyLines).ToArray();
        if (selected.Any(line => line.Confidence < 90m)) throw new DocumentRecognitionException(DocumentRecognitionFailure.LowConfidence);
        if (!DateOnly.TryParseExact(AfterLabel(date.Text), ["yyyy-MM-dd", "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd MMMM yyyy"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var sanctionDate)) throw new DocumentRecognitionException(failure);
        var currencyLabel = currencyLines.Length == 1 ? AfterLabel(currencyLines[0].Text) : null;
        var terms = ReadMoney(AfterLabel(sanctioned.Text), currencyLabel, failure);
        var instalment = ReadMoney(AfterLabel(emi.Text), currencyLabel, failure);
        if (terms.Currency != instalment.Currency) throw new DocumentRecognitionException(failure);
        var version = salaryExtractionVersion switch
        {
            SalarySlipPngExtractor.ExtractionVersion => "loan-sanction-png-ocr/1.0.0",
            SalarySlipJpegExtractor.ExtractionVersion => "loan-sanction-jpeg-ocr/1.0.0",
            SalarySlipPdfExtractor.ExtractionVersion => "loan-sanction-pdf-ocr/1.0.0",
            _ => throw new DocumentRecognitionException(DocumentRecognitionFailure.Unclassified),
        };
        var confidence = selected.Min(line => line.Confidence) / 100m;
        ValidatedSourceFact Fact(string field, string label, decimal amount, OcrLine line)
        {
            var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{content.Reference.DocumentVersionId}|{processingId}|loan-sanction:{field}")));
            return new("sf-" + identity[..24], content.Reference.CustomerId, "LOAN", label, amount, terms.Currency, sanctionDate,
                FormattableString.Invariant($"Loan sanction letter OCR: page 1, line {line.Number}; Sanction Date: {sanctionDate:yyyy-MM-dd}"), confidence,
                content.Reference.EvidenceId, content.Reference.DocumentVersionId, version, FixtureDocumentExtractor.ValidationVersion);
        }
        // Validate both amounts before publishing either; one invalid field yields no candidates.
        return [Fact("sanctioned-amount", "Sanctioned loan amount stated on letter", terms.Amount, sanctioned),
            Fact("emi", "EMI stated on sanction letter", instalment.Amount, emi)];
    }
}
