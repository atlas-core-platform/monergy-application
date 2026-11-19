namespace Monergy.Platform;

public sealed record HelpTopicSubmission(string ContractVersion, string GeneralQuestion, string? TopicId,
    string SourceAudience, string KnowledgeRevision, string RetrievalRevision, string IdempotencyKey);
public sealed record FeedbackCounter(string ContractVersion, string SourceAudience, string Category, string Intent,
    string KnowledgeRevision, string RetrievalRevision);
public sealed record TopicResolution(string ApprovedChangeReference, string PublishedVersion, string VerificationReference);
public sealed record ApprovedPublication(string ContractVersion, string KnowledgeRevision, string RetrievalRevision, TopicResolution[] ApprovedChanges);
public sealed record HelpTopic(string GapId, string GeneralQuestion, string SourceAudience, string Classification,
    string Status, long Revision, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? TopicId,
    string? OwnerReference, string? DuplicateOf, bool ApprovedGeneralTopic, TopicResolution? Resolution,
    long? TopicSubmissionCount = null, long? MatchedOutcomeCount = null);
public sealed record TopicChange(string ContractVersion, long ExpectedRevision, string Status, string Classification,
    bool ApprovedGeneralTopic, string? TopicId, string? OwnerReference, string? DuplicateOf, bool PrivacyDelete, TopicResolution? Resolution);
public sealed record FeedbackReviewRequest(string AuthenticationContext, string? GapId, string? Cursor, int Limit,
    string? Status, string? SourceAudience, string? OperationId, TopicChange? Change);
public sealed record FeedbackPage(IReadOnlyList<HelpTopic> Topics, string? NextCursor);
public sealed record FeedbackSaved(bool Saved, string GapId, string AuditState = "NotApplicable");
public sealed record FeedbackDelivery(string EventId, int Attempts, int Recoveries, string State);
public sealed record FeedbackDeliveryStatus(string GapId, IReadOnlyList<FeedbackDelivery> Deliveries, int RetryLimit, int RecoveryLimit);
