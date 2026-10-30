using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Monergy.Contracts;

public static class ReportingContractNames
{
    public const string GenerateReport = "GenerateReport";
    public const string GetReport = "GetReport";
    public const string ReportGenerated = "ReportGenerated";
}

public sealed record FinancialReviewReceiptSelection(
    string AssetAllocationCalculationId,
    string MonthlyNetCashflowRateCalculationId,
    string MonthlyEmiShareCalculationId,
    string MonthlyDebtServiceCoverageCalculationId);

public sealed record GenerateReport(
    string CustomerId,
    FinancialReviewReceiptSelection? FinancialReview = null);

public sealed record GetReport(string CustomerId, string ReportId);

public sealed record ReportGenerationJob(
    string JobId,
    string ReportId,
    JobState State,
    int Attempt,
    string? FailureCode,
    bool Retryable,
    DateTimeOffset UpdatedAt);

public static class ReportingIdentity
{
    public const string OwnerService = "Reporting Service";

    public static string JobId(string operationId) => $"job-report-{operationId}";

    public static string ReportId(string contractVersion, string customerId, string operationId)
    {
        var material = string.Join("|", ReportingContractNames.GenerateReport, contractVersion,
            customerId, operationId);
        return $"report-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..20].ToLowerInvariant()}";
    }
}

public sealed record ReportSourceReference(
    string SourceType,
    string SourceId,
    string AuthoritativeOwner,
    string? EvidenceReferenceId,
    string? FinancialProvenanceReferenceId,
    string? CalculationLineageReferenceId,
    string? RuleId = null,
    string? RuleVersion = null,
    string? DefinitionHash = null,
    string? NumericSemantics = null,
    IReadOnlyList<string>? SourceCalculationIds = null,
    string? FinancialFactReferenceId = null,
    int? FinancialFactRevision = null,
    string? DocumentVersionReferenceId = null,
    FinancialGoalReportSnapshot? FinancialGoal = null);

public sealed record FinancialGoalReportSnapshot(
    int Revision,
    string OperationId,
    DateOnly TargetDate,
    string LifecycleStatus,
    DateOnly? LifecycleAsOfDate,
    string PriorityLevel,
    DateOnly? PriorityAsOfDate,
    DateTimeOffset RecordedAt);

public sealed record ReportLineItem(
    string Label,
    decimal Value,
    string Unit,
    ReportSourceReference Source);

public sealed record ReportExport(
    string FileName,
    string MediaType,
    string Content,
    string Sha256);

public sealed record TrustedFinancialReport(
    string ReportId,
    string CustomerId,
    string State,
    DateTimeOffset GeneratedAt,
    ImmutableArray<ReportLineItem> Items,
    ImmutableArray<string> SourceFinancialReferences,
    ImmutableArray<string> EvidenceReferences,
    ImmutableArray<string> FinancialProvenanceReferences,
    ImmutableArray<string> CalculationLineageReferences,
    string? AiResponseTraceReference,
    string AuditCompatibilityReferenceId,
    ReportExport Export);

public sealed record ReportGeneratedPayload(
    string ReportId,
    string CustomerId,
    string State,
    ImmutableArray<string> SourceFinancialReferences,
    ImmutableArray<string> EvidenceReferences,
    ImmutableArray<string> FinancialProvenanceReferences,
    ImmutableArray<string> CalculationLineageReferences,
    string? AiResponseTraceReference,
    string AuditCompatibilityReferenceId,
    string ExportSha256);
