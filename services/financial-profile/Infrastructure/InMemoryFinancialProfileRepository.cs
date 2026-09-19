using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Services.FinancialProfile.Application;

namespace Monergy.Services.FinancialProfile.Infrastructure;

public sealed class InMemoryFinancialProfileRepository : IFinancialProfileRepository
{
    private readonly object sync = new();
    private readonly Dictionary<string, FinancialFactRecord> factsByBusinessKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FinancialFactRecord> factsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FinancialFactRecord>> factHistory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FinancialProfileRecord> profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FinancialProvenance> provenance = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FinancialNormalizationSave> idempotency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idempotencyPayloads = new(StringComparer.Ordinal);
    private readonly List<object> outbox = [];

    public Task<FinancialNormalizationSave> NormalizeAsync(
        string idempotencyKey,
        ContractRequest<NormalizeSourceFacts> request,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var payloadSignature = JsonSerializer.Serialize(request.Payload, ContractJson.Options);
            if (idempotency.TryGetValue(idempotencyKey, out var prior))
            {
                if (!string.Equals(idempotencyPayloads[idempotencyKey], payloadSignature, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Idempotency key conflict.");
                }

                return Task.FromResult(prior with { Persisted = false });
            }

            if (profiles.TryGetValue(request.Payload.FinancialProfileId, out var currentProfile) &&
                !string.Equals(currentProfile.CustomerId, request.Payload.CustomerId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Financial Profile ownership conflict.");
            }

            var savedFacts = new List<FinancialFactRecord>();
            var savedProvenance = new List<FinancialProvenance>();
            var events = new List<object>();
            var createdFactIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in request.Payload.Facts)
            {
                var key = BusinessKey(request.Payload.FinancialProfileId, source.FactType, source.Label);
                var exists = factsByBusinessKey.TryGetValue(key, out var current);
                var revision = exists ? current!.Revision + 1 : 1;
                var factId = exists ? current!.FinancialFactId : StableId("ff", key);
                var provenanceId = StableId(
                    "fpv",
                    $"{factId}|{source.SourceFactId}|{request.Payload.NormalizationVersion}|{revision}");
                var fact = new FinancialFactRecord(
                    factId,
                    request.Payload.FinancialProfileId,
                    request.Payload.CustomerId,
                    source.FactType,
                    source.Label,
                    source.CandidateValue,
                    source.Currency,
                    source.EffectiveDate,
                    revision,
                    provenanceId,
                    source.SourceFactId);
                var lineage = new FinancialProvenance(
                    provenanceId,
                    factId,
                    request.Payload.CustomerId,
                    source.EvidenceId,
                    source.DocumentVersionId,
                    source.SourceFactId,
                    source.ExtractionVersion,
                    source.ValidationVersion,
                    request.Payload.NormalizationVersion,
                    request.Security.Actor.ActorId,
                    request.Security.Workload.WorkloadIdentityId,
                    recordedAt,
                    request.CorrelationId,
                    request.CausationId);
                savedFacts.Add(fact);
                savedProvenance.Add(lineage);
                if (!exists)
                {
                    createdFactIds.Add(factId);
                }

                var contractId = exists ? "CID-035" : "CID-034";
                var eventName = exists ? Vs02ContractNames.FinancialFactUpdated : Vs02ContractNames.FinancialFactCreated;
                events.Add(new DomainEvent<FinancialFactChangedPayload>(
                    contractId,
                    $"evt-financial-{factId}-{revision}",
                    eventName,
                    ContractGuard.CurrentVersion,
                    recordedAt,
                    request.CorrelationId,
                    request.RequestId,
                    "Financial Profile Service",
                    "financial-fact",
                    factId,
                    new FinancialFactChangedPayload(
                        fact.FinancialProfileId,
                        fact.FinancialFactId,
                        fact.FactType,
                        recordedAt,
                        fact.FinancialProvenanceId,
                        revision)));
            }

            var profileRevision = currentProfile?.Revision + 1 ?? 1;
            var replacementIds = savedFacts.Select(fact => fact.FinancialFactId).ToHashSet(StringComparer.Ordinal);
            var currentFacts = factsById.Values
                .Where(fact => string.Equals(fact.FinancialProfileId, request.Payload.FinancialProfileId, StringComparison.Ordinal) &&
                               !replacementIds.Contains(fact.FinancialFactId))
                .Concat(savedFacts)
                .OrderBy(fact => fact.FactType, StringComparer.Ordinal)
                .ThenBy(fact => fact.FinancialFactId, StringComparer.Ordinal)
                .ToArray();
            var profile = new FinancialProfileRecord(
                request.Payload.FinancialProfileId,
                request.Payload.CustomerId,
                profileRevision,
                recordedAt,
                currentFacts);
            events.Add(new DomainEvent<FinancialProfileChangedPayload>(
                "CID-036",
                $"evt-profile-{profile.FinancialProfileId}-{profile.Revision}",
                Vs02ContractNames.FinancialProfileChanged,
                ContractGuard.CurrentVersion,
                recordedAt,
                request.CorrelationId,
                request.RequestId,
                "Financial Profile Service",
                "financial-profile",
                profile.FinancialProfileId,
                new FinancialProfileChangedPayload(
                    profile.FinancialProfileId,
                    profile.CustomerId,
                    recordedAt,
                    profile.Revision,
                    savedFacts.Select(fact => fact.FinancialFactId).Order(StringComparer.Ordinal).ToArray())));

            foreach (var fact in savedFacts)
            {
                var key = BusinessKey(fact.FinancialProfileId, fact.FactType, fact.Label);
                factsByBusinessKey[key] = fact;
                factsById[fact.FinancialFactId] = fact;
                if (!factHistory.TryGetValue(fact.FinancialFactId, out var history))
                {
                    history = [];
                    factHistory.Add(fact.FinancialFactId, history);
                }

                history.Add(fact);
            }

            foreach (var lineage in savedProvenance)
            {
                provenance.Add(lineage.FinancialProvenanceId, lineage);
            }

            profiles[profile.FinancialProfileId] = profile;
            outbox.AddRange(events);
            var result = new FinancialNormalizationSave(savedFacts, savedProvenance, events, profile, createdFactIds, true);
            idempotency.Add(idempotencyKey, result);
            idempotencyPayloads.Add(idempotencyKey, payloadSignature);
            return Task.FromResult(result);
        }
    }

    public Task<FinancialProfileRecord?> GetProfileAsync(string financialProfileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            profiles.TryGetValue(financialProfileId, out var value);
            return Task.FromResult(value);
        }
    }

    public Task<FinancialFactRecord?> GetFactAsync(string financialFactId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            factsById.TryGetValue(financialFactId, out var value);
            return Task.FromResult(value);
        }
    }

    public Task<FinancialProvenance?> GetProvenanceAsync(string provenanceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            provenance.TryGetValue(provenanceId, out var value);
            return Task.FromResult(value);
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

    public IReadOnlyList<FinancialFactRecord> ReadFactHistory(string financialFactId)
    {
        lock (sync)
        {
            return factHistory.TryGetValue(financialFactId, out var history) ? history.ToArray() : [];
        }
    }

    private static string BusinessKey(string profileId, string factType, string label) =>
        $"{profileId}|{factType}|{label}";

    private static string StableId(string prefix, string value)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return $"{prefix}-{digest[..24]}";
    }
}
