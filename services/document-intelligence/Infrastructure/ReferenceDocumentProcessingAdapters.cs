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
    private readonly Dictionary<string, string> idempotency = new(StringComparer.Ordinal);
    private readonly List<object> outbox = [];

    public Task<(ProcessingRecord Record, bool Created)> AcceptAsync(
        ProcessingRecord candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (idempotency.TryGetValue(candidate.IdempotencyKey, out var existingId))
            {
                var existing = records[existingId];
                if (!string.Equals(existing.DocumentVersionId, candidate.DocumentVersionId, StringComparison.Ordinal) ||
                    !string.Equals(existing.CustomerId, candidate.CustomerId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Idempotency key conflict.");
                }

                return Task.FromResult((existing, false));
            }

            if (records.ContainsKey(candidate.ProcessingId))
            {
                throw new InvalidOperationException("Processing identity exists.");
            }

            records.Add(candidate.ProcessingId, candidate);
            idempotency.Add(candidate.IdempotencyKey, candidate.ProcessingId);
            return Task.FromResult((candidate, true));
        }
    }

    public Task<ProcessingRecord> TransitionAsync(
        string processingId,
        ProcessingState expected,
        ProcessingState target,
        DateTimeOffset updatedAt,
        IReadOnlyList<ValidatedSourceFact>? facts,
        string? failureCode,
        bool retryable,
        object? outboxEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!records.TryGetValue(processingId, out var current) || current.State != expected || !Allowed(expected, target))
            {
                throw new InvalidOperationException("Invalid processing state transition.");
            }

            var next = current with
            {
                State = target,
                UpdatedAt = updatedAt,
                Facts = facts ?? current.Facts,
                FailureCode = failureCode,
                Retryable = retryable,
            };
            records[processingId] = next;
            if (outboxEvent is not null)
            {
                outbox.Add(outboxEvent);
            }

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

    public IReadOnlyList<object> DrainOutbox()
    {
        lock (sync)
        {
            var drained = outbox.ToArray();
            outbox.Clear();
            return drained;
        }
    }

    private static bool Allowed(ProcessingState current, ProcessingState target) =>
        (current, target) is
            (ProcessingState.Accepted, ProcessingState.Running) or
            (ProcessingState.Failed, ProcessingState.Running) or
            (ProcessingState.Running, ProcessingState.Completed) or
            (ProcessingState.Running, ProcessingState.Failed);
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
