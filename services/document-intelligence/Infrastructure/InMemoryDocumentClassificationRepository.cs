using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
namespace Monergy.Services.DocumentIntelligence.Infrastructure;
public sealed class InMemoryDocumentClassificationRepository : IDocumentClassificationRepository
{
    private readonly object sync = new();
    private readonly Dictionary<(string Tenant, string Customer, string Version), DocumentClassification> rows = [];
    public Task<DocumentClassification> RecordAsync(DocumentClassification candidate, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (sync)
        {
            var key = (candidate.TenantId, candidate.CustomerId, candidate.DocumentVersionId);
            if (rows.TryGetValue(key, out var current))
            {
                if (current.SourceHash != candidate.SourceHash) throw new EvidenceDependencyException("Classification source changed.");
                return Task.FromResult(current);
            }
            rows.Add(key, candidate with { Reviews = Array.AsReadOnly(candidate.Reviews.ToArray()) }); return Task.FromResult(rows[key]);
        }
    }
    public Task<DocumentClassification?> ReadAsync(string tenant, string customer, string version, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (sync) return Task.FromResult(rows.GetValueOrDefault((tenant, customer, version))); }
    public Task<IReadOnlyList<DocumentClassification>> ListAsync(string tenant, bool unknownOnly, string? after, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (sync) return Task.FromResult<IReadOnlyList<DocumentClassification>>(rows.Values
            .Where(x => x.TenantId == tenant && (!unknownOnly || x.NeedsReview) && (after is null || string.CompareOrdinal(ClassificationRules.Key(x.CustomerId, x.DocumentVersionId), after) > 0))
            .OrderBy(x => ClassificationRules.Key(x.CustomerId, x.DocumentVersionId), StringComparer.Ordinal).Take(51).ToArray());
    }
    public Task<DocumentClassification> ReviewAsync(string tenant, string customer, string version, string actor, ClassificationChange change, DateTimeOffset now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (sync)
        {
            var key = (tenant, customer, version);
            var current = rows.GetValueOrDefault(key) ?? throw new TenantBoundaryException("CLASSIFICATION_NOT_FOUND", 404);
            var next = ClassificationRules.Apply(current, actor, change, now);
            rows[key] = next with { Reviews = Array.AsReadOnly(next.Reviews.ToArray()) }; return Task.FromResult(rows[key]);
        }
    }
}
