using System.Collections.Immutable;

namespace Monergy.Contracts;

public static class ReportingContractNames
{
    public const string GenerateReport = "GenerateReport";
    public const string GetReport = "GetReport";
    public const string ReportGenerated = "ReportGenerated";
}

public sealed record GenerateReport(string CustomerId);

public sealed record GetReport(string CustomerId, string ReportId);

public sealed record ReportSourceReference(
    string SourceType,
    string SourceId,
    string AuthoritativeOwner,
    string? EvidenceReferenceId,
    string? FinancialProvenanceReferenceId,
    string? CalculationLineageReferenceId);

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
