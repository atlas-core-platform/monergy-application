using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using static Monergy.Services.DocumentIntelligence.Infrastructure.FinancialDocumentOcrClassifier;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

// Literal policy figures only: neither verified cover nor an asset value or annual expense.
internal static class InsurancePolicyOcrExtractor
{
    private static readonly Regex PolicyDate = Pattern(@"^Policy\s+Date\s*:");
    private static readonly Regex SumAssured = Pattern(@"^Sum\s+Assured\s*:");
    private static readonly Regex Premium = Pattern(@"^Premium(?:\s+Amount)?\s*:");
    private static readonly Regex CurrencyLabel = Pattern(@"^Currency\s*:");
    private static Regex Pattern(string value) => new(value,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    internal static IReadOnlyList<ValidatedSourceFact> CreateCandidates(EvidenceContent content, string processingId,
        IReadOnlyList<OcrLine> lines, OcrLine title, string salaryExtractionVersion)
    {
        const DocumentRecognitionFailure failure = DocumentRecognitionFailure.IncompleteInsurance;
        var date = One(lines, PolicyDate, failure);
        var assured = One(lines, SumAssured, failure);
        var premium = One(lines, Premium, failure);
        var currencyLines = lines.Where(line => CurrencyLabel.IsMatch(line.Text)).ToArray();
        if (currencyLines.Length > 1) throw new DocumentRecognitionException(failure);
        var selected = new[] { title, date, assured, premium }.Concat(currencyLines).ToArray();
        if (selected.Any(line => line.Confidence < 90m)) throw new DocumentRecognitionException(DocumentRecognitionFailure.LowConfidence);
        if (!DateOnly.TryParseExact(AfterLabel(date.Text), ["yyyy-MM-dd", "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd MMMM yyyy"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var policyDate)) throw new DocumentRecognitionException(failure);
        var currencyLabel = currencyLines.Length == 1 ? AfterLabel(currencyLines[0].Text) : null;
        var sum = ReadMoney(AfterLabel(assured.Text), currencyLabel, failure);
        var payment = ReadMoney(AfterLabel(premium.Text), currencyLabel, failure);
        if (sum.Currency != payment.Currency) throw new DocumentRecognitionException(failure);
        var version = salaryExtractionVersion switch
        {
            SalarySlipPngExtractor.ExtractionVersion => "insurance-policy-png-ocr/1.0.0",
            SalarySlipJpegExtractor.ExtractionVersion => "insurance-policy-jpeg-ocr/1.0.0",
            SalarySlipPdfExtractor.ExtractionVersion => "insurance-policy-pdf-ocr/1.0.0",
            _ => throw new DocumentRecognitionException(DocumentRecognitionFailure.Unclassified),
        };
        var confidence = selected.Min(line => line.Confidence) / 100m;
        ValidatedSourceFact Fact(string field, string label, decimal amount, OcrLine line)
        {
            var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{content.Reference.DocumentVersionId}|{processingId}|insurance-policy:{field}")));
            return new("sf-" + identity[..24], content.Reference.CustomerId, "INSURANCE", label, amount, sum.Currency, policyDate,
                $"Insurance policy OCR: page 1, line {line.Number}; Policy Date: {policyDate:yyyy-MM-dd}", confidence,
                content.Reference.EvidenceId, content.Reference.DocumentVersionId, version, FixtureDocumentExtractor.ValidationVersion);
        }
        // Validate both amounts before publishing either; one invalid field yields no candidates.
        return [Fact("sum-assured", "Sum assured stated on policy", sum.Amount, assured),
            Fact("premium", "Premium stated on policy (frequency not established)", payment.Amount, premium)];
    }
}
