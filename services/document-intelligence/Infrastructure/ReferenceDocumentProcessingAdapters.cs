using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

public sealed class InMemoryDocumentProcessingRepository : IDocumentProcessingRepository
{
    private readonly object sync = new();
    private readonly Dictionary<string, ProcessingRecord> records = new(StringComparer.Ordinal);
    private readonly Dictionary<IdempotencyIdentity, string> idempotency = [];
    private readonly List<object> outbox = [];
    private readonly List<object> eventHistory = [];
    private bool failNextTerminalCommit;

    public Task<(ProcessingRecord Record, bool Created)> AcceptAsync(
        ProcessingRecord candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var identity = ToIdempotencyIdentity(candidate);
            if (idempotency.TryGetValue(identity, out var existingId))
            {
                var existing = records[existingId];
                if (!string.Equals(existing.PayloadFingerprint, candidate.PayloadFingerprint, StringComparison.Ordinal))
                {
                    throw new ProcessingConflictException("Idempotency key conflict.");
                }

                return Task.FromResult((existing, false));
            }

            if (records.ContainsKey(candidate.ProcessingId))
            {
                throw new ProcessingConflictException("Processing identity exists.");
            }

            if (candidate.PredecessorSnapshot is not null &&
                (!records.TryGetValue(candidate.PreviousProcessingId!, out var currentPredecessor) ||
                 !MatchesSnapshot(currentPredecessor, candidate.PredecessorSnapshot)))
            {
                throw new ProcessingConflictException("The predecessor changed before reprocessing admission.");
            }

            var frozenCandidate = FreezeRecord(candidate);
            records.Add(candidate.ProcessingId, frozenCandidate);
            idempotency.Add(identity, candidate.ProcessingId);
            return Task.FromResult((frozenCandidate, true));
        }
    }

    public Task<ProcessingRecord?> GetByIdempotencyAsync(
        string contractName,
        string contractVersion,
        string customerId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var identity = new IdempotencyIdentity(contractName, contractVersion, customerId, idempotencyKey);
            return Task.FromResult(idempotency.TryGetValue(identity, out var processingId)
                ? records[processingId]
                : null);
        }
    }

    public Task<ProcessingRecord> StartAttemptAsync(
        string processingId,
        ProcessingState expected,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!records.TryGetValue(processingId, out var current) ||
                current.State != expected ||
                expected is not (ProcessingState.Accepted or ProcessingState.Failed) ||
                (expected == ProcessingState.Failed && !current.Retryable))
            {
                throw new ProcessingConflictException("Invalid processing attempt start.");
            }

            var attempt = current.Attempt + 1;
            var next = current with
            {
                State = ProcessingState.Running,
                Attempt = attempt,
                ActiveAttemptId = $"{processingId}:attempt:{attempt}",
                UpdatedAt = startedAt,
                FailureCode = null,
                FailureCategory = null,
                Retryable = false,
            };
            records[processingId] = next;
            return Task.FromResult(next);
        }
    }

    public Task<ProcessingRecord> CommitTerminalAsync(
        string processingId,
        string attemptId,
        ProcessingState target,
        DateTimeOffset updatedAt,
        IReadOnlyList<ValidatedSourceFact>? facts,
        string? failureCode,
        ContractErrorCategory? failureCategory,
        bool retryable,
        object outboxEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!records.TryGetValue(processingId, out var current) ||
                current.State != ProcessingState.Running ||
                !string.Equals(current.ActiveAttemptId, attemptId, StringComparison.Ordinal) ||
                target is not (ProcessingState.Completed or ProcessingState.Failed))
            {
                throw new ProcessingConflictException("Invalid or stale terminal processing attempt.");
            }

            if (failNextTerminalCommit)
            {
                failNextTerminalCommit = false;
                throw new ProcessingAtomicCommitException("Injected atomic terminal commit failure.");
            }

            var frozenFacts = FreezeFacts(facts ?? current.Facts);
            var frozenEvent = FreezeEvent(outboxEvent);
            var next = current with
            {
                State = target,
                UpdatedAt = updatedAt,
                Facts = frozenFacts,
                FailureCode = failureCode,
                FailureCategory = failureCategory,
                Retryable = retryable,
                ActiveAttemptId = null,
                TerminalEventId = EventId(frozenEvent),
            };
            records[processingId] = next;
            outbox.Add(frozenEvent);
            eventHistory.Add(frozenEvent);

            return Task.FromResult(next);
        }
    }

    public Task<ProcessingRecord?> GetAsync(string processingId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            records.TryGetValue(processingId, out var record);
            return Task.FromResult(record);
        }
    }

    public void FailNextTerminalCommit()
    {
        lock (sync)
        {
            failNextTerminalCommit = true;
        }
    }

    public IReadOnlyList<object> OutboxSnapshot()
    {
        lock (sync)
        {
            return outbox.ToArray();
        }
    }

    public IReadOnlyList<object> EventHistorySnapshot()
    {
        lock (sync)
        {
            return eventHistory.ToArray();
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

    private static IdempotencyIdentity ToIdempotencyIdentity(ProcessingRecord candidate) =>
        new(
            candidate.ContractName,
            candidate.ContractVersion,
            candidate.CustomerId,
            candidate.IdempotencyKey);

    private readonly record struct IdempotencyIdentity(
        string ContractName,
        string ContractVersion,
        string CustomerId,
        string IdempotencyKey);

    private static bool MatchesSnapshot(ProcessingRecord current, ProcessingPredecessorSnapshot expected) =>
        string.Equals(current.ProcessingId, expected.ProcessingId, StringComparison.Ordinal) &&
        string.Equals(current.DocumentVersionId, expected.DocumentVersionId, StringComparison.Ordinal) &&
        string.Equals(current.EvidenceReferenceId, expected.EvidenceReferenceId, StringComparison.Ordinal) &&
        current.State == expected.State &&
        string.Equals(current.FailureCode, expected.FailureCode, StringComparison.Ordinal) &&
        current.Retryable == expected.Retryable &&
        current.UpdatedAt == expected.UpdatedAt &&
        current.Facts.Count == expected.FactCount &&
        string.Equals(current.TerminalEventId, expected.TerminalEventId, StringComparison.Ordinal);

    private static ProcessingRecord FreezeRecord(ProcessingRecord record) =>
        record with { Facts = FreezeFacts(record.Facts) };

    private static System.Collections.ObjectModel.ReadOnlyCollection<ValidatedSourceFact> FreezeFacts(
        IEnumerable<ValidatedSourceFact> facts) =>
        Array.AsReadOnly(facts.ToArray());

    private static object FreezeEvent(object value) => value switch
    {
        DomainEvent<ValidatedSourceFactsProducedPayload> success => success with
        {
            Payload = success.Payload with { Facts = FreezeFacts(success.Payload.Facts) },
        },
        DomainEvent<DocumentProcessingFailedPayload> failure => failure,
        _ => throw new InvalidOperationException("Unsupported processing terminal event type."),
    };

    private static string EventId(object value) => value switch
    {
        DomainEvent<ValidatedSourceFactsProducedPayload> success => success.EventId,
        DomainEvent<DocumentProcessingFailedPayload> failure => failure.EventId,
        _ => throw new InvalidOperationException("Unsupported processing terminal event type."),
    };
}

public sealed class FixtureDocumentExtractor : IDocumentExtractor
{
    public const string ExtractionVersion = "fixture-extractor/1.0.0";
    public const string ValidationVersion = "canonical-source-fact/1.0.0";

    public Task<IReadOnlyList<ValidatedSourceFact>> ExtractAsync(
        EvidenceContent content,
        string processingId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var facts = new List<ValidatedSourceFact>();
        var lines = content.Content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < lines.Length; index++)
        {
            var fields = lines[index].Split('|', StringSplitOptions.TrimEntries);
            if (fields.Length != 7 ||
                !IsAllowedFactType(fields[0]) ||
                string.IsNullOrWhiteSpace(fields[1]) ||
                !decimal.TryParse(fields[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ||
                value < 0 ||
                fields[3].Length != 3 ||
                !DateOnly.TryParseExact(fields[4], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var effectiveDate) ||
                string.IsNullOrWhiteSpace(fields[5]) ||
                !decimal.TryParse(fields[6], NumberStyles.Number, CultureInfo.InvariantCulture, out var confidence) ||
                confidence is < 0 or > 1)
            {
                continue;
            }

            var sourceFactId = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes($"{content.Reference.DocumentVersionId}|{processingId}|{index}")))
                .ToLowerInvariant();
            facts.Add(new ValidatedSourceFact(
                $"sf-{sourceFactId[..24]}",
                content.Reference.CustomerId,
                fields[0].ToUpperInvariant(),
                fields[1],
                value,
                fields[3].ToUpperInvariant(),
                effectiveDate,
                fields[5],
                confidence,
                content.Reference.EvidenceId,
                content.Reference.DocumentVersionId,
                ExtractionVersion,
                ValidationVersion));
        }

        return Task.FromResult<IReadOnlyList<ValidatedSourceFact>>(facts);
    }

    private static bool IsAllowedFactType(string value) =>
        string.Equals(value, "INCOME", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "EXPENSE", StringComparison.OrdinalIgnoreCase);
}

public sealed class ReferenceExecutionPolicy : IExecutionPolicy
{
    private readonly HashSet<string> revokedConsents = new(StringComparer.Ordinal);

    public void Revoke(string consentReferenceId) => revokedConsents.Add(consentReferenceId);

    public Task<ContractError?> EvaluateAsync(
        TrustedSecurityContext security,
        string customerId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ContractError? error = null;
        if (!string.Equals(security.Access.CustomerId, customerId, StringComparison.Ordinal))
        {
            error = new ContractError("security.customer.denied", ContractErrorCategory.AccessDenied, "Customer access is denied.", false, correlationId);
        }
        else if (string.IsNullOrWhiteSpace(security.Access.ConsentReferenceId))
        {
            error = new ContractError("consent.required", ContractErrorCategory.ConsentRequired, "Current consent evidence is required.", false, correlationId);
        }
        else if (revokedConsents.Contains(security.Access.ConsentReferenceId))
        {
            error = new ContractError("consent.revoked", ContractErrorCategory.ConsentRevoked, "Consent was revoked before execution.", false, correlationId);
        }

        return Task.FromResult(error);
    }
}
