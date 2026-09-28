using Monergy.Contracts;

namespace Monergy.Services.SearchRetrieval.Domain;

public sealed record DerivedSearchRecord(
    string SearchRecordId,
    string CustomerId,
    string RequiredAuthorizationContextId,
    SearchResultType ResultType,
    string Title,
    string SearchableText,
    string AuthoritativeOwner,
    SearchSourceReference Source);

public sealed record RankedSearchRecord(DerivedSearchRecord Record, double Score);

public static class SearchAuthority
{
    public const string DerivedRebuildable = "DERIVED_REBUILDABLE";
    public const string EvidenceService = "Evidence Service";
    public const string FinancialProfileService = "Financial Profile Service";
    public const string FinancialRulesService = "Financial Rules Service";
}
