using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

public sealed class StructuredDocumentExtractor(SalarySlipPngExtractor? salarySlip = null, SalarySlipPdfExtractor? salarySlipPdf = null, SalarySlipJpegExtractor? salarySlipJpeg = null) : IDocumentExtractor
{
    private readonly FixtureDocumentExtractor text = new();
    private readonly CsvFinancialDocumentExtractor csv = new();

    public Task<IReadOnlyList<ValidatedSourceFact>> ExtractAsync(
        EvidenceContent content, string processingId, CancellationToken cancellationToken) =>
        content.Reference.ContentType.ToLowerInvariant() switch
        {
            "text/plain" => text.ExtractAsync(content, processingId, cancellationToken),
            "text/csv" => csv.ExtractAsync(content, processingId, cancellationToken),
            "application/pdf" => salarySlipPdf is null ? throw new OcrEngineUnavailableException() :
                salarySlipPdf.ExtractAsync(content, processingId, cancellationToken),
            "image/jpeg" => salarySlipJpeg is null ? throw new OcrEngineUnavailableException() :
                salarySlipJpeg.ExtractAsync(content, processingId, cancellationToken),
            "image/png" => salarySlip is null ? throw new OcrEngineUnavailableException() :
                salarySlip.ExtractAsync(content, processingId, cancellationToken),
            _ => throw new DocumentExtractionException("The document format is unsupported."),
        };
}

public sealed class CsvFinancialDocumentExtractor : IDocumentExtractor
{
    public const string ExtractionVersion = "financial-csv/1.0.0";
    private static readonly string[] Headers = ["Category", "Description", "Amount", "Currency", "Date", "Source"];
    private static readonly HashSet<string> Categories = new(StringComparer.OrdinalIgnoreCase)
    { "INCOME", "EXPENSE", "LOAN", "ASSET", "INVESTMENT", "INSURANCE", "TAX" };

    public Task<IReadOnlyList<ValidatedSourceFact>> ExtractAsync(
        EvidenceContent content, string processingId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!content.Reference.ContentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase) ||
            Encoding.UTF8.GetByteCount(content.Content) is < 1 or > 1_048_576)
            throw InvalidCsv();
        using var reader = new StringReader(content.Content.TrimStart('\uFEFF'));
        using var parser = new TextFieldParser(reader)
        { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var facts = new List<ValidatedSourceFact>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var header = parser.ReadFields()?.Select(field => field.Trim()).ToArray();
            if (header is null || header.Length != Headers.Length ||
                header.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Headers.Length ||
                Headers.Any(name => !header.Contains(name, StringComparer.OrdinalIgnoreCase)))
                throw InvalidCsv();
            var columns = Headers.Select(name => Array.FindIndex(header,
                field => field.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
            while (!parser.EndOfData)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = parser.ReadFields();
                if (row is null || row.Length != Headers.Length || facts.Count >= 100) throw InvalidCsv();
                var category = row[columns[0]].Trim().ToUpperInvariant();
                var description = row[columns[1]].Trim();
                var amountText = row[columns[2]].Trim();
                var currency = row[columns[3]].Trim().ToUpperInvariant();
                var source = row[columns[5]].Trim();
                if (!Categories.Contains(category) || description.Length is < 1 or > 160 ||
                    source.Length is < 1 or > 140 || currency.Length != 3 ||
                    currency.Any(character => character is < 'A' or > 'Z') ||
                    amountText.Length > 28 ||
                    !decimal.TryParse(amountText, NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var amount) || amount < 0 ||
                    !DateOnly.TryParseExact(row[columns[4]].Trim(), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                    !identities.Add(category + "\u001f" + description))
                    throw InvalidCsv();
                var record = facts.Count + 2;
                var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{content.Reference.DocumentVersionId}|{processingId}|csv:{record}")));
                facts.Add(new ValidatedSourceFact(
                    "sf-" + identity[..24], content.Reference.CustomerId, category, description, amount,
                    currency, date, $"CSV record {record}: {source}", 1m,
                    content.Reference.EvidenceId, content.Reference.DocumentVersionId,
                    ExtractionVersion, FixtureDocumentExtractor.ValidationVersion));
            }
        }
        catch (MalformedLineException) { throw InvalidCsv(); }
        if (facts.Count == 0) throw InvalidCsv();
        // Publish only after every record validates; candidates still require explicit customer review.
        return Task.FromResult<IReadOnlyList<ValidatedSourceFact>>(facts);
    }

    private static DocumentExtractionException InvalidCsv() =>
        new("The CSV must match the financial information template and contain 1–100 valid records.");
}
