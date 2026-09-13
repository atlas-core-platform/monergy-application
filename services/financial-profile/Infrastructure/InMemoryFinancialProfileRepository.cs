using System.Security.Cryptography;
using System.Text;
using Monergy.Contracts;
using Monergy.Services.FinancialProfile.Application;

namespace Monergy.Services.FinancialProfile.Infrastructure;

public sealed class InMemoryFinancialProfileRepository : IFinancialProfileRepository
{
    private readonly object sync = new();
    private readonly Dictionary<string, FinancialFactRecord> facts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FinancialProvenance> provenance = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FinancialNormalizationSave> idempotency = new(StringComparer.Ordinal);
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
            if (idempotency.TryGetValue(idempotencyKey, out var prior))
            {
                if (!SameRequest(prior, request.Payload))
                {
                    throw new InvalidOperationException("Idempotency key conflict.");
                }

                return Task.FromResult(prior with { Created = false });
            }

            var savedFacts = new List<FinancialFactRecord>();
            var savedProvenance = new List<FinancialProvenance>();
            var events = new List<object>();
            foreach (var source in request.Payload.Facts)
            {
                var key = $"{request.Payload.FinancialProfileId}|{source.FactType}|{source.Label}";
                var exists = facts.TryGetValue(key, out var current);
                var revision = exists ? current!.Revision + 1 : 1;
                var factId = exists ? current!.FinancialFactId : StableId("ff", key);
                var provenanceId = StableId("fpv", $"{factId}|{source.SourceFactId}|{request.Payload.NormalizationVersion}");
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
                facts[key] = fact;
                provenance.Add(provenanceId, lineage);
                savedFacts.Add(fact);
                savedProvenance.Add(lineage);

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

            outbox.AddRange(events);
            var result = new FinancialNormalizationSave(savedFacts, savedProvenance, events, true);
            idempotency.Add(idempotencyKey, result);
            return Task.FromResult(result);
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

    private static bool SameRequest(FinancialNormalizationSave prior, NormalizeSourceFacts current) =>
        prior.Facts.Count == current.Facts.Count &&
        prior.Facts.All(fact => current.Facts.Any(source =>
            string.Equals(source.SourceFactId, fact.SourceFactId, StringComparison.Ordinal) &&
            string.Equals(source.CustomerId, fact.CustomerId, StringComparison.Ordinal)));

    private static string StableId(string prefix, string value)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return $"{prefix}-{digest[..24]}";
    }
}
