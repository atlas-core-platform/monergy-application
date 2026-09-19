using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Domain;

namespace Monergy.Services.FinancialRules.Application;

public sealed class FinancialRulesApplication(
    IRuleRegistry rules,
    IFinancialInputReader inputs,
    ICalculationAccessPolicy access,
    ICalculationRepository repository,
    ILifecycleTelemetry telemetry,
    TimeProvider clock)
{
    public async Task<ContractResult<CalculationResult>> ExecuteAsync(
        ContractRequest<ExecuteCalculation> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await AuthorizeAsync(request, FinancialRulesContractNames.ExecuteCalculation, request.Payload?.CustomerId, cancellationToken);
            var payload = request.Payload;
            if (payload is null || string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
                string.IsNullOrWhiteSpace(payload.RuleId) || string.IsNullOrWhiteSpace(payload.RuleVersion) ||
                payload.Inputs.IsDefaultOrEmpty || payload.Inputs.Length > 32 ||
                payload.Inputs.Any(input => input is null || string.IsNullOrWhiteSpace(input.Role) ||
                    string.IsNullOrWhiteSpace(input.FinancialFactId) || input.ExpectedRevision is <= 0) ||
                payload.Inputs.Select(input => input.Role).Distinct(StringComparer.Ordinal).Count() != payload.Inputs.Length)
            {
                throw new CalculationException("calculation.request.invalid", ContractErrorCategory.ValidationError);
            }

            var identity = new CalculationRequestIdentity(request.ContractName, request.ContractVersion,
                request.Security.Access.CustomerId, request.IdempotencyKey);
            // Explicit property order and ordinal roles; request/time/correlation IDs do not replace caller identity.
            var canonical = payload with { Inputs = payload.Inputs.OrderBy(input => input.Role, StringComparer.Ordinal).ToImmutableArray() };
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical, ContractJson.Options)));
            var previous = repository.FindRequest(identity, fingerprint);
            if (previous is not null)
            {
                await ValidateHistoricalAccessAsync(request, previous, cancellationToken);
                return Respond(request, previous, "REPLAYED");
            }

            var rule = rules.Find(payload.RuleId, payload.RuleVersion)
                ?? throw new CalculationException("calculation.rule.unavailable", ContractErrorCategory.NotFound);
            var captured = ImmutableArray.CreateBuilder<CalculationInputLineage>();
            foreach (var reference in canonical.Inputs)
            {
                var factResult = await inputs.GetFactAsync(Forward(request, Vs02ContractNames.GetFinancialFact,
                    new GetFinancialFact(reference.FinancialFactId, payload.CustomerId)), cancellationToken);
                var fact = RequireSuccess(factResult);
                if (fact.FinancialFactId != reference.FinancialFactId || fact.CustomerId != payload.CustomerId ||
                    fact.Revision <= 0 || string.IsNullOrWhiteSpace(fact.FinancialProvenanceId))
                {
                    throw new CalculationException("calculation.fact.invalid", ContractErrorCategory.DependencyFailure);
                }

                if (reference.ExpectedRevision is not null && reference.ExpectedRevision != fact.Revision)
                {
                    throw new CalculationException("calculation.revision.changed", ContractErrorCategory.PreconditionFailed);
                }

                var provenance = await ReadProvenanceAsync(request, fact.FinancialProvenanceId, payload.CustomerId, cancellationToken);
                if (provenance.FinancialFactId != fact.FinancialFactId)
                {
                    throw new CalculationException("calculation.provenance.mismatch", ContractErrorCategory.DependencyFailure);
                }

                captured.Add(new CalculationInputLineage(reference.Role, fact.FinancialFactId, fact.Revision, fact.Value, fact.Currency, provenance));
            }

            // Recheck current policy after dependency reads. The atomic transition below performs no external I/O.
            await AuthorizeAsync(request, FinancialRulesContractNames.ExecuteCalculation, payload.CustomerId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var lineage = captured.ToImmutable();
            var saved = repository.Commit(identity, fingerprint, () => Transition(request, rule, lineage));
            // A concurrent winner may have read different exact revisions; authorize that winner's original lineage.
            await ValidateHistoricalAccessAsync(request, saved, cancellationToken);
            return Respond(request, saved, "RECORDED");
        }
        catch (CalculationException exception)
        {
            return Failure<ExecuteCalculation, CalculationResult>(request, exception);
        }
        catch (IOException)
        {
            return ContractResult<CalculationResult>.Failed(request, "calculation.storage.unavailable",
                ContractErrorCategory.TemporarilyUnavailable, "Calculation storage is temporarily unavailable.", true);
        }
        catch (HttpRequestException)
        {
            return ContractResult<CalculationResult>.Failed(request, "calculation.dependency.unavailable",
                ContractErrorCategory.DependencyFailure, "An authoritative dependency is unavailable.", true);
        }
    }

    public async Task<ContractResult<CalculationResult>> GetResultAsync(
        ContractRequest<GetCalculationResult> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await AuthorizeAsync(request, FinancialRulesContractNames.GetCalculationResult, request.Payload?.CustomerId, cancellationToken);
            var record = await ReadAsync(request, request.Payload!.CalculationResultId, request.Payload.CustomerId, cancellationToken);
            return Respond(request, record, "READ");
        }
        catch (CalculationException exception) { return Failure<GetCalculationResult, CalculationResult>(request, exception); }
        catch (HttpRequestException) { return DependencyFailure<GetCalculationResult, CalculationResult>(request); }
    }

    public async Task<ContractResult<CalculationExplanation>> ExplainAsync(
        ContractRequest<ExplainCalculation> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await AuthorizeAsync(request, FinancialRulesContractNames.ExplainCalculation, request.Payload?.CustomerId, cancellationToken);
            var record = await ReadAsync(request, request.Payload!.CalculationResultId, request.Payload.CustomerId, cancellationToken);
            if (record.Result is null)
            {
                throw new CalculationException(record.Error!.Code, record.Error.Category);
            }

            var result = record.Result;
            Record(request, "EXPLAINED", result.CalculationResultId);
            return ContractResult<CalculationExplanation>.Succeeded(request, new CalculationExplanation(result,
                $"Engineering fixture {result.RuleId}/{result.RuleVersion}; executed {result.Operation}. {result.NumericSemantics}. Not approved client methodology."));
        }
        catch (CalculationException exception) { return Failure<ExplainCalculation, CalculationExplanation>(request, exception); }
        catch (HttpRequestException) { return DependencyFailure<ExplainCalculation, CalculationExplanation>(request); }
    }

    // Internal verification operation only. Public CID-039 remains explanation, not a new recalculation command.
    public async Task<ContractResult<CalculationResult>> ReproduceAsync(
        ContractRequest<ExplainCalculation> request, CancellationToken cancellationToken = default)
    {
        var explained = await ExplainAsync(request, cancellationToken);
        if (explained.Outcome != ContractOutcome.Success)
        {
            return new(request.ContractName, request.ContractVersion, request.RequestId, request.CorrelationId,
                explained.Outcome, null, explained.Error);
        }

        try
        {
            var historical = explained.Data!.Result;
            var rule = rules.Find(historical.RuleId, historical.RuleVersion)
                ?? throw new CalculationException("calculation.rule.unavailable", ContractErrorCategory.NotFound);
            if (rule.Definition.DefinitionHash != historical.DefinitionHash ||
                rule.Definition.ImplementationIdentity != historical.ImplementationIdentity)
            {
                throw new CalculationException("calculation.rule.drift", ContractErrorCategory.PreconditionFailed);
            }

            var reproduced = rule.Evaluate(historical.Inputs);
            if (reproduced.Value != historical.Value || reproduced.Unit != historical.Unit || reproduced.Operation != historical.Operation)
            {
                throw new CalculationException("calculation.reproduction.drift", ContractErrorCategory.PreconditionFailed);
            }

            return ContractResult<CalculationResult>.Succeeded(request, historical);
        }
        catch (CalculationException exception) { return Failure<ExplainCalculation, CalculationResult>(request, exception); }
    }

    private CalculationExecution Transition(ContractRequest<ExecuteCalculation> request, IDeterministicRule rule,
        ImmutableArray<CalculationInputLineage> lineage)
    {
        var id = Guid.NewGuid().ToString("N");
        var lineageId = Guid.NewGuid().ToString("N");
        var eventId = Guid.NewGuid().ToString("N");
        var now = clock.GetUtcNow();
        CalculationResult? result = null;
        ContractError? error = null;
        try
        {
            var value = rule.Evaluate(lineage);
            result = new CalculationResult(id, 1, lineageId, request.Payload.CustomerId,
                rule.Definition.RuleId, rule.Definition.Version, rule.Definition.ImplementationIdentity,
                rule.Definition.DefinitionHash, rule.Definition.NumericSemantics, lineage, value.Value, value.Unit,
                value.Operation, now, request.Security.Actor.ActorId, request.Security.Workload.WorkloadIdentityId,
                request.Security.Access.Purpose, request.CorrelationId, request.CausationId, eventId);
        }
        catch (CalculationException exception)
        {
            error = new ContractError(exception.Code, exception.Category, "The engineering calculation could not be computed.", false, request.CorrelationId);
        }

        var outcome = new DomainEvent<CalculationOutcomePayload>(
            error is null ? "CID-040" : "CID-041", eventId,
            error is null ? FinancialRulesContractNames.CalculationCompleted : FinancialRulesContractNames.CalculationFailed,
            ContractGuard.CurrentVersion, now, request.CorrelationId, request.RequestId,
            "Financial Rules Service", "financial-calculation", id,
            new CalculationOutcomePayload(request.Payload.CustomerId, id, lineageId,
                rule.Definition.RuleId, rule.Definition.Version, 1, error?.Code));
        return new CalculationExecution(id, request.Payload.CustomerId, lineage, result, error, outcome);
    }

    private async Task<CalculationExecution> ReadAsync<T>(ContractRequest<T> request, string id, string customer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new CalculationException("calculation.id.invalid", ContractErrorCategory.ValidationError);
        }

        var record = repository.FindResult(id, customer)
            ?? throw new CalculationException("calculation.history.unavailable", ContractErrorCategory.NotFound);
        await ValidateHistoricalAccessAsync(request, record, cancellationToken);
        return record;
    }

    private async Task ValidateHistoricalAccessAsync<T>(ContractRequest<T> request, CalculationExecution record,
        CancellationToken cancellationToken)
    {
        foreach (var input in record.Inputs)
        {
            var current = await ReadProvenanceAsync(request, input.Provenance.FinancialProvenanceId, record.CustomerId, cancellationToken);
            if (current != input.Provenance)
            {
                throw new CalculationException("calculation.history.drift", ContractErrorCategory.PreconditionFailed);
            }
        }

        await AuthorizeAsync(request, request.ContractName, record.CustomerId, cancellationToken);
    }

    private async Task<FinancialProvenance> ReadProvenanceAsync<T>(ContractRequest<T> request, string id, string customer,
        CancellationToken cancellationToken)
    {
        var response = await inputs.GetProvenanceAsync(Forward(request, Vs02ContractNames.GetFinancialProvenance,
            new GetFinancialProvenance(id, customer)), cancellationToken);
        var provenance = RequireSuccess(response);
        if (provenance.FinancialProvenanceId != id || provenance.CustomerId != customer ||
            string.IsNullOrWhiteSpace(provenance.FinancialFactId) || string.IsNullOrWhiteSpace(provenance.EvidenceId) ||
            string.IsNullOrWhiteSpace(provenance.DocumentVersionId) || string.IsNullOrWhiteSpace(provenance.SourceFactId) ||
            string.IsNullOrWhiteSpace(provenance.ExtractionVersion) || string.IsNullOrWhiteSpace(provenance.ValidationVersion) ||
            string.IsNullOrWhiteSpace(provenance.NormalizationVersion) || string.IsNullOrWhiteSpace(provenance.ActorId) ||
            string.IsNullOrWhiteSpace(provenance.WorkloadIdentityId) || provenance.RecordedAt == default ||
            string.IsNullOrWhiteSpace(provenance.CorrelationId))
        {
            throw new CalculationException("calculation.provenance.invalid", ContractErrorCategory.DependencyFailure);
        }

        return provenance;
    }

    private async Task AuthorizeAsync<T>(ContractRequest<T> request, string name, string? customer, CancellationToken cancellationToken)
    {
        if (request.Security is null)
        {
            throw new CalculationException("security.authentication.required", ContractErrorCategory.AuthenticationRequired);
        }

        var error = ContractGuard.Validate(request, name);
        if (error is not null) { throw new CalculationException(error.Code, error.Category); }
        if (string.IsNullOrWhiteSpace(customer) || request.Security.Access.CustomerId != customer)
        {
            throw new CalculationException("calculation.customer.denied", ContractErrorCategory.AccessDenied);
        }

        var policyError = await access.AuthorizeAsync(request.Security, customer, name, request.CorrelationId, cancellationToken);
        if (policyError is not null) { throw new CalculationException(policyError.Code, policyError.Category); }
    }

    private ContractResult<CalculationResult> Respond<T>(ContractRequest<T> request, CalculationExecution execution, string outcome)
    {
        Record(request, outcome, execution.CalculationId);
        return execution.Error is null
            ? ContractResult<CalculationResult>.Succeeded(request, execution.Result!)
            : new(request.ContractName, request.ContractVersion, request.RequestId, request.CorrelationId,
                ContractOutcome.Failed, null, execution.Error with { CorrelationId = request.CorrelationId });
    }

    private void Record<T>(ContractRequest<T> request, string outcome, string id) =>
        telemetry.Record(new LifecycleSignal("Financial Rules Service", request.ContractName, outcome,
            request.RequestId, request.CorrelationId, request.CausationId, "financial-calculation", id, "IN_MEMORY_REFERENCE"));

    private static ContractResult<TResult> Failure<TRequest, TResult>(ContractRequest<TRequest> request, CalculationException exception) =>
        exception.Category is ContractErrorCategory.DependencyFailure or ContractErrorCategory.TemporarilyUnavailable
            ? ContractResult<TResult>.Failed(request, exception.Code, exception.Category, "Calculation dependency is unavailable or invalid.", true)
            : ContractResult<TResult>.Rejected(request, exception.Code, exception.Category, "Calculation request or authorized history is unavailable.");

    private static ContractResult<TResult> DependencyFailure<TRequest, TResult>(ContractRequest<TRequest> request) =>
        ContractResult<TResult>.Failed(request, "calculation.dependency.unavailable", ContractErrorCategory.DependencyFailure, "Authoritative history is unavailable.", true);

    private static T RequireSuccess<T>(ContractResult<T>? response) where T : class =>
        response?.Outcome == ContractOutcome.Success && response.Data is not null
            ? response.Data
            : throw new CalculationException("calculation.input.unavailable", response?.Error?.Category ?? ContractErrorCategory.DependencyFailure);

    private static ContractRequest<TPayload> Forward<TSource, TPayload>(ContractRequest<TSource> source, string name, TPayload payload) =>
        new(name, ContractGuard.CurrentVersion, source.RequestId, source.CorrelationId, source.CausationId,
            source.Security, null, payload);
}
