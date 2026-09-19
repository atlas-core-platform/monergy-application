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

public sealed record FinancialProfileRecord(
    string FinancialProfileId,
    string CustomerId,
    int Revision,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<FinancialFactRecord> Facts);

public sealed record FinancialNormalizationSave(
    IReadOnlyList<FinancialFactRecord> Facts,
    IReadOnlyList<FinancialProvenance> Provenance,
    IReadOnlyList<object> Events,
    FinancialProfileRecord Profile,
    IReadOnlySet<string> CreatedFactIds,
    bool Persisted);

public sealed record FinancialNormalizationIdentity(
    string ContractName,
    string ContractVersion,
    string CustomerId,
    string IdempotencyKey)
{
    public static FinancialNormalizationIdentity From(ContractRequest<NormalizeSourceFacts> request) =>
        new(
            request.ContractName,
            request.ContractVersion,
            request.Security.Access.CustomerId,
            request.IdempotencyKey!);
}

public sealed record FinancialProfileChangeTransition(
    DateTimeOffset OccurredAt,
    string CorrelationId,
    string CausationId)
{
    public DomainEvent<FinancialProfileChangedPayload> CreateEvent(
        FinancialProfileRecord profile,
        IReadOnlyList<FinancialFactRecord> changedFacts) =>
        new(
            "CID-036",
            $"evt-profile-{profile.FinancialProfileId}-{profile.Revision}",
            Vs02ContractNames.FinancialProfileChanged,
            ContractGuard.CurrentVersion,
            OccurredAt,
            CorrelationId,
            CausationId,
            "Financial Profile Service",
            "financial-profile",
            profile.FinancialProfileId,
            new FinancialProfileChangedPayload(
                profile.FinancialProfileId,
                profile.CustomerId,
                OccurredAt,
                profile.Revision,
                changedFacts.Select(fact => fact.FinancialFactId).Order(StringComparer.Ordinal).ToArray()));
}

public interface IFinancialProfileRepository
{
    Task<FinancialNormalizationSave> NormalizeAsync(
        FinancialNormalizationIdentity idempotencyIdentity,
        ContractRequest<NormalizeSourceFacts> request,
        FinancialProfileChangeTransition profileChange,
        CancellationToken cancellationToken);

    Task<FinancialProfileRecord?> GetProfileAsync(
        string financialProfileId,
        CancellationToken cancellationToken);

