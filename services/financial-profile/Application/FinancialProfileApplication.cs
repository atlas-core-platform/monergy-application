using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.Services.FinancialProfile.Application;

public sealed record FinancialFactRecord(
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
    string SourceFactId);

public sealed record FinancialNormalizationSave(
    IReadOnlyList<FinancialFactRecord> Facts,
    IReadOnlyList<FinancialProvenance> Provenance,
    IReadOnlyList<object> Events,
    bool Created);

public interface IFinancialProfileRepository
{
    Task<FinancialNormalizationSave> NormalizeAsync(
        string idempotencyKey,
        ContractRequest<NormalizeSourceFacts> request,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken);

    Task<FinancialProvenance?> GetProvenanceAsync(
        string provenanceId,
        CancellationToken cancellationToken);
}

public sealed class FinancialProfileApplication(
    IFinancialProfileRepository repository,
    ILifecycleTelemetry telemetry,
    TimeProvider timeProvider)
{
    private static readonly HashSet<string> AllowedFactTypes =
        new(StringComparer.Ordinal) { "INCOME", "EXPENSE" };

    public async Task<ContractResult<NormalizationResult>> NormalizeSourceFactsAsync(
        ContractRequest<NormalizeSourceFacts> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, Vs02ContractNames.NormalizeSourceFacts);
        if (contextError is not null)
        {
            return Reject<NormalizationResult, NormalizeSourceFacts>(request, contextError);
        }

        var payload = request.Payload;
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(payload.FinancialProfileId) ||
            string.IsNullOrWhiteSpace(payload.ProcessingId) ||
            string.IsNullOrWhiteSpace(payload.NormalizationVersion) ||
            payload.Facts.Count == 0 ||
            !string.Equals(payload.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal) ||
            payload.Facts.Any(fact => !Valid(fact, payload.CustomerId)))
        {
            return ContractResult<NormalizationResult>.Rejected(
                request, "financial.normalization.invalid", ContractErrorCategory.ValidationError, "Validated source facts are invalid.");
        }

        try
        {
            var saved = await repository.NormalizeAsync(
                request.IdempotencyKey,
                request,
                timeProvider.GetUtcNow(),
                cancellationToken);
            var normalized = saved.Facts.Select(fact => new NormalizedFinancialFact(
                fact.FinancialFactId,
                fact.FinancialProfileId,
                fact.CustomerId,
                fact.FactType,
                fact.Label,
                fact.Value,
                fact.Currency,
                fact.EffectiveDate,
                fact.Revision,
                fact.FinancialProvenanceId,
                saved.Created)).ToArray();
            telemetry.Record(new LifecycleSignal(
                "Financial Profile Service",
                Vs02ContractNames.NormalizeSourceFacts,
                saved.Created ? "PROMOTED" : "REPLAYED",
                request.RequestId,
                request.CorrelationId,
                request.CausationId,
                "financial-profile",
                payload.FinancialProfileId,
                "IN_MEMORY_REFERENCE"));
            return ContractResult<NormalizationResult>.Succeeded(request, new NormalizationResult(normalized));
        }
        catch (InvalidOperationException)
        {
            return ContractResult<NormalizationResult>.Rejected(
                request, "financial.normalization.conflict", ContractErrorCategory.Conflict, "Normalization conflicts with current authoritative state.");
        }
    }

    public async Task<ContractResult<FinancialProvenance>> GetFinancialProvenanceAsync(
        ContractRequest<GetFinancialProvenance> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, Vs02ContractNames.GetFinancialProvenance);
        if (contextError is not null)
        {
            return Reject<FinancialProvenance, GetFinancialProvenance>(request, contextError);
        }

        var provenance = await repository.GetProvenanceAsync(request.Payload.FinancialProvenanceId, cancellationToken);
        if (provenance is null ||
            !string.Equals(provenance.CustomerId, request.Payload.CustomerId, StringComparison.Ordinal) ||
            !string.Equals(provenance.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal))
        {
            return ContractResult<FinancialProvenance>.Rejected(
                request, "financial.provenance.not-found", ContractErrorCategory.NotFound, "Authorized financial provenance was not found.");
        }

        return ContractResult<FinancialProvenance>.Succeeded(request, provenance);
    }

    private static bool Valid(ValidatedSourceFact fact, string customerId) =>
        string.Equals(fact.CustomerId, customerId, StringComparison.Ordinal) &&
        AllowedFactTypes.Contains(fact.FactType) &&
        !string.IsNullOrWhiteSpace(fact.Label) &&
        fact.CandidateValue >= 0 &&
        fact.Currency.Length == 3 &&
        fact.Confidence is >= 0 and <= 1 &&
        !string.IsNullOrWhiteSpace(fact.EvidenceId) &&
        !string.IsNullOrWhiteSpace(fact.DocumentVersionId) &&
        !string.IsNullOrWhiteSpace(fact.ExtractionVersion) &&
        !string.IsNullOrWhiteSpace(fact.ValidationVersion);

    private static ContractResult<TData> Reject<TData, TPayload>(
        ContractRequest<TPayload> request,
        ContractError error) =>
        new(request.ContractName, request.ContractVersion, request.RequestId, request.CorrelationId, ContractOutcome.Rejected, default, error);
}
