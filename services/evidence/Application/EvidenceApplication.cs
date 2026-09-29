using System.Text.RegularExpressions;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.Services.Evidence.Application;

public sealed record EvidenceContentDescriptor(
    string Reference,
    string Sha256,
    string ContentType,
    long ContentLength = 0,
    string? StorageKey = null,
    string? StorageVersionId = null);

public sealed record EvidenceVersionRecord(
    string DocumentId,
    string DocumentVersionId,
    string EvidenceId,
    string CustomerId,
    int Version,
    string SourceName,
    string OriginalFileName,
    string ContentType,
    string ContentReference,
    string ContentSha256,
    DateTimeOffset ReceivedAt,
    DateTimeOffset CreatedAt,
    long ContentLength = 0,
    string? ContentStorageKey = null,
    string? ContentStorageVersionId = null);

public sealed record EvidenceSaveResult(EvidenceVersionRecord Version, bool Created, bool FirstVersion);

public sealed record EvidenceOperationIdentity(
    string ContractName,
    string ContractVersion,
    string CustomerId,
    string IdempotencyKey);

public interface IEvidenceRepository
{
    string AdapterKind { get; }

    Task<EvidenceSaveResult> SaveVersionAsync(
        EvidenceOperationIdentity identity,
        EvidenceVersionRecord candidate,
        Func<EvidenceVersionRecord, IReadOnlyList<object>> eventFactory,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EvidenceVersionRecord>> GetDocumentAsync(string documentId, CancellationToken cancellationToken);

    Task<EvidenceVersionRecord?> GetVersionAsync(string versionId, CancellationToken cancellationToken);
}

public interface IEvidenceContentStore
{
    Task<EvidenceContentDescriptor?> GetDescriptorAsync(string reference, CancellationToken cancellationToken);
}

public sealed class EvidenceApplication(
    IEvidenceRepository repository,
    IEvidenceContentStore contentStore,
    ILifecycleTelemetry telemetry,
    TimeProvider timeProvider)
{
    private static readonly Regex Sha256Pattern = new("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SupportedContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "application/pdf", "image/png", "image/jpeg", "text/plain" };

    public async Task<ContractResult<DocumentVersionCreatedResult>> CreateDocumentVersionAsync(
        ContractRequest<CreateDocumentVersion> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, Vs02ContractNames.CreateDocumentVersion);
        if (contextError is not null)
        {
            return Reject<DocumentVersionCreatedResult, CreateDocumentVersion>(request, contextError);
        }

        var payload = request.Payload;
        if (!string.Equals(payload.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal))
        {
            return ContractResult<DocumentVersionCreatedResult>.Rejected(
                request, "evidence.customer.denied", ContractErrorCategory.AccessDenied, "Evidence access is denied.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(payload.DocumentId) ||
            string.IsNullOrWhiteSpace(payload.DocumentVersionId) ||
            string.IsNullOrWhiteSpace(payload.EvidenceId) ||
            string.IsNullOrWhiteSpace(payload.ContentReference) ||
            string.IsNullOrWhiteSpace(payload.SourceName) ||
            !IsSafeFileName(payload.OriginalFileName) ||
            !SupportedContentTypes.Contains(payload.DeclaredContentType) ||
            !Sha256Pattern.IsMatch(payload.ContentSha256))
        {
            return ContractResult<DocumentVersionCreatedResult>.Rejected(
                request, "evidence.version.invalid", ContractErrorCategory.ValidationError, "Document version metadata is invalid.");
        }

        var content = await contentStore.GetDescriptorAsync(payload.ContentReference, cancellationToken);
        if (content is null ||
            !string.Equals(content.Sha256, payload.ContentSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(content.ContentType, payload.DeclaredContentType, StringComparison.OrdinalIgnoreCase))
        {
            return ContractResult<DocumentVersionCreatedResult>.Rejected(
                request,
                "evidence.content.unverified",
                ContractErrorCategory.PreconditionFailed,
                "The staged evidence content could not be verified.");
        }

        var now = timeProvider.GetUtcNow();
        var candidate = new EvidenceVersionRecord(
            payload.DocumentId,
            payload.DocumentVersionId,
            payload.EvidenceId,
            payload.CustomerId,
            0,
            payload.SourceName.Trim(),
            payload.OriginalFileName,
            payload.DeclaredContentType,
            payload.ContentReference,
            payload.ContentSha256.ToLowerInvariant(),
            payload.ReceivedAt,
            now,
            content.ContentLength,
            content.StorageKey,
            content.StorageVersionId);

        try
        {
            var saved = await repository.SaveVersionAsync(
                new EvidenceOperationIdentity(request.ContractName, request.ContractVersion,
                    payload.CustomerId, request.IdempotencyKey),
                candidate,
                version => CreateEvents(version, request),
                cancellationToken);
            var result = new DocumentVersionCreatedResult(
                saved.Version.DocumentId,
                saved.Version.DocumentVersionId,
                saved.Version.EvidenceId,
                saved.Version.Version,
                saved.Version.ContentReference,
                saved.Version.ContentSha256,
                saved.Version.CreatedAt);

            telemetry.Record(new LifecycleSignal(
                "Evidence Service",
                Vs02ContractNames.CreateDocumentVersion,
                saved.Created ? "CREATED" : "REPLAYED",
                request.RequestId,
                request.CorrelationId,
                request.CausationId,
                "document-version",
                saved.Version.DocumentVersionId,
                repository.AdapterKind));
            return ContractResult<DocumentVersionCreatedResult>.Succeeded(request, result);
        }
        catch (InvalidOperationException)
        {
            return ContractResult<DocumentVersionCreatedResult>.Rejected(
                request, "evidence.version.conflict", ContractErrorCategory.Conflict, "Document version identity conflicts with existing evidence.");
        }
    }

    public async Task<ContractResult<EvidenceMetadata>> GetEvidenceMetadataAsync(
        ContractRequest<GetEvidenceMetadata> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, Vs02ContractNames.GetEvidenceMetadata);
        if (contextError is not null)
        {
            return Reject<EvidenceMetadata, GetEvidenceMetadata>(request, contextError);
        }

        if (!string.Equals(request.Payload.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal))
        {
            return ContractResult<EvidenceMetadata>.Rejected(
                request, "evidence.customer.denied", ContractErrorCategory.AccessDenied, "Evidence access is denied.");
        }

        var versions = await repository.GetDocumentAsync(request.Payload.DocumentId, cancellationToken);
        if (versions.Count == 0 || versions.Any(version => !string.Equals(version.CustomerId, request.Payload.CustomerId, StringComparison.Ordinal)))
        {
            return ContractResult<EvidenceMetadata>.Rejected(
                request, "evidence.document.not-found", ContractErrorCategory.NotFound, "Authorized evidence metadata was not found.");
        }

        var metadata = new EvidenceMetadata(
            request.Payload.DocumentId,
            request.Payload.CustomerId,
            versions.Select(version => new EvidenceVersionMetadata(
                version.DocumentVersionId,
                version.EvidenceId,
                version.Version,
                version.SourceName,
                version.OriginalFileName,
                version.ContentType,
                version.ContentSha256,
                version.ReceivedAt)).ToArray());
        return ContractResult<EvidenceMetadata>.Succeeded(request, metadata);
    }

    public async Task<ContractResult<EvidenceReference>> GetEvidenceReferenceAsync(
        ContractRequest<GetEvidenceReference> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, Vs02ContractNames.GetEvidenceReference);
        if (contextError is not null)
        {
            return Reject<EvidenceReference, GetEvidenceReference>(request, contextError);
        }

        var version = await repository.GetVersionAsync(request.Payload.DocumentVersionId, cancellationToken);
        if (version is null ||
            !string.Equals(version.CustomerId, request.Payload.CustomerId, StringComparison.Ordinal) ||
            !string.Equals(request.Payload.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal))
        {
            return ContractResult<EvidenceReference>.Rejected(
                request, "evidence.reference.not-found", ContractErrorCategory.NotFound, "Authorized evidence reference was not found.");
        }

        var reference = new EvidenceReference(
            version.EvidenceId,
            version.DocumentId,
            version.DocumentVersionId,
            version.CustomerId,
            version.ContentReference,
            version.ContentSha256,
            version.ContentType);
        return ContractResult<EvidenceReference>.Succeeded(request, reference);
    }

    private static IReadOnlyList<object> CreateEvents(
        EvidenceVersionRecord version,
        ContractRequest<CreateDocumentVersion> request)
    {
        var registered = new DomainEvent<EvidenceRegisteredPayload>(
            "CID-023",
            $"evt-evidence-{version.EvidenceId}",
            Vs02ContractNames.EvidenceRegistered,
            ContractGuard.CurrentVersion,
            version.CreatedAt,
            request.CorrelationId,
            request.RequestId,
            "Evidence Service",
            "evidence",
            version.EvidenceId,
            new EvidenceRegisteredPayload(version.EvidenceId, version.DocumentId, version.CustomerId));
        var versionCreated = new DomainEvent<EvidenceVersionCreatedPayload>(
            "CID-024",
            $"evt-version-{version.DocumentVersionId}",
            Vs02ContractNames.EvidenceVersionCreated,
            ContractGuard.CurrentVersion,
            version.CreatedAt,
            request.CorrelationId,
            request.RequestId,
            "Evidence Service",
            "document-version",
            version.DocumentVersionId,
            new EvidenceVersionCreatedPayload(
                version.EvidenceId,
                version.DocumentId,
                version.DocumentVersionId,
                version.Version,
                version.ContentSha256));
        return version.Version == 1 ? [registered, versionCreated] : [versionCreated];
    }

    private static bool IsSafeFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        !fileName.Contains('/', StringComparison.Ordinal) &&
        !fileName.Contains('\\', StringComparison.Ordinal) &&
        string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal);

    private static ContractResult<TData> Reject<TData, TPayload>(
        ContractRequest<TPayload> request,
        ContractError error) =>
        new(
            request.ContractName,
            request.ContractVersion,
            request.RequestId,
            request.CorrelationId,
            ContractOutcome.Rejected,
            default,
            error);
}
