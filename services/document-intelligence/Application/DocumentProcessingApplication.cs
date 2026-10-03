using System.Security.Cryptography;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.Services.DocumentIntelligence.Application;

public sealed record EvidenceContent(EvidenceReference Reference, string Content);

public sealed class EvidenceDependencyException(string message) : Exception(message);

public sealed class DocumentExtractionException(string message) : Exception(message);

public sealed class ProcessingConflictException(string message) : Exception(message);

public sealed class ProcessingAtomicCommitException(string message) : Exception(message);

public interface IEvidenceContentReader
{
    Task<EvidenceContent?> ReadAsync(
        string documentVersionId,
        string customerId,
        TrustedSecurityContext security,
        string correlationId,
        CancellationToken cancellationToken);
}

public interface IExecutionPolicy
{
    Task<ContractError?> EvaluateAsync(
        TrustedSecurityContext security,
        string customerId,
        string correlationId,
        CancellationToken cancellationToken);
}

public interface IDocumentExtractor
{
    Task<IReadOnlyList<ValidatedSourceFact>> ExtractAsync(
        EvidenceContent content,
        string processingId,
        CancellationToken cancellationToken);
}

public sealed record ProcessingPredecessorSnapshot(
    string ProcessingId,
    string DocumentVersionId,
    string EvidenceReferenceId,
    ProcessingState State,
    string? FailureCode,
    bool Retryable,
    DateTimeOffset UpdatedAt,
    int FactCount,
    string? TerminalEventId);

public sealed record ProcessingRecord(
    string ProcessingId,
    string DocumentVersionId,
    string EvidenceReferenceId,
    string CustomerId,
    string ContractName,
    string ContractVersion,
    string IdempotencyKey,
    string PayloadFingerprint,
    string? PreviousProcessingId,
    ReprocessingReason? ReprocessingReason,
    ProcessingPredecessorSnapshot? PredecessorSnapshot,
    ProcessingState State,
    string? FailureCode,
    ContractErrorCategory? FailureCategory,
    bool Retryable,
    int Attempt,
    string? ActiveAttemptId,
    string? TerminalEventId,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ValidatedSourceFact> Facts);

public interface IDocumentProcessingRepository
{
    Task<(ProcessingRecord Record, bool Created)> AcceptAsync(
        ProcessingRecord candidate,
        CancellationToken cancellationToken);

