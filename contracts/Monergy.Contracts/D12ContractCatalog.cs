using System.Collections.Immutable;

namespace Monergy.Contracts;

public sealed record D12ContractDefinition(
    string ContractId,
    string Name,
    string Taxonomy,
    string Owner,
    string Treatment);

public static class D12ContractCatalog
{
    public static ImmutableArray<D12ContractDefinition> Contracts { get; } =
    [
        new("CID-007", "TrustedSecurityContext", "SECURITY_CONTEXT", "Customer & Identity Service", "CONSUMED"),
        new("CID-020", Vs02ContractNames.CreateDocumentVersion, "COMMAND", "Evidence Service", "REFERENCE_SCENARIO"),
        new("CID-021", Vs02ContractNames.GetEvidenceMetadata, "QUERY", "Evidence Service", "CONSUMED"),
        new("CID-022", Vs02ContractNames.GetEvidenceReference, "QUERY", "Evidence Service", "CONSUMED"),
        new("CID-025", Vs02ContractNames.ProcessDocument, "JOB", "Document Intelligence Service", "COMPATIBILITY"),
        new("CID-026", D12ContractNames.ReprocessDocument, "JOB", "Document Intelligence Service", "NEWLY_REALIZED"),
        new("CID-027", Vs02ContractNames.GetProcessingStatus, "QUERY", "Document Intelligence Service", "COMPATIBILITY"),
        new("CID-028", Vs02ContractNames.ValidatedSourceFactsProduced, "EVENT", "Document Intelligence Service", "COMPATIBILITY"),
        new("CID-029", D12ContractNames.DocumentProcessingFailed, "EVENT", "Document Intelligence Service", "NEWLY_REALIZED"),
        new("CID-055", Vs02ContractNames.ScheduleJob, "JOB", "Job Management Service", "REFERENCE_SCENARIO"),
        new("CID-057", Vs02ContractNames.GetJobStatus, "QUERY", "Job Management Service", "REFERENCE_SCENARIO"),
        new("CID-061", "RecordAuditEvidence", "COMMAND", "Audit Service", "REFERENCE_SCENARIO"),
    ];
}
