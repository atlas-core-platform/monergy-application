namespace Monergy.Contracts;

public sealed record ContractDefinition(
    string Id,
    string Name,
    string Type,
    string Owner,
    string Idempotency);

public static class Vs02ContractCatalog
{
    public static IReadOnlyList<ContractDefinition> All { get; } =
    [
        new("CID-020", Vs02ContractNames.CreateDocumentVersion, "COMMAND", "Evidence Service", "IDEMPOTENCY_KEY_REQUIRED"),
        new("CID-021", Vs02ContractNames.GetEvidenceMetadata, "QUERY", "Evidence Service", "READ_ONLY"),
        new("CID-022", Vs02ContractNames.GetEvidenceReference, "QUERY", "Evidence Service", "READ_ONLY"),
        new("CID-023", Vs02ContractNames.EvidenceRegistered, "EVENT", "Evidence Service", "IMMUTABLE_REPLAY_SAFE"),
        new("CID-024", Vs02ContractNames.EvidenceVersionCreated, "EVENT", "Evidence Service", "IMMUTABLE_REPLAY_SAFE"),
        new("CID-025", Vs02ContractNames.ProcessDocument, "JOB", "Document Intelligence Service", "IDEMPOTENCY_KEY_REQUIRED"),
        new("CID-027", Vs02ContractNames.GetProcessingStatus, "QUERY", "Document Intelligence Service", "READ_ONLY"),
        new("CID-028", Vs02ContractNames.ValidatedSourceFactsProduced, "EVENT", "Document Intelligence Service", "IMMUTABLE_REPLAY_SAFE"),
        new("CID-031", Vs02ContractNames.NormalizeSourceFacts, "COMMAND", "Financial Profile Service", "IDEMPOTENCY_KEY_REQUIRED"),
        new("CID-033", Vs02ContractNames.GetFinancialProvenance, "QUERY", "Financial Profile Service", "READ_ONLY"),
        new("CID-034", Vs02ContractNames.FinancialFactCreated, "EVENT", "Financial Profile Service", "IMMUTABLE_REPLAY_SAFE"),
        new("CID-035", Vs02ContractNames.FinancialFactUpdated, "EVENT", "Financial Profile Service", "IMMUTABLE_REPLAY_SAFE"),
        new("CID-055", Vs02ContractNames.ScheduleJob, "JOB", "Job Management Service", "IDEMPOTENCY_KEY_REQUIRED"),
        new("CID-057", Vs02ContractNames.GetJobStatus, "QUERY", "Job Management Service", "READ_ONLY"),
    ];
}
