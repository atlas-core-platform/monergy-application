namespace Monergy.Services.DocumentIntelligence.Application;

public sealed record DocumentOcrLine(int Number, string Text, decimal Confidence);
public sealed record DocumentOcrPage(int Number, IReadOnlyList<DocumentOcrLine> Lines);
public sealed record DocumentOcrResult(string TenantId, string CustomerId, string DocumentVersionId, string SourceHash,
    string EngineVersion, string State, DateTimeOffset RecordedAt, IReadOnlyList<DocumentOcrPage> Pages);
public interface IDocumentOcrRepository
{
    Task SaveAsync(DocumentOcrResult result, CancellationToken ct);
    Task<DocumentOcrResult?> ReadOcrAsync(string tenant, string customer, string version, CancellationToken ct);
}
public static class DocumentOcrRules
{
    public const string Version = "document-ocr/1; tesseract-5.3.4-eng; poppler-24.02.0";
    public const int MaximumPages = 20;
    public static bool Valid(DocumentOcrResult row) => row.EngineVersion == Version && row.SourceHash is { Length: 64 } &&
        row.SourceHash.All(char.IsAsciiHexDigitLower) && row.RecordedAt >= DateTimeOffset.UnixEpoch &&
        row.State is "AVAILABLE" or "UNRELIABLE_TEXT" or "NO_TEXT" or "UNSUPPORTED" &&
        row.Pages is not null && row.Pages.Count <= MaximumPages && row.Pages.Select((page, index) => page is not null && page.Number == index + 1 && page.Lines is not null && page.Lines.Count <= 1000 &&
            page.Lines.Select((line, number) => line is not null && line.Number == number + 1 && line.Confidence is >= 0 and <= 100 &&
                line.Text is { Length: > 0 and <= 4000 } && !line.Text.Any(char.IsControl)).All(x => x)).All(x => x) &&
        row.Pages.SelectMany(x => x.Lines).Sum(x => x.Text.Length) <= 200_000 &&
        (row.State == "UNSUPPORTED" ? row.Pages.Count == 0 : row.Pages.Count > 0 && row.State == State(row.Pages));
    public static string State(IReadOnlyList<DocumentOcrPage> pages) => pages.All(x => x.Lines.Count == 0) ? "NO_TEXT" :
        pages.Any(x => x.Lines.Count == 0 || x.Lines.Any(line => line.Confidence < 90)) ? "UNRELIABLE_TEXT" : "AVAILABLE";
}
