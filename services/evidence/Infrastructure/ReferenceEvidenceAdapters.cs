using System.Collections.Concurrent;
using Monergy.Services.Evidence.Application;

namespace Monergy.Services.Evidence.Infrastructure;

public sealed class InMemoryEvidenceRepository : IEvidenceRepository
{
    private readonly object sync = new();
    private readonly Dictionary<string, EvidenceVersionRecord> versions = new(StringComparer.Ordinal);
    private readonly Dictionary<EvidenceOperationIdentity, string> idempotency = [];
    private readonly List<object> outbox = [];

    public string AdapterKind => "IN_MEMORY_REFERENCE";

    public Task<EvidenceSaveResult> SaveVersionAsync(
        EvidenceOperationIdentity identity,
        EvidenceVersionRecord candidate,
        Func<EvidenceVersionRecord, IReadOnlyList<object>> eventFactory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (idempotency.TryGetValue(identity, out var existingId))
            {
                var prior = versions[existingId];
                if (!Equivalent(prior, candidate))
                {
                    throw new InvalidOperationException("An idempotency key cannot identify different document content.");
                }

                return Task.FromResult(new EvidenceSaveResult(prior, false, prior.Version == 1));
            }

            if (versions.ContainsKey(candidate.DocumentVersionId))
            {
                throw new InvalidOperationException("Document version identity already exists.");
            }

            var documentVersions = versions.Values
                .Where(version => string.Equals(version.DocumentId, candidate.DocumentId, StringComparison.Ordinal))
                .OrderBy(version => version.Version)
                .ToArray();
            if (documentVersions.Any(version => !string.Equals(version.CustomerId, candidate.CustomerId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Document identity belongs to another customer.");
            }

            var saved = candidate with { Version = documentVersions.Length + 1 };
            versions.Add(saved.DocumentVersionId, saved);
            idempotency.Add(identity, saved.DocumentVersionId);
            outbox.AddRange(eventFactory(saved));
            return Task.FromResult(new EvidenceSaveResult(saved, true, documentVersions.Length == 0));
        }
    }

    public Task<IReadOnlyList<EvidenceVersionRecord>> GetDocumentAsync(
        string documentId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            return Task.FromResult<IReadOnlyList<EvidenceVersionRecord>>(
                versions.Values
                    .Where(version => string.Equals(version.DocumentId, documentId, StringComparison.Ordinal))
                    .OrderBy(version => version.Version)
                    .ToArray());
        }
    }

    public Task<EvidenceVersionRecord?> GetVersionAsync(string versionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            versions.TryGetValue(versionId, out var version);
            return Task.FromResult(version);
        }
    }

    public IReadOnlyList<object> DrainOutbox()
    {
        lock (sync)
        {
            var drained = outbox.ToArray();
            outbox.Clear();
            return drained;
        }
    }

    private static bool Equivalent(EvidenceVersionRecord prior, EvidenceVersionRecord candidate) =>
        string.Equals(prior.DocumentId, candidate.DocumentId, StringComparison.Ordinal) &&
        string.Equals(prior.DocumentVersionId, candidate.DocumentVersionId, StringComparison.Ordinal) &&
        string.Equals(prior.EvidenceId, candidate.EvidenceId, StringComparison.Ordinal) &&
        string.Equals(prior.CustomerId, candidate.CustomerId, StringComparison.Ordinal) &&
        string.Equals(prior.ContentReference, candidate.ContentReference, StringComparison.Ordinal) &&
        string.Equals(prior.ContentSha256, candidate.ContentSha256, StringComparison.OrdinalIgnoreCase);
}

public sealed class ReferenceEvidenceContentStore : IEvidenceContentStore
{
    private readonly ConcurrentDictionary<string, (EvidenceContentDescriptor Descriptor, string Content)> content =
        new(StringComparer.Ordinal);

    public void Seed(string reference, string sha256, string contentType, string value) =>
        content[reference] = (new EvidenceContentDescriptor(reference, sha256, contentType), value);

    public Task<EvidenceContentDescriptor?> GetDescriptorAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(content.TryGetValue(reference, out var item) ? item.Descriptor : null);
    }

    public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(content.TryGetValue(reference, out var item) ? item.Content : null);
    }
}
