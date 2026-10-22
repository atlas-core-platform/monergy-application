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
public sealed class QaDocumentReader(IEvidenceContentReader evidence, IDocumentOcrRepository ocr)
{
    public const string Version = "qa-document-passages/1; tesseract-5.3.4; poppler-24.02.0";
    public const string RetainedOcrVersion = "qa-document-passages/2; retained-source-ocr/1";
    public async Task<QaDocumentPassages> Read(string customer, string version, TrustedSecurityContext security, CancellationToken ct)
    {
        var content = await evidence.ReadAsync(version, customer, security, "qa-document-" + Guid.NewGuid().ToString("N"), ct)
            ?? throw new TenantBoundaryException("AI_DOCUMENT_NOT_FOUND", 404);
        if (content.Reference.CustomerId != customer || content.Reference.DocumentVersionId != version ||
            content.Reference.ContentSha256.Length != 64) throw new TenantBoundaryException("AI_DOCUMENT_INVALID", 503);
        var reference = content.Reference;
        QaDocumentPassages Result(string state, IReadOnlyList<QaDocumentPassage> passages) => new(customer, version, reference.ContentSha256, reference.ContentType is "image/png" or "image/jpeg" or "application/pdf" ? RetainedOcrVersion : Version, state, passages);
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
                const string prefix = "local-uat-workspace:";
                var identity = security.Workload.WorkloadIdentityId;
                if (!identity.StartsWith(prefix, StringComparison.Ordinal) || !TenantAccessClient.Identifier(identity[prefix.Length..], 64))
                    throw new TenantBoundaryException("AI_DOCUMENT_INVALID", 503);
                var tenant = identity[prefix.Length..];
                DocumentOcrResult? recorded;
                try { recorded = await ocr.ReadOcrAsync(tenant, customer, version, ct); }
                catch (InvalidOperationException) { throw new TenantBoundaryException("AI_DOCUMENT_UNAVAILABLE", 503); }
                if (recorded is null) return Result("NOT_PROCESSED", []);
                if (!DocumentOcrRules.Valid(recorded) || recorded.TenantId != tenant || recorded.CustomerId != customer ||
                    recorded.DocumentVersionId != version || !string.Equals(recorded.SourceHash, reference.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    throw new TenantBoundaryException("AI_DOCUMENT_INVALID", 503);
                if (recorded.State != "AVAILABLE") return Result(recorded.State, []);
                // Preserve page boundaries and line locations. Never discard uncertain/blank
                // pages or silently return a prefix when the quotation budget is exceeded.
                var collected = new List<QaDocumentPassage>();
                foreach (var page in recorded.Pages)
                    foreach (var passage in TextPassages(string.Join('\n', page.Lines.Select(line => line.Text))))
                        collected.Add(passage with { Id = "passage-" + collected.Count.ToString(CultureInfo.InvariantCulture), Location = $"page {page.Number}, {passage.Location}" });
                if (collected.Count > 20 || collected.Sum(x => x.Text.Length) > 20000) return Result("UNSUPPORTED", []);
                passages = collected;
            }
            else return Result("UNSUPPORTED", []);
            return Result(passages.Count == 0 ? "NO_TEXT" : "AVAILABLE", passages);
        }
        catch (DocumentExtractionException) { return Result("UNSUPPORTED", []); }
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
