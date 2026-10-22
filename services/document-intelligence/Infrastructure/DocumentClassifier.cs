using System.Text;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

// Classification is derived source metadata. It never creates or approves financial facts.
public sealed class DocumentClassifier(IDocumentClassificationRepository repository, IImageTextRecognizer recognizer, IPdfPageRenderer renderer, TimeProvider clock, DocumentOcrService? ocr = null)
{
    private readonly DocumentOcrService ocrReader = ocr ?? new(new InMemoryDocumentOcrRepository(), recognizer, renderer, clock);
    public async Task<EvidenceContent> ClassifyAsync(string tenant, EvidenceContent content, CancellationToken ct)
    {
        var reference = content.Reference;
        var bytes = content.BinaryContent ?? Encoding.UTF8.GetBytes(content.Content);
        if (!TenantAccessClient.Identifier(tenant, 64) || !string.Equals(ClassificationRules.Hash(bytes), reference.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new EvidenceDependencyException("Classification source identity is invalid.");
        (string Class, string Reason) result;
        try
        {
            IReadOnlyList<(string Text, decimal Confidence)> lines;
            if (reference.ContentType is "text/plain" or "text/csv")
                lines = content.Content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => (x, 100m)).ToArray();
            else if (reference.ContentType is "image/png" or "image/jpeg" or "application/pdf" && content.BinaryContent is not null)
            {
                var recognized = await ocrReader.RecognizeAsync(tenant, content, ct);
                lines = recognized.Result.Pages.SelectMany(x => x.Lines).Select(x => (x.Text, x.Confidence)).ToArray();
                content = content with { RecognizedTsv = recognized.SinglePageTsv };
                if (recognized.Result.State == "UNSUPPORTED") throw new DocumentRecognitionException(DocumentRecognitionFailure.Unclassified);
            }
            else lines = [];
            result = ClassificationRules.Classify(lines);
        }
        catch (DocumentRecognitionException) { result = ("UNCLASSIFIED", "UNSUPPORTED_LAYOUT"); }
        // Dependency outages remain retryable processing failures, not permanent unknown classifications.
        try
        {
            await repository.RecordAsync(new(tenant, reference.CustomerId, reference.DocumentVersionId, reference.ContentSha256.ToLowerInvariant(),
                ClassificationRules.Version, result.Class, result.Reason, clock.GetUtcNow(), 1, []), ct);
        }
        catch (Exception e) when (e is Npgsql.NpgsqlException or InvalidOperationException or TenantBoundaryException)
        { throw new EvidenceDependencyException("Classification storage unavailable."); }
        return content;
    }
}
