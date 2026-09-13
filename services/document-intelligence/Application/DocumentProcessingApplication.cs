using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.Services.DocumentIntelligence.Application;

public sealed record EvidenceContent(EvidenceReference Reference, string Content);

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

public sealed record ProcessingRecord(
    string ProcessingId,
    string DocumentVersionId,
    string EvidenceReferenceId,
    string CustomerId,
    string IdempotencyKey,
    ProcessingState State,
    string? FailureCode,
    bool Retryable,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ValidatedSourceFact> Facts);

public interface IDocumentProcessingRepository
{
    Task<(ProcessingRecord Record, bool Created)> AcceptAsync(
        ProcessingRecord candidate,
        CancellationToken cancellationToken);

    Task<ProcessingRecord> TransitionAsync(
        string processingId,
        ProcessingState expected,
        ProcessingState target,
        DateTimeOffset updatedAt,
        IReadOnlyList<ValidatedSourceFact>? facts,
        string? failureCode,
        bool retryable,
        object? outboxEvent,
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
            request.IdempotencyKey,
            ProcessingState.Accepted,
            null,
            false,
            now,
            []);

        try
        {
            var accepted = await repository.AcceptAsync(candidate, cancellationToken);
            if (!accepted.Created)
            {
                if (accepted.Record.State != ProcessingState.Failed || !accepted.Record.Retryable)
                {
                    return ContractResult<ProcessingStatus>.Succeeded(request, ToStatus(accepted.Record));
                }

                await repository.TransitionAsync(
                    payload.ProcessingId,
                    ProcessingState.Failed,
                    ProcessingState.Running,
                    now,
                    null,
                    null,
                    false,
                    null,
                    cancellationToken);
            }
            else
            {
                await repository.TransitionAsync(
                    payload.ProcessingId,
                    ProcessingState.Accepted,
                    ProcessingState.Running,
                    now,
                    null,
                    null,
                    false,
                    null,
                    cancellationToken);
            }

            var evidence = await contentReader.ReadAsync(
                payload.DocumentVersionId,
                payload.CustomerId,
                request.Security,
                request.CorrelationId,
                cancellationToken);
            if (evidence is null || !string.Equals(evidence.Reference.EvidenceId, payload.EvidenceReferenceId, StringComparison.Ordinal))
            {
                var failed = await FailAsync(request, "processing.evidence.unavailable", true, cancellationToken);
                return ContractResult<ProcessingStatus>.Failed(
                    request, "processing.evidence.unavailable", ContractErrorCategory.DependencyFailure, "Evidence content is unavailable.", true) with
                { Data = failed };
            }

            var facts = await extractor.ExtractAsync(evidence, payload.ProcessingId, cancellationToken);
            if (facts.Count == 0)
            {
                var failed = await FailAsync(request, "processing.no-valid-facts", false, cancellationToken);
                return ContractResult<ProcessingStatus>.Failed(
                    request, "processing.no-valid-facts", ContractErrorCategory.ProcessingFailed, "No validated source facts were produced.", false) with
                { Data = failed };
            }

            var completedAt = timeProvider.GetUtcNow();
            var produced = new DomainEvent<ValidatedSourceFactsProducedPayload>(
                "CID-028",
                $"evt-facts-{payload.ProcessingId}",
                Vs02ContractNames.ValidatedSourceFactsProduced,
                ContractGuard.CurrentVersion,
                completedAt,
                request.CorrelationId,
                request.RequestId,
                "Document Intelligence Service",
                "document-processing",
                payload.ProcessingId,
                new ValidatedSourceFactsProducedPayload(
                    payload.ProcessingId,
                    payload.CustomerId,
                    evidence.Reference.EvidenceId,
                    payload.DocumentVersionId,
                    facts));
            var completed = await repository.TransitionAsync(
                payload.ProcessingId,
                ProcessingState.Running,
                ProcessingState.Completed,
                completedAt,
                facts,
                null,
                false,
                produced,
                cancellationToken);

            telemetry.Record(new LifecycleSignal(
                "Document Intelligence Service",
                Vs02ContractNames.ProcessDocument,
                "COMPLETED",
                request.RequestId,
                request.CorrelationId,
                request.CausationId,
                "document-processing",
                payload.ProcessingId,
                "FIXTURE"));
            return ContractResult<ProcessingStatus>.Succeeded(request, ToStatus(completed));
        }
        catch (InvalidOperationException)
        {
            return ContractResult<ProcessingStatus>.Rejected(
                request, "processing.state.conflict", ContractErrorCategory.Conflict, "The processing state transition is invalid.");
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

        var record = await repository.GetAsync(request.Payload.ProcessingId, cancellationToken);
        if (record is null ||
            !string.Equals(record.CustomerId, request.Payload.CustomerId, StringComparison.Ordinal) ||
            !string.Equals(record.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal))
        {
            return ContractResult<ProcessingStatus>.Rejected(
                request, "processing.not-found", ContractErrorCategory.NotFound, "Authorized processing status was not found.");
        }

        return ContractResult<ProcessingStatus>.Succeeded(request, ToStatus(record));
    }

    private async Task<ProcessingStatus> FailAsync(
        ContractRequest<ProcessDocument> request,
        string failureCode,
        bool retryable,
        CancellationToken cancellationToken)
    {
        var failed = await repository.TransitionAsync(
            request.Payload.ProcessingId,
            ProcessingState.Running,
            ProcessingState.Failed,
            timeProvider.GetUtcNow(),
            null,
            failureCode,
            retryable,
            null,
            cancellationToken);
        telemetry.Record(new LifecycleSignal(
            "Document Intelligence Service",
            Vs02ContractNames.ProcessDocument,
            "FAILED",
            request.RequestId,
            request.CorrelationId,
            request.CausationId,
            "document-processing",
            request.Payload.ProcessingId,
            "FIXTURE"));
        return ToStatus(failed);
    }

    private static ProcessingStatus ToStatus(ProcessingRecord record) =>
        new(record.ProcessingId, record.DocumentVersionId, record.State, record.FailureCode, record.Retryable, record.UpdatedAt);

    private static ContractResult<TData> Reject<TData, TPayload>(
        ContractRequest<TPayload> request,
        ContractError error) =>
        new(request.ContractName, request.ContractVersion, request.RequestId, request.CorrelationId, ContractOutcome.Rejected, default, error);
}
