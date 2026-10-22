using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Monergy.Services.Evidence.Application;

namespace Monergy.Services.Evidence.Infrastructure;

public sealed partial class InMemoryEvidenceRepository : IEvidenceRepository
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
            if (documentVersions.Any(version => !string.Equals(version.CustomerId, candidate.CustomerId, StringComparison.Ordinal) || version.TenantId is not null && version.TenantId != candidate.TenantId))
            {
                throw new InvalidOperationException("Document identity belongs to another customer.");
            }

            DocumentVault.Replacement(candidate, documentVersions.LastOrDefault());
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

    public Task<IReadOnlyList<EvidenceVersionRecord>> ListCustomerAsync(
        string customerId,
        int limit,
        CancellationToken cancellationToken, EvidenceVersionRecord? after = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            return Task.FromResult<IReadOnlyList<EvidenceVersionRecord>>(
                versions.Values
                    .Where(version => string.Equals(version.CustomerId, customerId, StringComparison.Ordinal))
                    .Where(version => after is null || version.CreatedAt < after.CreatedAt ||
                        (version.CreatedAt == after.CreatedAt && string.CompareOrdinal(version.DocumentVersionId, after.DocumentVersionId) < 0))
                    .OrderByDescending(version => version.CreatedAt)
                    .ThenByDescending(version => version.DocumentVersionId, StringComparer.Ordinal)
                    .Take(limit)
                    .ToArray());
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
        prior.SourceLocation == candidate.SourceLocation &&
        prior.ExpectedPreviousVersionId == candidate.ExpectedPreviousVersionId &&
        (candidate.ExpectedPreviousVersionId is null || prior.OriginalFileName == candidate.OriginalFileName && prior.ContentType == candidate.ContentType) &&
        prior.TenantId == candidate.TenantId &&
        string.Equals(prior.DocumentId, candidate.DocumentId, StringComparison.Ordinal) &&
        string.Equals(prior.DocumentVersionId, candidate.DocumentVersionId, StringComparison.Ordinal) &&
        string.Equals(prior.EvidenceId, candidate.EvidenceId, StringComparison.Ordinal) &&
        string.Equals(prior.CustomerId, candidate.CustomerId, StringComparison.Ordinal) &&
        string.Equals(prior.ContentReference, candidate.ContentReference, StringComparison.Ordinal) &&
        string.Equals(prior.ContentSha256, candidate.ContentSha256, StringComparison.OrdinalIgnoreCase);
}

public sealed class ReferenceEvidenceContentStore : IEvidenceIntakeStore
{
    private readonly ConcurrentDictionary<string, (EvidenceContentDescriptor Descriptor, byte[] Content)> content =
        new(StringComparer.Ordinal);

    public void Seed(string reference, string sha256, string contentType, string value) =>
        content[reference] = (new EvidenceContentDescriptor(reference, sha256, contentType,
            Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value));

    public EvidenceContentDescriptor Stage(string operationId, byte[] value, string contentType)
    {
        ArgumentNullException.ThrowIfNull(value);
        var reference = "reference://customer-intake/" + operationId;
        var descriptor = new EvidenceContentDescriptor(reference,
            Convert.ToHexStringLower(SHA256.HashData(value)), contentType, value.LongLength);
        if (!content.TryAdd(reference, (descriptor, value.ToArray())))
        {
            var current = content[reference].Descriptor;
            if (current.Sha256 != descriptor.Sha256 || current.ContentType != descriptor.ContentType ||
                current.ContentLength != descriptor.ContentLength)
                throw new InvalidOperationException("Reference evidence operation identifies different content.");
            return current;
        }
        return descriptor;
    }

    public EvidenceContentDescriptor StageScanned(string operationId, byte[] value, string contentType, EvidenceMalwareCheck check, EvidenceSourceLocation? sourceLocation = null)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(value));
        if (!EvidenceMalwarePolicy.Verified(check, hash, DateTimeOffset.UtcNow)) throw new EvidenceScanUnavailableException();
        var reference = "reference://customer-intake/" + operationId;
        var descriptor = new EvidenceContentDescriptor(reference, hash, contentType, value.LongLength, MalwareCheck: check, SourceLocation: sourceLocation);
        return content.AddOrUpdate(reference, _ => (descriptor, value.ToArray()), (_, current) =>
        {
            if (current.Descriptor.SourceLocation != sourceLocation || current.Descriptor.Sha256 != hash || current.Descriptor.ContentType != contentType || current.Descriptor.ContentLength != value.LongLength)
                throw new InvalidOperationException("Reference evidence operation identifies different content.");
            return (descriptor, current.Content);
        }).Descriptor;
    }

    public Task<EvidenceContentDescriptor> StageScannedAsync(string operationId, byte[] bytes, string contentType,
        EvidenceMalwareCheck check, EvidenceSourceLocation? source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(StageScanned(operationId, bytes, contentType, check, source));
    }
    public Task<byte[]?> ReadRegisteredAsync(EvidenceVersionRecord version, CancellationToken ct) => ReadBytesAsync(version.ContentReference, ct);

    public Task<EvidenceContentDescriptor?> GetDescriptorAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(content.TryGetValue(reference, out var item) ? item.Descriptor : null);
    }

    public Task<byte[]?> ReadBytesAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(content.TryGetValue(reference, out var item) ? item.Content.ToArray() : null);
    }

    public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(content.TryGetValue(reference, out var item)
            ? Encoding.UTF8.GetString(item.Content)
            : null);
    }
}