    Task<ProcessingRecord?> GetByIdempotencyAsync(
        string contractName,
        string contractVersion,
        string customerId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<ProcessingRecord> StartAttemptAsync(
        string processingId,
        ProcessingState expected,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken);

    Task<ProcessingRecord> CommitTerminalAsync(
        string processingId,
        string attemptId,
        ProcessingState target,
        DateTimeOffset updatedAt,
        IReadOnlyList<ValidatedSourceFact>? facts,
        string? failureCode,
        ContractErrorCategory? failureCategory,
        bool retryable,
        object outboxEvent,
        CancellationToken cancellationToken);

    Task<ProcessingRecord?> GetAsync(string processingId, CancellationToken cancellationToken);
}

public sealed class DocumentProcessingApplication(
    IDocumentProcessingRepository repository,
    IEvidenceContentReader contentReader,
    IDocumentExtractor extractor,
    IExecutionPolicy executionPolicy,
    ILifecycleTelemetry telemetry,
    TimeProvider timeProvider)
{
    public async Task<ContractResult<ProcessingStatus>> ProcessDocumentAsync(
        ContractRequest<ProcessDocument> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, Vs02ContractNames.ProcessDocument);
        if (contextError is not null)
        {
            return Reject<ProcessingStatus, ProcessDocument>(request, contextError);
        }

        var payload = request.Payload;
        if (payload is null)
        {
            return ContractResult<ProcessingStatus>.Rejected(
                request, "processing.request.invalid", ContractErrorCategory.ValidationError, "The processing request is invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(payload.ProcessingId) ||
            string.IsNullOrWhiteSpace(payload.DocumentVersionId) ||
            string.IsNullOrWhiteSpace(payload.EvidenceReferenceId) ||
            !string.Equals(payload.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal))
        {
            return ContractResult<ProcessingStatus>.Rejected(
                request, "processing.request.invalid", ContractErrorCategory.ValidationError, "The processing request is invalid.");
        }

        var policyError = await executionPolicy.EvaluateAsync(
            request.Security,
            payload.CustomerId,
            request.CorrelationId,
            cancellationToken);
        if (policyError is not null)
        {
            return Reject<ProcessingStatus, ProcessDocument>(request, policyError);
        }

        var now = timeProvider.GetUtcNow();
        var candidate = new ProcessingRecord(
            payload.ProcessingId,
            payload.DocumentVersionId,
            payload.EvidenceReferenceId,
            payload.CustomerId,
            request.ContractName,
            request.ContractVersion,
            request.IdempotencyKey!,
            Fingerprint(payload),
            null,
            null,
            null,
            ProcessingState.Accepted,
            null,
            null,
            false,
            0,
            null,
            null,
            now,
            []);

        try
        {
            var accepted = await repository.AcceptAsync(candidate, cancellationToken);
            ProcessingRecord started;
            if (!accepted.Created)
            {
                if (accepted.Record.State != ProcessingState.Failed || !accepted.Record.Retryable)
                {
                    return ContractResult<ProcessingStatus>.Succeeded(request, ToStatus(accepted.Record));
                }

                started = await repository.StartAttemptAsync(
                    payload.ProcessingId,
                    ProcessingState.Failed,
                    now,
                    cancellationToken);
            }
            else
            {
                started = await repository.StartAttemptAsync(
                    payload.ProcessingId,
                    ProcessingState.Accepted,
                    now,
                    cancellationToken);
            }

            return await ExecuteAsync(request, started, cancellationToken);
        }
        catch (ProcessingConflictException)
        {
            return ContractResult<ProcessingStatus>.Rejected(
                request, "processing.state.conflict", ContractErrorCategory.Conflict, "The processing identity, request, or state conflicts.");
        }
    }

    public async Task<ContractResult<ReprocessDocumentResult>> ReprocessDocumentAsync(
        ContractRequest<ReprocessDocument> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, D12ContractNames.ReprocessDocument);
        if (contextError is not null)
        {
            return Reject<ReprocessDocumentResult, ReprocessDocument>(request, contextError);
        }

        var payload = request.Payload;
        if (payload is null)
        {
            return ContractResult<ReprocessDocumentResult>.Rejected(
                request, "reprocessing.request.invalid", ContractErrorCategory.ValidationError, "The reprocessing request is invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(payload.ProcessingId) ||
            string.IsNullOrWhiteSpace(payload.PreviousProcessingId) ||
            string.IsNullOrWhiteSpace(payload.CustomerId) ||
            string.IsNullOrWhiteSpace(payload.EvidenceReferenceId) ||
            string.IsNullOrWhiteSpace(payload.DocumentVersionId) ||
            payload.ProcessingId == payload.PreviousProcessingId ||
            !Enum.IsDefined(payload.Reason) ||
            !HasCompleteD12Security(request.Security) ||
            !string.Equals(payload.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal))
        {
            return ContractResult<ReprocessDocumentResult>.Rejected(
                request, "reprocessing.request.invalid", ContractErrorCategory.ValidationError, "The reprocessing request is invalid.");
        }

        var policyError = await executionPolicy.EvaluateAsync(
            request.Security,
            payload.CustomerId,
            request.CorrelationId,
            cancellationToken);
        if (policyError is not null)
        {
            return Reject<ReprocessDocumentResult, ReprocessDocument>(request, policyError);
        }

        var payloadFingerprint = Fingerprint(payload);
        var replay = await repository.GetByIdempotencyAsync(
            request.ContractName,
            request.ContractVersion,
            payload.CustomerId,
            request.IdempotencyKey!,
            cancellationToken);
        if (replay is not null)
        {
            return string.Equals(replay.PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal)
                ? ContractResult<ReprocessDocumentResult>.Succeeded(request, ToReprocessResult(replay))
                : ContractResult<ReprocessDocumentResult>.Rejected(
                    request,
                    "reprocessing.idempotency.conflict",
                    ContractErrorCategory.Conflict,
                    "The idempotency key is already bound to different reprocessing intent.");
        }

        var predecessor = await repository.GetAsync(payload.PreviousProcessingId, cancellationToken);
        if (predecessor is null ||
            !string.Equals(predecessor.CustomerId, payload.CustomerId, StringComparison.Ordinal) ||
            !string.Equals(predecessor.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal))
        {
            return ContractResult<ReprocessDocumentResult>.Rejected(
                request, "reprocessing.predecessor.not-found", ContractErrorCategory.NotFound, "An authorized terminal predecessor was not found.");
        }

        var eligibilityError = ValidateEligibility(payload, predecessor, request.CorrelationId);
        if (eligibilityError is not null)
        {
            return Reject<ReprocessDocumentResult, ReprocessDocument>(request, eligibilityError);
        }

        EvidenceContent? source;
        try
        {
            source = await contentReader.ReadAsync(
                payload.DocumentVersionId,
                payload.CustomerId,
                request.Security,
                request.CorrelationId,
                cancellationToken);
        }
        catch (EvidenceDependencyException)
        {
            return ContractResult<ReprocessDocumentResult>.Rejected(
                request, "reprocessing.evidence.dependency-unavailable", ContractErrorCategory.DependencyFailure,
                "The evidence authority is temporarily unavailable.", true);
        }

        if (!MatchesRequestedSource(source, payload.CustomerId, payload.EvidenceReferenceId, payload.DocumentVersionId))
        {
            return ContractResult<ReprocessDocumentResult>.Rejected(
                request, "reprocessing.evidence.invalid", ContractErrorCategory.PreconditionFailed,
                "The requested immutable Evidence-owned source is unavailable or inconsistent.");
        }

        var now = timeProvider.GetUtcNow();
        var candidate = new ProcessingRecord(
            payload.ProcessingId,
            payload.DocumentVersionId,
            payload.EvidenceReferenceId,
            payload.CustomerId,
            request.ContractName,
            request.ContractVersion,
            request.IdempotencyKey!,
            payloadFingerprint,
            predecessor.ProcessingId,
            payload.Reason,
            Snapshot(predecessor),
            ProcessingState.Accepted,
            null,
            null,
            false,
            0,
            null,
            null,
            now,
            []);

        try
        {
            var accepted = await repository.AcceptAsync(candidate, cancellationToken);
            if (!accepted.Created)
            {
                return ContractResult<ReprocessDocumentResult>.Succeeded(request, ToReprocessResult(accepted.Record));
            }

            var started = await repository.StartAttemptAsync(
                payload.ProcessingId,
                ProcessingState.Accepted,
                now,
                cancellationToken);
            var execution = await ExecuteAsync(request, started, cancellationToken);
            return new ContractResult<ReprocessDocumentResult>(
                execution.ContractName,
                execution.ContractVersion,
                execution.RequestId,
                execution.CorrelationId,
                execution.Outcome,
                execution.Data is null ? null : ToReprocessResult(execution.Data, payload.PreviousProcessingId),
                execution.Error);
        }
        catch (ProcessingConflictException)
        {
            return ContractResult<ReprocessDocumentResult>.Rejected(
                request, "reprocessing.state.conflict", ContractErrorCategory.Conflict,
                "The processing identity, idempotency key, request payload, or state conflicts.");
        }
    }

    public async Task<ContractResult<ProcessingStatus>> GetProcessingStatusAsync(
        ContractRequest<GetProcessingStatus> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, Vs02ContractNames.GetProcessingStatus);
        if (contextError is not null)
        {
            return Reject<ProcessingStatus, GetProcessingStatus>(request, contextError);
        }

        var payload = request.Payload;
        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.ProcessingId) ||
            string.IsNullOrWhiteSpace(payload.CustomerId))
        {
            return ContractResult<ProcessingStatus>.Rejected(
                request, "processing.request.invalid", ContractErrorCategory.ValidationError, "The processing status request is invalid.");
        }

        var record = await repository.GetAsync(payload.ProcessingId, cancellationToken);
        if (record is null ||
            !string.Equals(record.CustomerId, payload.CustomerId, StringComparison.Ordinal) ||
            !string.Equals(record.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal))
        {
            return ContractResult<ProcessingStatus>.Rejected(
                request, "processing.not-found", ContractErrorCategory.NotFound, "Authorized processing status was not found.");
        }

        var policyError = await executionPolicy.EvaluateAsync(
            request.Security,
            record.CustomerId,
            request.CorrelationId,
            cancellationToken);
        if (policyError is not null)
        {
            return Reject<ProcessingStatus, GetProcessingStatus>(request, policyError);
        }

        return ContractResult<ProcessingStatus>.Succeeded(request, ToStatus(record));
    }

    private async Task<ContractResult<ProcessingStatus>> ExecuteAsync<TPayload>(
        ContractRequest<TPayload> request,
        ProcessingRecord started,
        CancellationToken cancellationToken)
    {
        var policyError = await executionPolicy.EvaluateAsync(
            request.Security,
            started.CustomerId,
            request.CorrelationId,
            cancellationToken);
        if (policyError is not null)
        {
            return await FailAsync(request, started, policyError.Code, policyError.Category, policyError.Retryable, cancellationToken);
        }

        EvidenceContent? evidence;
        try
        {
            evidence = await contentReader.ReadAsync(
                started.DocumentVersionId,
                started.CustomerId,
                request.Security,
                request.CorrelationId,
                cancellationToken);
        }
        catch (EvidenceDependencyException)
        {
            return await FailAsync(
                request, started, "processing.evidence.dependency-unavailable",
                ContractErrorCategory.DependencyFailure, true, cancellationToken);
        }

        if (!MatchesRequestedSource(
                evidence,
                started.CustomerId,
                started.EvidenceReferenceId,
                started.DocumentVersionId))
        {
            return await FailAsync(
                request, started, "processing.evidence.unavailable",
                ContractErrorCategory.DependencyFailure, true, cancellationToken);
        }

        policyError = await executionPolicy.EvaluateAsync(
            request.Security,
            started.CustomerId,
            request.CorrelationId,
            cancellationToken);
        if (policyError is not null)
        {
            return await FailAsync(request, started, policyError.Code, policyError.Category, policyError.Retryable, cancellationToken);
        }

        IReadOnlyList<ValidatedSourceFact> facts;
        try
        {
            facts = await extractor.ExtractAsync(evidence!, started.ProcessingId, cancellationToken);
        }
        catch (DocumentExtractionException)
        {
            return await FailAsync(
                request, started, "processing.extraction.failed",
                ContractErrorCategory.ProcessingFailed, false, cancellationToken);
        }

        if (facts.Count == 0)
        {
            return await FailAsync(
                request, started, "processing.no-valid-facts",
                ContractErrorCategory.ProcessingFailed, false, cancellationToken);
        }

        var completedAt = timeProvider.GetUtcNow();
        var produced = new DomainEvent<ValidatedSourceFactsProducedPayload>(
            "CID-028",
            $"evt-facts-{started.ProcessingId}-{started.Attempt}",
            Vs02ContractNames.ValidatedSourceFactsProduced,
            ContractGuard.CurrentVersion,
            completedAt,
            request.CorrelationId,
            request.RequestId,
            "Document Intelligence Service",
            "document-processing",
            started.ProcessingId,
            new ValidatedSourceFactsProducedPayload(
                started.ProcessingId,
                started.CustomerId,
                evidence!.Reference.EvidenceId,
                started.DocumentVersionId,
                facts));
        var completed = await repository.CommitTerminalAsync(
            started.ProcessingId,
            RequiredAttemptId(started),
            ProcessingState.Completed,
            completedAt,
            facts,
            null,
            null,
            false,
            produced,
            cancellationToken);

        Record(request, "COMPLETED", started.ProcessingId);
        return ContractResult<ProcessingStatus>.Succeeded(request, ToStatus(completed));
    }

    private async Task<ContractResult<ProcessingStatus>> FailAsync<TPayload>(
        ContractRequest<TPayload> request,
        ProcessingRecord started,
        string failureCode,
        ContractErrorCategory category,
        bool retryable,
        CancellationToken cancellationToken)
    {
        var failedAt = timeProvider.GetUtcNow();
        var attemptId = RequiredAttemptId(started);
        var failedEvent = new DomainEvent<DocumentProcessingFailedPayload>(
            "CID-029",
            $"evt-processing-failed-{started.ProcessingId}-{started.Attempt}",
            D12ContractNames.DocumentProcessingFailed,
            ContractGuard.CurrentVersion,
            failedAt,
            request.CorrelationId,
            request.RequestId,
            "Document Intelligence Service",
            "document-processing",
            started.ProcessingId,
            new DocumentProcessingFailedPayload(
                started.ProcessingId,
                started.CustomerId,
                started.EvidenceReferenceId,
                started.DocumentVersionId,
                failureCode,
                category,
                retryable,
                started.PreviousProcessingId,
                attemptId));
        var failed = await repository.CommitTerminalAsync(
            started.ProcessingId,
            attemptId,
            ProcessingState.Failed,
            failedAt,
            null,
            failureCode,
            category,
            retryable,
            failedEvent,
            cancellationToken);

        Record(request, "FAILED", started.ProcessingId);
        return ContractResult<ProcessingStatus>.Failed(
            request, failureCode, category, SafeFailureMessage(category), retryable) with
        { Data = ToStatus(failed) };
    }

    private static ContractError? ValidateEligibility(
        ReprocessDocument request,
        ProcessingRecord predecessor,
        string correlationId)
    {
        if (predecessor.State is not (ProcessingState.Completed or ProcessingState.Failed))
        {
            return new ContractError(
                "reprocessing.predecessor.not-terminal",
                ContractErrorCategory.PreconditionFailed,
                "The predecessor must be terminal before reprocessing.",
                false,
                correlationId);
        }

        if (!string.Equals(predecessor.EvidenceReferenceId, request.EvidenceReferenceId, StringComparison.Ordinal))
        {
            return new ContractError(
                "reprocessing.evidence.identity-mismatch",
                ContractErrorCategory.PreconditionFailed,
                "The requested evidence identity does not match the predecessor.",
                false,
                correlationId);
        }

        var eligible = request.Reason switch
        {
            ReprocessingReason.FailedProcessing =>
                predecessor.State == ProcessingState.Failed &&
                string.Equals(predecessor.DocumentVersionId, request.DocumentVersionId, StringComparison.Ordinal),
            ReprocessingReason.UpdatedEvidence =>
                !string.Equals(predecessor.DocumentVersionId, request.DocumentVersionId, StringComparison.Ordinal),
            _ => false,
        };
        return eligible
            ? null
            : new ContractError(
                "reprocessing.reason.ineligible",
                ContractErrorCategory.PreconditionFailed,
                "The predecessor and immutable document version are not eligible for the requested reason.",
                false,
                correlationId);
    }

    private static bool MatchesRequestedSource(
        EvidenceContent? evidence,
        string customerId,
        string evidenceId,
        string documentVersionId) =>
        evidence is not null &&
        string.Equals(evidence.Reference.CustomerId, customerId, StringComparison.Ordinal) &&
        string.Equals(evidence.Reference.EvidenceId, evidenceId, StringComparison.Ordinal) &&
        string.Equals(evidence.Reference.DocumentVersionId, documentVersionId, StringComparison.Ordinal);

    private static bool HasCompleteD12Security(TrustedSecurityContext security) =>
        !string.IsNullOrWhiteSpace(security.Actor.ActorType) &&
        !string.IsNullOrWhiteSpace(security.Workload.WorkloadId);

    private void Record<TPayload>(ContractRequest<TPayload> request, string outcome, string processingId) =>
        telemetry.Record(new LifecycleSignal(
            "Document Intelligence Service",
            request.ContractName,
            outcome,
            request.RequestId,
            request.CorrelationId,
            request.CausationId,
            "document-processing",
            processingId,
            "FIXTURE"));

    private static string Fingerprint<TPayload>(TPayload payload) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options)))
            .ToLowerInvariant();

    private static string RequiredAttemptId(ProcessingRecord record) =>
        record.ActiveAttemptId ?? throw new InvalidOperationException("The processing attempt identity is unavailable.");

    private static ProcessingPredecessorSnapshot Snapshot(ProcessingRecord record) =>
        new(
            record.ProcessingId,
            record.DocumentVersionId,
            record.EvidenceReferenceId,
            record.State,
            record.FailureCode,
            record.Retryable,
            record.UpdatedAt,
            record.Facts.Count,
            record.TerminalEventId);

    private static string SafeFailureMessage(ContractErrorCategory category) => category switch
    {
        ContractErrorCategory.DependencyFailure => "Evidence content is unavailable.",
        ContractErrorCategory.AccessDenied => "Current execution authorization was denied.",
        ContractErrorCategory.ConsentRequired => "Current consent evidence is required.",
        ContractErrorCategory.ConsentExpired => "Consent expired before execution.",
        ContractErrorCategory.ConsentRevoked => "Consent was revoked before execution.",
        _ => "Document processing did not produce a valid outcome.",
    };

    private static ProcessingStatus ToStatus(ProcessingRecord record) =>
        new(record.ProcessingId, record.DocumentVersionId, record.State, record.FailureCode, record.Retryable, record.UpdatedAt);

    private static ReprocessDocumentResult ToReprocessResult(ProcessingRecord record, string? previousProcessingId = null) =>
        new(
            record.ProcessingId,
            previousProcessingId ?? record.PreviousProcessingId ?? string.Empty,
            record.State,
            record.FailureCode,
            record.Retryable,
            record.UpdatedAt);

    private static ReprocessDocumentResult ToReprocessResult(ProcessingStatus status, string previousProcessingId) =>
        new(
            status.ProcessingId,
            previousProcessingId,
            status.State,
            status.FailureCode,
            status.Retryable,
            status.UpdatedAt);

    private static ContractResult<TData> Reject<TData, TPayload>(
        ContractRequest<TPayload> request,
        ContractError error) =>
        new(request.ContractName, request.ContractVersion, request.RequestId, request.CorrelationId, ContractOutcome.Rejected, default, error);
}
