using System.Collections.Immutable;

namespace Monergy.Contracts;

public static class FinancialRulesContractNames
{
    public const string ExecuteCalculation = "ExecuteCalculation";
    public const string GetCalculationResult = "GetCalculationResult";
    public const string ExplainCalculation = "ExplainCalculation";
    public const string CalculationCompleted = "CalculationCompleted";
    public const string CalculationFailed = "CalculationFailed";
}

public sealed record CalculationInputReference(string Role, string FinancialFactId, int? ExpectedRevision);

public sealed record ExecuteCalculation(
    string CustomerId,
    string RuleId,
    string RuleVersion,
    ImmutableArray<CalculationInputReference> Inputs);

public sealed record GetCalculationResult(string CustomerId, string CalculationResultId);

public sealed record ExplainCalculation(string CustomerId, string CalculationResultId);

public sealed record CalculationInputLineage(
    string Role,
    string FinancialFactId,
    int FactRevision,
    decimal Value,
    string Unit,
    FinancialProvenance Provenance);

public sealed record CalculationResult(
    string CalculationResultId,
    int Revision,
    string CalculationLineageId,
    string CustomerId,
    string RuleId,
    string RuleVersion,
    string ImplementationIdentity,
    string DefinitionHash,
    string NumericSemantics,
    ImmutableArray<CalculationInputLineage> Inputs,
    decimal Value,
    string Unit,
    string Operation,
    DateTimeOffset ExecutedAt,
    string ActorId,
    string WorkloadIdentityId,
    string Purpose,
    string CorrelationId,
    string? CausationId,
    string OutcomeEventId);

public sealed record CalculationExplanation(CalculationResult Result, string Explanation);

public sealed record CalculationOutcomePayload(
    string CustomerId,
    string CalculationResultId,
    string CalculationLineageId,
    string RuleId,
    string RuleVersion,
    int Revision,
    string? FailureCode);

public static class D05ContractCatalog
{
    public static IReadOnlyList<ContractDefinition> All { get; } = Array.AsReadOnly<ContractDefinition>(
    [
        new("CID-037", FinancialRulesContractNames.ExecuteCalculation, "COMMAND", "Financial Rules Service", "IDEMPOTENCY_KEY_REQUIRED"),
        new("CID-038", FinancialRulesContractNames.GetCalculationResult, "QUERY", "Financial Rules Service", "READ_ONLY"),
        new("CID-039", FinancialRulesContractNames.ExplainCalculation, "QUERY", "Financial Rules Service", "READ_ONLY"),
        new("CID-040", FinancialRulesContractNames.CalculationCompleted, "EVENT", "Financial Rules Service", "IMMUTABLE_REPLAY_SAFE"),
        new("CID-041", FinancialRulesContractNames.CalculationFailed, "EVENT", "Financial Rules Service", "IMMUTABLE_REPLAY_SAFE"),
    ]);
}
