using System.Collections.Immutable;

namespace Monergy.Contracts;

public sealed record D10ContractDefinition(
    string ContractId,
    string Name,
    string Taxonomy,
    string Owner,
    string D10Treatment);

public static class D10ContractCatalog
{
    public static ImmutableArray<D10ContractDefinition> Contracts { get; } =
    [
        new("CID-005", "CustomerIdentityChanged", "EVENT", "Customer & Identity Service", "OBSERVED_AUDIT_INPUT"),
        new("CID-007", "TrustedSecurityContext", "SECURITY_CONTEXT", "Customer & Identity Service", "CONSUMED"),
        new("CID-011", "EvaluateConsent", "QUERY", "Consent Service", "CONSUMED"),
        new("CID-012", "ConsentGranted", "EVENT", "Consent Service", "OBSERVED_AUDIT_INPUT"),
        new("CID-013", "ConsentRevoked", "EVENT", "Consent Service", "OBSERVED_AUDIT_INPUT"),
        new("CID-017", "ProviderResultReceived", "EVENT", "Integration Gateway Service", "OBSERVED_AUDIT_INPUT"),
        new("CID-023", Vs02ContractNames.EvidenceRegistered, "EVENT", "Evidence Service", "OBSERVED_AUDIT_INPUT"),
        new("CID-034", Vs02ContractNames.FinancialFactCreated, "EVENT", "Financial Profile Service", "OBSERVED_AUDIT_INPUT"),
        new("CID-040", FinancialRulesContractNames.CalculationCompleted, "EVENT", "Financial Rules Service", "OBSERVED_AUDIT_INPUT"),
        new("CID-049", "AIResponseProduced", "EVENT", "AI Intelligence Service", "OBSERVED_AUDIT_INPUT"),
        new("CID-053", ReportingContractNames.ReportGenerated, "EVENT", "Reporting Service", "ADVANCED_AUDIT_PROPAGATION"),
        new("CID-055", Vs02ContractNames.ScheduleJob, "JOB", "Job Management Service", "ADVANCED_DURABLE"),
        new("CID-056", Vs02ContractNames.CancelJob, "JOB", "Job Management Service", "NEWLY_REALIZED"),
        new("CID-057", Vs02ContractNames.GetJobStatus, "QUERY", "Job Management Service", "ADVANCED_DURABLE"),
        new("CID-058", Vs02ContractNames.JobStarted, "EVENT", "Job Management Service", "NEWLY_REALIZED"),
        new("CID-059", Vs02ContractNames.JobCompleted, "EVENT", "Job Management Service", "NEWLY_REALIZED"),
        new("CID-060", Vs02ContractNames.JobFailed, "EVENT", "Job Management Service", "NEWLY_REALIZED"),
        new("CID-061", "RecordAuditEvidence", "COMMAND", "Audit Service", "NEWLY_REALIZED_PHYSICAL_PROPAGATION"),
    ];
}
