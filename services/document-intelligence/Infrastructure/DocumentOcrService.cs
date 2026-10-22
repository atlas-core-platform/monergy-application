using Monergy.Services.DocumentIntelligence.Application;
namespace Monergy.Services.DocumentIntelligence.Infrastructure;

public interface IPdfPageSetRenderer : IPdfPageRenderer
{
    Task<IReadOnlyList<byte[]>> RenderPagesAsync(byte[] pdf, CancellationToken ct);
}
public sealed class DocumentOcrService(IDocumentOcrRepository repository, IImageTextRecognizer recognizer, IPdfPageRenderer renderer, TimeProvider clock)
{
    public async Task<(DocumentOcrResult Result, string? SinglePageTsv)> RecognizeAsync(string tenant, EvidenceContent content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = content.BinaryContent;
        if (content.Content.Length != 0 || bytes is null || !string.Equals(ClassificationRules.Hash(bytes), content.Reference.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new EvidenceDependencyException("OCR source identity is invalid.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var pages = new List<DocumentOcrPage>(); string? single = null; var unsupported = false;
        try
        {
            IReadOnlyList<byte[]> images;
            if (content.Reference.ContentType == "application/pdf") images = renderer is IPdfPageSetRenderer multi ? await multi.RenderPagesAsync(bytes, deadline.Token) : [await renderer.RenderAsync(bytes, deadline.Token)];
            else if (content.Reference.ContentType == "image/png") { SalarySlipPngExtractor.ValidatePng(bytes); images = [bytes]; }
            else if (content.Reference.ContentType == "image/jpeg") { SalarySlipJpegExtractor.ValidateJpeg(bytes); images = [bytes]; }
            else throw new DocumentRecognitionException(DocumentRecognitionFailure.Unclassified);
            if (images.Count is < 1 or > DocumentOcrRules.MaximumPages) throw new DocumentRecognitionException(DocumentRecognitionFailure.UnsupportedPdf);
            foreach (var image in images)
            {
                var tsv = await recognizer.RecognizeAsync(image, deadline.Token);
                var lines = FinancialDocumentOcrClassifier.ParseTsv(tsv);
                pages.Add(new(pages.Count + 1, lines.Select(x => new DocumentOcrLine(x.Number, x.Text, x.Confidence)).ToArray()));
                if (images.Count == 1) single = tsv;
            }
        }
        catch (DocumentRecognitionException) { pages.Clear(); single = null; unsupported = true; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new OcrEngineUnavailableException(); }
        var result = new DocumentOcrResult(tenant, content.Reference.CustomerId, content.Reference.DocumentVersionId,
            content.Reference.ContentSha256.ToLowerInvariant(), DocumentOcrRules.Version, unsupported ? "UNSUPPORTED" : DocumentOcrRules.State(pages), clock.GetUtcNow(), pages);
        if (!DocumentOcrRules.Valid(result)) { result = result with { State = "UNSUPPORTED", Pages = [] }; single = null; }
        try { await repository.SaveAsync(result, ct); }
        catch (Exception e) when (e is Npgsql.NpgsqlException or InvalidOperationException or Monergy.Platform.TenantBoundaryException)
        { throw new EvidenceDependencyException("OCR storage unavailable."); }
        return (result, single);
    }
}
public sealed class InMemoryDocumentOcrRepository : IDocumentOcrRepository
{
    private readonly object sync = new();
    private readonly Dictionary<(string Tenant, string Customer, string Version), DocumentOcrResult> rows = [];
    public Task SaveAsync(DocumentOcrResult result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!DocumentOcrRules.Valid(result)) throw new EvidenceDependencyException("Invalid OCR record.");
        lock (sync)
        {
            var key = (result.TenantId, result.CustomerId, result.DocumentVersionId);
            if (rows.TryGetValue(key, out var existing) && existing.SourceHash != result.SourceHash) throw new EvidenceDependencyException("OCR original changed.");
            rows[key] = result with { Pages = Array.AsReadOnly(result.Pages.Select(x => x with { Lines = Array.AsReadOnly(x.Lines.ToArray()) }).ToArray()) };
        }
        return Task.CompletedTask;
    }
    public Task<DocumentOcrResult?> ReadOcrAsync(string tenant, string customer, string version, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (sync) return Task.FromResult(rows.GetValueOrDefault((tenant, customer, version))); }
}
