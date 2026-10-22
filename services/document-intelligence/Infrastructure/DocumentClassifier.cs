using System.Text;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

// Classification is derived source metadata. It never creates or approves financial facts.
public sealed class DocumentClassifier(IDocumentClassificationRepository repository, IImageTextRecognizer recognizer, IPdfPageRenderer renderer, TimeProvider clock)
{
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
                if (reference.ContentType == "image/png") SalarySlipPngExtractor.ValidatePng(bytes);
                else if (reference.ContentType == "image/jpeg") SalarySlipJpegExtractor.ValidateJpeg(bytes);
                else bytes = await renderer.RenderAsync(bytes, ct);
                var tsv = await recognizer.RecognizeAsync(bytes, ct);
                lines = FinancialDocumentOcrClassifier.ParseTsv(tsv).Select(x => (x.Text, x.Confidence)).ToArray();
                content = content with { RecognizedTsv = tsv };
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
