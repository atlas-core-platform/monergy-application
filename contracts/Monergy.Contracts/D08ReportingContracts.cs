using System.Collections.Immutable;

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
    IReadOnlyList<string>? SourceCalculationIds = null);

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
