using System.Collections.Immutable;

namespace Monergy.Contracts;

public sealed record ReportingContractDefinition(
    string ContractId,
    string Name,
    string Taxonomy,
    string Owner,
    ImmutableArray<string> Consumers,
    string AuthorityRule);

public static class D08ContractCatalog
{
    public static ImmutableArray<ReportingContractDefinition> Contracts { get; } =
    [
        new("CID-051", ReportingContractNames.GenerateReport, "JOB", "Reporting Service",
            ["Job Management Service"], "Reporting consumes authoritative values and calculations; it does not own them."),
        new("CID-052", ReportingContractNames.GetReport, "QUERY", "Reporting Service",
            [], "Returns an authorized governed report within the current customer boundary."),
        new("CID-053", ReportingContractNames.ReportGenerated, "EVENT", "Reporting Service",
            ["Audit Service"], "Announces report identity and provenance context without transferring source authority."),
    ];
}
