using System.Collections.Immutable;
using Monergy.Contracts;

namespace Monergy.Services.FinancialRules.Application;

public interface IFinancialInputReader
{
    Task<ContractResult<AuthoritativeFinancialFact>> GetFactAsync(ContractRequest<GetFinancialFact> request, CancellationToken cancellationToken);
    Task<ContractResult<FinancialProvenance>> GetProvenanceAsync(ContractRequest<GetFinancialProvenance> request, CancellationToken cancellationToken);
}

public interface ICalculationAccessPolicy
{
    Task<ContractError?> AuthorizeAsync(TrustedSecurityContext context, string customerId, string operation, string correlationId, CancellationToken cancellationToken);
}

public sealed record CalculationRequestIdentity(string ContractName, string Version, string CustomerId, string IdempotencyKey);

public sealed record CalculationExecution(
    string CalculationId,
    string CustomerId,
    ImmutableArray<CalculationInputLineage> Inputs,
    CalculationResult? Result,
    ContractError? Error,
    DomainEvent<CalculationOutcomePayload> Event);

public interface ICalculationRepository
{
    CalculationExecution? FindRequest(CalculationRequestIdentity identity, string fingerprint);
    CalculationExecution? FindResult(string calculationId, string customerId);
    CalculationExecution Commit(CalculationRequestIdentity identity, string fingerprint, Func<CalculationExecution> applicationTransition);
    ImmutableArray<DomainEvent<CalculationOutcomePayload>> PendingEvents();
    void Acknowledge(string eventId);
}

public interface ICalculationEventSink
{
    Task PublishAsync(DomainEvent<CalculationOutcomePayload> outcome, CancellationToken cancellationToken);
}
