using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using static Monergy.Services.DocumentIntelligence.Infrastructure.FinancialDocumentOcrClassifier;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

// A dated source purchase figure does not verify ownership, authenticity or current property value.
internal static class PropertySaleDeedOcrExtractor
{
    private static readonly Regex DeedDate = Pattern(@"^(?:Sale\s+Deed\s+Date|Deed\s+Date)\s*:");
    private static readonly Regex PurchaseValue = Pattern(@"^Purchase\s+Value\s*:");
    private static readonly Regex CurrencyLabel = Pattern(@"^Currency\s*:");
    private static Regex Pattern(string value) => new(value,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    internal static IReadOnlyList<ValidatedSourceFact> CreateCandidates(EvidenceContent content, string processingId,
        IReadOnlyList<OcrLine> lines, OcrLine title, string salaryExtractionVersion)
    {
        const DocumentRecognitionFailure failure = DocumentRecognitionFailure.IncompletePropertySaleDeed;
        var date = One(lines, DeedDate, failure);
        var value = One(lines, PurchaseValue, failure);
        var currencyLines = lines.Where(line => CurrencyLabel.IsMatch(line.Text)).ToArray();
        if (currencyLines.Length > 1) throw new DocumentRecognitionException(failure);
        var required = new[] { title, date, value }.Concat(currencyLines).ToArray();
        if (required.Any(line => line.Confidence < 90m)) throw new DocumentRecognitionException(DocumentRecognitionFailure.LowConfidence);
        if (!DateOnly.TryParseExact(AfterLabel(date.Text), ["yyyy-MM-dd", "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd MMMM yyyy"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var deedDate)) throw new DocumentRecognitionException(failure);
        var money = ReadMoney(AfterLabel(value.Text), currencyLines.Length == 1 ? AfterLabel(currencyLines[0].Text) : null, failure);
        var version = salaryExtractionVersion switch
        {
            SalarySlipPngExtractor.ExtractionVersion => "property-sale-deed-png-ocr/1.0.0",
            SalarySlipJpegExtractor.ExtractionVersion => "property-sale-deed-jpeg-ocr/1.0.0",
            SalarySlipPdfExtractor.ExtractionVersion => "property-sale-deed-pdf-ocr/1.0.0",
            _ => throw new DocumentRecognitionException(DocumentRecognitionFailure.Unclassified),
        };
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{content.Reference.DocumentVersionId}|{processingId}|property-sale-deed:purchase-value")));
        return [new("sf-" + identity[..24], content.Reference.CustomerId, "ASSET", "Purchase value stated on property sale deed",
            money.Amount, money.Currency, deedDate,
            FormattableString.Invariant($"Property sale deed OCR: page 1, line {value.Number}; Deed Date: {deedDate:yyyy-MM-dd}"),
            required.Min(line => line.Confidence) / 100m, content.Reference.EvidenceId, content.Reference.DocumentVersionId,
            version, FixtureDocumentExtractor.ValidationVersion)];
    }
}
