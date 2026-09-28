using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Monergy.Contracts;

public static class SearchContractNames
{
    public const string SearchDocuments = "SearchDocuments";
    public const string SearchFinancialData = "SearchFinancialData";
    public const string BuildRetrievalContext = "BuildRetrievalContext";
    public const string IndexRefreshRequested = "IndexRefreshRequested";
}

[JsonConverter(typeof(JsonStringEnumConverter<SearchMatchMode>))]
public enum SearchMatchMode
{
    Lexical,
    Semantic,
}

[JsonConverter(typeof(JsonStringEnumConverter<SearchResultType>))]
public enum SearchResultType
{
    Document,
    Financial,
    Calculation,
}

public sealed record SearchDocuments(
    string CustomerId,
    string Query,
    SearchMatchMode MatchMode,
    int Limit = 20);

public sealed record SearchFinancialData(
    string CustomerId,
    string Query,
    SearchMatchMode MatchMode,
    int Limit = 20);

public sealed record BuildRetrievalContext(
    string CustomerId,
    ImmutableArray<string> SearchRecordIds);

public sealed record IndexRefreshRequested(string CustomerId);

public sealed record SearchSourceReference(
    string SourceObjectType,
    string SourceObjectId,
    string SourceReferenceId,
    string? ProvenanceReferenceId,
    string? CalculationLineageReferenceId);

public sealed record SearchHit(
    string SearchRecordId,
    SearchResultType ResultType,
    string Title,
    string Summary,
    string AuthoritativeOwner,
    string RepresentationState,
    double Score,
    SearchSourceReference Source);

public sealed record SearchResults(
    string Query,
    SearchMatchMode MatchMode,
    ImmutableArray<SearchHit> Items);

public sealed record RetrievalContext(
    ImmutableArray<SearchHit> Items,
    string RepresentationState);

public sealed record IndexRefreshResult(
    int RecordCount,
    string RebuildFingerprint,
    string RepresentationState);
