namespace Monergy.Contracts;

public static class Vs02ContractNames
{
    public const string CreateDocumentVersion = "CreateDocumentVersion";
    public const string GetEvidenceMetadata = "GetEvidenceMetadata";
    public const string GetEvidenceReference = "GetEvidenceReference";
    public const string EvidenceRegistered = "EvidenceRegistered";
    public const string EvidenceVersionCreated = "EvidenceVersionCreated";
    public const string ProcessDocument = "ProcessDocument";
    public const string GetProcessingStatus = "GetProcessingStatus";
    public const string ValidatedSourceFactsProduced = "ValidatedSourceFactsProduced";
    public const string NormalizeSourceFacts = "NormalizeSourceFacts";
    public const string GetFinancialProvenance = "GetFinancialProvenance";
    public const string FinancialFactCreated = "FinancialFactCreated";
    public const string FinancialFactUpdated = "FinancialFactUpdated";
    public const string ScheduleJob = "ScheduleJob";
    public const string GetJobStatus = "GetJobStatus";
}

public sealed record CreateDocumentVersion(
    string DocumentId,
    string DocumentVersionId,
    string EvidenceId,
    string CustomerId,
    string SourceName,
    string OriginalFileName,
    string DeclaredContentType,
    string ContentReference,
    string ContentSha256,
    DateTimeOffset ReceivedAt);

public sealed record DocumentVersionCreatedResult(
    string DocumentId,
    string DocumentVersionId,
    string EvidenceId,
    int Version,
    string ContentReference,
    string ContentSha256,
    DateTimeOffset CreatedAt);

public sealed record GetEvidenceMetadata(string DocumentId, string CustomerId);

public sealed record EvidenceVersionMetadata(
    string DocumentVersionId,
    string EvidenceId,
    int Version,
    string SourceName,
    string OriginalFileName,
    string ContentType,
    string ContentSha256,
    DateTimeOffset ReceivedAt);

public sealed record EvidenceMetadata(
    string DocumentId,
    string CustomerId,
    IReadOnlyList<EvidenceVersionMetadata> Versions);

public sealed record GetEvidenceReference(string DocumentVersionId, string CustomerId);

public sealed record EvidenceReference(
    string EvidenceId,
    string DocumentId,
    string DocumentVersionId,
    string CustomerId,
    string ContentReference,
    string ContentSha256,
    string ContentType);

public sealed record EvidenceRegisteredPayload(string EvidenceId, string DocumentId, string CustomerId);

public sealed record EvidenceVersionCreatedPayload(
    string EvidenceId,
    string DocumentId,
    string DocumentVersionId,
    int Version,
    string ContentSha256);

public sealed record ProcessDocument(
    string ProcessingId,
    string DocumentVersionId,
    string EvidenceReferenceId,
    string CustomerId,
    DateTimeOffset RequestedAt);

public enum ProcessingState
{
    Accepted,
    Running,
    Completed,
    Failed,
}

public sealed record ProcessingStatus(
    string ProcessingId,
    string DocumentVersionId,
    ProcessingState State,
    string? FailureCode,
    bool Retryable,
    DateTimeOffset UpdatedAt);

public sealed record GetProcessingStatus(string ProcessingId, string CustomerId);

public sealed record ValidatedSourceFact(
    string SourceFactId,
    string CustomerId,
    string FactType,
    string Label,
    decimal CandidateValue,
    string Currency,
    DateOnly EffectiveDate,
    string SourceLocation,
    decimal Confidence,
    string EvidenceId,
    string DocumentVersionId,
    string ExtractionVersion,
    string ValidationVersion);

public sealed record ValidatedSourceFactsProducedPayload(
    string ProcessingId,
    string CustomerId,
    string EvidenceId,
    string DocumentVersionId,
    IReadOnlyList<ValidatedSourceFact> Facts);

public sealed record NormalizeSourceFacts(
    string FinancialProfileId,
    string CustomerId,
    string ProcessingId,
    IReadOnlyList<ValidatedSourceFact> Facts,
    string NormalizationVersion,
    DateTimeOffset RequestedAt);

public sealed record NormalizedFinancialFact(
    string FinancialFactId,
    string FinancialProfileId,
    string CustomerId,
    string FactType,
    string Label,
    decimal Value,
    string Currency,
    DateOnly EffectiveDate,
    int Revision,
    string FinancialProvenanceId,
    bool Created);

public sealed record NormalizationResult(IReadOnlyList<NormalizedFinancialFact> Facts);

public sealed record GetFinancialProvenance(string FinancialProvenanceId, string CustomerId);

public sealed record FinancialProvenance(
    string FinancialProvenanceId,
    string FinancialFactId,
    string CustomerId,
    string EvidenceId,
    string DocumentVersionId,
    string SourceFactId,
    string ExtractionVersion,
    string ValidationVersion,
    string NormalizationVersion,
    string ActorId,
    string WorkloadIdentityId,
    DateTimeOffset RecordedAt,
    string CorrelationId,
    string? CausationId);

public sealed record FinancialFactChangedPayload(
    string FinancialProfileId,
    string FinancialFactId,
    string FactType,
    DateTimeOffset ChangedAt,
    string FinancialProvenanceId,
    int Revision);

public sealed record ScheduleJob(
    string JobId,
    string JobName,
    string OwnerService,
    string PayloadReference,
    string CustomerId,
    DateTimeOffset RequestedAt);

public enum JobState
{
    Scheduled,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public sealed record ScheduledJob(
    string JobId,
    string JobName,
    string OwnerService,
    string PayloadReference,
    string CustomerId,
    JobState State,
    int Attempt,
    string? FailureCode,
    bool Retryable,
    DateTimeOffset UpdatedAt);

public sealed record GetJobStatus(string JobId, string CustomerId);