    Task<FinancialFactRecord?> GetFactAsync(
        string financialFactId,
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
            string.IsNullOrWhiteSpace(payload.CustomerId) ||
            string.IsNullOrWhiteSpace(payload.ProcessingId) ||
            string.IsNullOrWhiteSpace(payload.NormalizationVersion) ||
            payload.Facts is null ||
            payload.Facts.Count == 0 ||
            !string.Equals(payload.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal) ||
            payload.Facts.Any(fact => !Valid(fact, payload.CustomerId)) ||
            payload.Facts.GroupBy(fact => $"{fact.FactType}|{fact.Label}", StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            return ContractResult<NormalizationResult>.Rejected(
                request, "financial.normalization.invalid", ContractErrorCategory.ValidationError, "Validated source facts are invalid.");
        }

        try
        {
            var recordedAt = timeProvider.GetUtcNow();
            var idempotencyIdentity = FinancialNormalizationIdentity.From(request);
            var profileChange = new FinancialProfileChangeTransition(
                recordedAt,
                request.CorrelationId,
                request.RequestId);
            var saved = await repository.NormalizeAsync(
                idempotencyIdentity,
                request,
                profileChange,
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
                saved.Persisted && saved.CreatedFactIds.Contains(fact.FinancialFactId))).ToArray();
            telemetry.Record(new LifecycleSignal(
                "Financial Profile Service",
                Vs02ContractNames.NormalizeSourceFacts,
                saved.Persisted ? "PROMOTED" : "REPLAYED",
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

    public async Task<ContractResult<FinancialProfileDetails>> GetFinancialProfileAsync(
        ContractRequest<GetFinancialProfile> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, Vs02ContractNames.GetFinancialProfile);
        if (contextError is not null)
        {
            return Reject<FinancialProfileDetails, GetFinancialProfile>(request, contextError);
        }

        if (!ValidQuery(request.Payload.FinancialProfileId, request.Payload.CustomerId, request.Security.Access.CustomerId))
        {
            return ContractResult<FinancialProfileDetails>.Rejected(
                request, "financial.profile.invalid", ContractErrorCategory.ValidationError, "Financial Profile query is invalid.");
        }

        var profile = await repository.GetProfileAsync(request.Payload.FinancialProfileId, cancellationToken);
        if (profile is null || !SameCustomer(profile.CustomerId, request.Payload.CustomerId, request.Security.Access.CustomerId))
        {
            return ContractResult<FinancialProfileDetails>.Rejected(
                request, "financial.profile.not-found", ContractErrorCategory.NotFound, "Authorized Financial Profile was not found.");
        }

        telemetry.Record(QuerySignal(Vs02ContractNames.GetFinancialProfile, request, "financial-profile", profile.FinancialProfileId));
        return ContractResult<FinancialProfileDetails>.Succeeded(
            request,
            new FinancialProfileDetails(
                profile.FinancialProfileId,
                profile.CustomerId,
                profile.Revision,
                profile.UpdatedAt,
                profile.Facts.Select(ToContract).ToArray()));
    }

    public async Task<ContractResult<AuthoritativeFinancialFact>> GetFinancialFactAsync(
        ContractRequest<GetFinancialFact> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, Vs02ContractNames.GetFinancialFact);
        if (contextError is not null)
        {
            return Reject<AuthoritativeFinancialFact, GetFinancialFact>(request, contextError);
        }

        if (!ValidQuery(request.Payload.FinancialFactId, request.Payload.CustomerId, request.Security.Access.CustomerId))
        {
            return ContractResult<AuthoritativeFinancialFact>.Rejected(
                request, "financial.fact.invalid", ContractErrorCategory.ValidationError, "Financial fact query is invalid.");
        }

        var fact = await repository.GetFactAsync(request.Payload.FinancialFactId, cancellationToken);
        if (fact is null || !SameCustomer(fact.CustomerId, request.Payload.CustomerId, request.Security.Access.CustomerId))
        {
            return ContractResult<AuthoritativeFinancialFact>.Rejected(
                request, "financial.fact.not-found", ContractErrorCategory.NotFound, "Authorized financial fact was not found.");
        }

        telemetry.Record(QuerySignal(Vs02ContractNames.GetFinancialFact, request, "financial-fact", fact.FinancialFactId));
        return ContractResult<AuthoritativeFinancialFact>.Succeeded(request, ToContract(fact));
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

        if (!ValidQuery(request.Payload.FinancialProvenanceId, request.Payload.CustomerId, request.Security.Access.CustomerId))
        {
            return ContractResult<FinancialProvenance>.Rejected(
                request, "financial.provenance.invalid", ContractErrorCategory.ValidationError, "Financial provenance query is invalid.");
        }

        var provenance = await repository.GetProvenanceAsync(request.Payload.FinancialProvenanceId, cancellationToken);
        if (provenance is null || !SameCustomer(provenance.CustomerId, request.Payload.CustomerId, request.Security.Access.CustomerId))
        {
            return ContractResult<FinancialProvenance>.Rejected(
                request, "financial.provenance.not-found", ContractErrorCategory.NotFound, "Authorized financial provenance was not found.");
        }

        telemetry.Record(QuerySignal(Vs02ContractNames.GetFinancialProvenance, request, "financial-provenance", provenance.FinancialProvenanceId));
        return ContractResult<FinancialProvenance>.Succeeded(request, provenance);
    }

    private static bool Valid(ValidatedSourceFact fact, string customerId) =>
        string.Equals(fact.CustomerId, customerId, StringComparison.Ordinal) &&
        FinancialObjectTypes.All.Contains(fact.FactType) &&
        !string.IsNullOrWhiteSpace(fact.SourceFactId) &&
        !string.IsNullOrWhiteSpace(fact.Label) &&
        fact.CandidateValue >= 0 &&
        !string.IsNullOrWhiteSpace(fact.Currency) &&
        fact.Currency.Length == 3 &&
        fact.EffectiveDate != default &&
        !string.IsNullOrWhiteSpace(fact.SourceLocation) &&
        fact.Confidence is >= 0 and <= 1 &&
        !string.IsNullOrWhiteSpace(fact.EvidenceId) &&
        !string.IsNullOrWhiteSpace(fact.DocumentVersionId) &&
        !string.IsNullOrWhiteSpace(fact.ExtractionVersion) &&
        !string.IsNullOrWhiteSpace(fact.ValidationVersion);

    private static bool ValidQuery(string identifier, string payloadCustomerId, string securityCustomerId) =>
        !string.IsNullOrWhiteSpace(identifier) &&
        !string.IsNullOrWhiteSpace(payloadCustomerId) &&
        string.Equals(payloadCustomerId, securityCustomerId, StringComparison.Ordinal);

    private static bool SameCustomer(string authorityCustomerId, string payloadCustomerId, string securityCustomerId) =>
        string.Equals(authorityCustomerId, payloadCustomerId, StringComparison.Ordinal) &&
        string.Equals(authorityCustomerId, securityCustomerId, StringComparison.Ordinal);

    private static AuthoritativeFinancialFact ToContract(FinancialFactRecord fact) =>
        new(
            fact.FinancialFactId,
            fact.FinancialProfileId,
            fact.CustomerId,
            fact.FactType,
            fact.Label,
            fact.Value,
            fact.Currency,
            fact.EffectiveDate,
            fact.Revision,
            fact.FinancialProvenanceId);

    private static LifecycleSignal QuerySignal<TPayload>(
        string operation,
        ContractRequest<TPayload> request,
        string subjectType,
        string subjectId) =>
        new(
            "Financial Profile Service",
            operation,
            "READ",
            request.RequestId,
            request.CorrelationId,
            request.CausationId,
            subjectType,
            subjectId,
            "IN_MEMORY_REFERENCE");

    private static ContractResult<TData> Reject<TData, TPayload>(
        ContractRequest<TPayload> request,
        ContractError error) =>
        new(request.ContractName, request.ContractVersion, request.RequestId, request.CorrelationId, ContractOutcome.Rejected, default, error);
}
