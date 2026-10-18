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
    private readonly XlsxFinancialDocumentExtractor xlsx = new();

    public Task<IReadOnlyList<ValidatedSourceFact>> ExtractAsync(
        EvidenceContent content, string processingId, CancellationToken cancellationToken) =>
        content.Reference.ContentType.ToLowerInvariant() switch
        {
            "text/plain" => text.ExtractAsync(content, processingId, cancellationToken),
            "text/csv" => csv.ExtractAsync(content, processingId, cancellationToken),
            XlsxFinancialDocumentExtractor.ContentType => xlsx.ExtractAsync(content, processingId, cancellationToken),
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
        var rows = new List<string[]>();
        try
        {
            var header = parser.ReadFields() ?? throw InvalidCsv();
            while (!parser.EndOfData)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rows.Count >= 100) throw InvalidCsv();
                rows.Add(parser.ReadFields() ?? throw InvalidCsv());
            }
            return Task.FromResult(ValidateTable(content.Reference, processingId, header, rows, "CSV", "csv", ExtractionVersion, cancellationToken));
        }
        catch (MalformedLineException) { throw InvalidCsv(); }
    }

    internal static IReadOnlyList<ValidatedSourceFact> ValidateTable(EvidenceReference reference, string processingId,
        string[] suppliedHeader, IReadOnlyList<string[]> rows, string location, string identityKind, string extractionVersion, CancellationToken ct)
    {
        var header = suppliedHeader.Select(field => field.Trim()).ToArray();
        if (header.Length != Headers.Length || header.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Headers.Length ||
            Headers.Any(name => !header.Contains(name, StringComparer.OrdinalIgnoreCase)) || rows.Count is < 1 or > 100) throw InvalidCsv();
        var columns = Headers.Select(name => Array.FindIndex(header, field => field.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        var facts = new List<ValidatedSourceFact>(); var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (row.Length != Headers.Length) throw InvalidCsv();
            var category = row[columns[0]].Trim().ToUpperInvariant(); var description = row[columns[1]].Trim();
            var amountText = row[columns[2]].Trim(); var currency = row[columns[3]].Trim().ToUpperInvariant(); var source = row[columns[5]].Trim();
            if (!Categories.Contains(category) || description.Length is < 1 or > 160 || source.Length is < 1 or > 140 ||
                currency.Length != 3 || currency.Any(c => c is < 'A' or > 'Z') || amountText.Length > 28 ||
                !decimal.TryParse(amountText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount < 0 ||
                !DateOnly.TryParseExact(row[columns[4]].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                !identities.Add(category + "\u001f" + description)) throw InvalidCsv();
            var record = facts.Count + 2;
            var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{reference.DocumentVersionId}|{processingId}|{identityKind}:{record}")));
            facts.Add(new ValidatedSourceFact("sf-" + identity[..24], reference.CustomerId, category, description, amount, currency, date,
                $"{location} {(identityKind == "xlsx" ? "row" : "record")} {record}: {source}", 1m, reference.EvidenceId, reference.DocumentVersionId, extractionVersion, FixtureDocumentExtractor.ValidationVersion));
        }
        // No caller sees a valid prefix from a later-invalid table.
        return facts;
    }

    private static DocumentExtractionException InvalidCsv() =>
        new("The CSV must match the financial information template and contain 1–100 valid records.");
}
