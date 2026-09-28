using System.Collections.Immutable;
using Monergy.Contracts;

namespace Monergy.Services.Reporting.Domain;

public sealed record ReportSourceSnapshot(
    string CustomerId,
    DateTimeOffset AsOf,
    ImmutableArray<ReportLineItem> Items,
    string? AiResponseTraceReference = null);

public static class ReportingAuthority
{
    public const string EvidenceService = "Evidence Service";
    public const string FinancialProfileService = "Financial Profile Service";
    public const string FinancialRulesService = "Financial Rules Service";
    public const string ReportingService = "Reporting Service";
    public const string Generated = "GENERATED";
}
