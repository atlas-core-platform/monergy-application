using System.Globalization;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
namespace Monergy.Services.DocumentIntelligence;

// Document Intelligence owns bounded text/OCR interpretation. Passages are
// evidence quotations/transcriptions, never adopted Financial Profile facts.
public sealed class QaDocumentReader(IEvidenceContentReader evidence, IImageTextRecognizer recognizer, IPdfPageRenderer renderer)
{
    public const string Version = "qa-document-passages/1; tesseract-5.3.4; poppler-24.02.0";
    public async Task<QaDocumentPassages> Read(string customer, string version, TrustedSecurityContext security, CancellationToken ct)
    {
        var content = await evidence.ReadAsync(version, customer, security, "qa-document-" + Guid.NewGuid().ToString("N"), ct)
            ?? throw new TenantBoundaryException("AI_DOCUMENT_NOT_FOUND", 404);
        if (content.Reference.CustomerId != customer || content.Reference.DocumentVersionId != version ||
            content.Reference.ContentSha256.Length != 64) throw new TenantBoundaryException("AI_DOCUMENT_INVALID", 503);
        var reference = content.Reference;
        QaDocumentPassages Result(string state, IReadOnlyList<QaDocumentPassage> passages) => new(customer, version, reference.ContentSha256, Version, state, passages);
        try
        {
            IReadOnlyList<QaDocumentPassage> passages;
            if (reference.ContentType is "text/plain" or "text/csv") passages = TextPassages(content.Content);
            else if (reference.ContentType == XlsxFinancialDocumentExtractor.ContentType)
            {
                var facts = await new XlsxFinancialDocumentExtractor().ExtractAsync(content, "qa-document", ct);
                if (facts.Count > 20) return Result("UNSUPPORTED", []);
                passages = facts.Select((fact, index) => new QaDocumentPassage("passage-" + index.ToString(CultureInfo.InvariantCulture),
                    $"Extracted spreadsheet candidate: {fact.Label}: {fact.CandidateValue.ToString(CultureInfo.InvariantCulture)} {fact.Currency}; effective {fact.EffectiveDate:yyyy-MM-dd}. Not reviewed or verified financial truth.", fact.SourceLocation)).ToArray();
            }
            else if (reference.ContentType is "image/png" or "image/jpeg" or "application/pdf" && content.BinaryContent is not null)
            {
                var bytes = content.BinaryContent;
                if (reference.ContentType == "application/pdf") bytes = await renderer.RenderAsync(bytes, ct);
                var lines = FinancialDocumentOcrClassifier.ParseTsv(await recognizer.RecognizeAsync(bytes, ct));
                // Do not silently remove uncertain words and change the meaning of a passage.
                if (lines.Length == 0 || lines.Any(line => line.Confidence < 90)) return Result("UNRELIABLE_TEXT", []);
                passages = TextPassages(string.Join('\n', lines.Select(line => line.Text)));
            }
            else return Result("UNSUPPORTED", []);
            return Result(passages.Count == 0 ? "NO_TEXT" : "AVAILABLE", passages);
        }
        catch (DocumentRecognitionException) { return Result("UNSUPPORTED", []); }
    }
    public static IReadOnlyList<QaDocumentPassage> TextPassages(string text)
    {
        if (text.Length > 20000 || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new DocumentRecognitionException(DocumentRecognitionFailure.Incomplete);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var passages = new List<QaDocumentPassage>(); var buffer = new System.Text.StringBuilder(); var first = 1;
        void Add(int end)
        {
            if (string.IsNullOrWhiteSpace(buffer.ToString())) { buffer.Clear(); return; }
            passages.Add(new("passage-" + passages.Count.ToString(CultureInfo.InvariantCulture), buffer.ToString(), $"lines {first}-{end}")); buffer.Clear();
        }
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 1000) throw new DocumentRecognitionException(DocumentRecognitionFailure.Incomplete);
            if (buffer.Length + lines[i].Length + 1 > 1000) { Add(i); first = i + 1; }
            if (lines[i].Length == 0 && buffer.Length == 0) { first = i + 2; continue; }
            if (buffer.Length > 0) buffer.Append('\n'); buffer.Append(lines[i]);
        }
        Add(lines.Length);
        if (passages.Count > 20) throw new DocumentRecognitionException(DocumentRecognitionFailure.Incomplete);
        return passages;
    }
}
public static class QaDocumentEndpoints
{
    public static void MapQaDocumentPassages(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/local/v1/customers/{customerId}/documents/{versionId}/qa-passages");
        group.AddEndpointFilter(CustomerReviewHttp.Filter);
        group.MapGet("", async (string customerId, string versionId, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, QaDocumentReader reader) => {
            CustomerReviewHttp.NoQuery(http.Request);
            if (!TenantAccessClient.Identifier(customerId, 128) || !TenantAccessClient.Identifier(versionId, 160) || !bindings.AllowsCustomerLookup(customerId, TenantAccessClient.Headers(http).Tenant))
                throw new TenantBoundaryException("AI_DOCUMENT_INVALID", 400);
            var before = await Authorize(http, customerId, access);
            var (tenant, session) = TenantAccessClient.Headers(http);
            var security = new TrustedSecurityContext(new(before.ActorId, "Customer", DateTimeOffset.UtcNow, session),
                new("document-intelligence", "local-uat-workspace:" + tenant), new("customer-advice", null, before.DecisionId, customerId));
            var result = await reader.Read(customerId, versionId, security, http.RequestAborted);
            CustomerReviewHttp.Confirm(before, await Authorize(http, customerId, access)); return result;
        });
    }
    private static async Task<CustomerAccessDecision> Authorize(HttpContext http, string customer, TenantAccessClient access)
    {
        var read = await access.EvaluateCustomerAsync(http, "evidence.document.read", customer);
        var ai = await access.EvaluateCustomerAsync(http, "ai-intelligence.analysis.execute", customer);
        if (read.Outcome != "Allow" || ai.Outcome != "Allow" || read.AccessBasis != "Self" || ai.AccessBasis != "Self") throw new TenantBoundaryException("ACCESS_DENIED", 403);
        CustomerReviewHttp.Confirm(read, ai); return read;
    }
}
