using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.SearchRetrieval.Application;
using Monergy.Services.SearchRetrieval.Domain;

namespace Monergy.Services.SearchRetrieval.Infrastructure;

public sealed class ReferenceSearchProjectionSource : ISearchProjectionSource
{
    private ImmutableArray<DerivedSearchRecord> records;

    public ReferenceSearchProjectionSource(IConfiguration configuration, IEnumerable<DerivedSearchRecord>? initial = null)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        records = (initial ?? DefaultRecords()).OrderBy(item => item.SearchRecordId, StringComparer.Ordinal).ToImmutableArray();
    }

    public ImmutableArray<DerivedSearchRecord> ReadAll() => records;

    public ImmutableArray<DerivedSearchRecord> ReadForCustomer(string customerId) =>
        records.Where(item => item.CustomerId == customerId).ToImmutableArray();

    public void Replace(IEnumerable<DerivedSearchRecord> replacement) =>
        records = replacement.OrderBy(item => item.SearchRecordId, StringComparer.Ordinal).ToImmutableArray();

    private static IEnumerable<DerivedSearchRecord> DefaultRecords()
    {
        const string customer = "reference-customer";
        const string authorization = "reference-authorization";
        yield return new("document-reference-001", customer, authorization, SearchResultType.Document,
            "Salary statement evidence", "Salary statement for the reference scenario", SearchAuthority.EvidenceService,
            new("DocumentVersion", "document-version-reference-001", "evidence-reference-001",
                "evidence-provenance-reference-001", null));
        yield return new("financial-reference-001", customer, authorization, SearchResultType.Financial,
            "Normalized salary", "Salary INR 125000 from the reference financial profile", SearchAuthority.FinancialProfileService,
            new("FinancialFact", "financial-fact-reference-001", "financial-profile-reference-001",
                "financial-provenance-reference-001", null));
        yield return new("calculation-reference-001", customer, authorization, SearchResultType.Calculation,
            "Savings ratio calculation", "Deterministic savings ratio for the reference scenario", SearchAuthority.FinancialRulesService,
            new("FinancialCalculation", "calculation-reference-001", "calculation-result-reference-001",
                "financial-provenance-reference-001", "calculation-lineage-reference-001"));
    }
}

public sealed class ReferenceSearchAuthorizationPolicy : ISearchAuthorizationPolicy
{
    private readonly Dictionary<string, TrustedSecurityContext> grants = new(StringComparer.Ordinal);
    private readonly object sync = new();

    public ReferenceSearchAuthorizationPolicy(IConfiguration configuration) => ReferenceAdapterGuard.EnsureAllowed(configuration);

    public void Grant(TrustedSecurityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (sync) { grants[context.Access.AuthorizationContextId] = context; }
    }

    public Task<ContractError?> AuthorizeAsync(TrustedSecurityContext context, string customerId, string operation,
        string correlationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var permitted = grants.TryGetValue(context.Access.AuthorizationContextId, out var grant) &&
                grant == context && context.Access.CustomerId == customerId;
            return Task.FromResult(permitted ? null : new ContractError("search.authorization.denied",
                ContractErrorCategory.AccessDenied, "Current customer, actor and workload authorization is required.",
                false, correlationId));
        }
    }
}

public sealed partial class InMemoryDerivedSearchIndex : IDerivedSearchIndex
{
    private sealed record IndexedRecord(DerivedSearchRecord Record, double[] Vector);
    private ImmutableArray<IndexedRecord> records = [];
    private readonly object sync = new();

    public InMemoryDerivedSearchIndex(IConfiguration configuration) => ReferenceAdapterGuard.EnsureAllowed(configuration);

    public IndexRefreshResult Rebuild(IEnumerable<DerivedSearchRecord> records)
    {
        var ordered = records.OrderBy(item => item.SearchRecordId, StringComparer.Ordinal).ToArray();
        Validate(ordered);
        var rebuilt = Build(ordered);
        lock (sync) { this.records = rebuilt; }
        return Result(ordered);
    }

    public IndexRefreshResult RebuildCustomer(string customerId, IEnumerable<DerivedSearchRecord> records)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        var ordered = records.OrderBy(item => item.SearchRecordId, StringComparer.Ordinal).ToArray();
        Validate(ordered);
        if (ordered.Any(item => item.CustomerId != customerId))
        {
            throw new ArgumentException("A customer rebuild may contain records only for its requested customer boundary.", nameof(records));
        }

        var rebuilt = Build(ordered);
        lock (sync)
        {
            this.records = this.records.Where(item => item.Record.CustomerId != customerId)
                .Concat(rebuilt)
                .OrderBy(item => item.Record.CustomerId, StringComparer.Ordinal)
                .ThenBy(item => item.Record.SearchRecordId, StringComparer.Ordinal)
                .ToImmutableArray();
        }
        return Result(ordered);
    }

    public ImmutableArray<RankedSearchRecord> Search(string customerId, string authorizationContextId,
        ImmutableHashSet<SearchResultType> resultTypes, string query, SearchMatchMode matchMode, int limit)
    {
        var queryTokens = Tokens(query);
        var queryVector = Vectorize(query);
        ImmutableArray<IndexedRecord> snapshot;
        lock (sync) { snapshot = records; }
        return snapshot
            .Where(item => item.Record.CustomerId == customerId &&
                item.Record.RequiredAuthorizationContextId == authorizationContextId &&
                resultTypes.Contains(item.Record.ResultType))
            .Select(item => new RankedSearchRecord(item.Record, matchMode == SearchMatchMode.Semantic
                ? Cosine(queryVector, item.Vector)
                : LexicalScore(queryTokens, Tokens(item.Record.Title + " " + item.Record.SearchableText))))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Record.SearchRecordId, StringComparer.Ordinal)
            .Take(limit)
            .ToImmutableArray();
    }

    public ImmutableArray<DerivedSearchRecord> FindAuthorized(string customerId, string authorizationContextId,
        IEnumerable<string> searchRecordIds)
    {
        var ids = searchRecordIds.ToImmutableHashSet(StringComparer.Ordinal);
        ImmutableArray<IndexedRecord> snapshot;
        lock (sync) { snapshot = records; }
        return snapshot.Where(item => ids.Contains(item.Record.SearchRecordId) &&
                item.Record.CustomerId == customerId &&
                item.Record.RequiredAuthorizationContextId == authorizationContextId)
            .Select(item => item.Record)
            .OrderBy(item => item.SearchRecordId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static void Validate(DerivedSearchRecord[] source)
    {
        if (source.Any(item => string.IsNullOrWhiteSpace(item.SearchRecordId) ||
                string.IsNullOrWhiteSpace(item.CustomerId) ||
                string.IsNullOrWhiteSpace(item.RequiredAuthorizationContextId) ||
                string.IsNullOrWhiteSpace(item.Title) ||
                string.IsNullOrWhiteSpace(item.SearchableText) ||
                string.IsNullOrWhiteSpace(item.AuthoritativeOwner) ||
                item.AuthoritativeOwner == "Search & Retrieval Service" ||
                string.IsNullOrWhiteSpace(item.Source.SourceObjectId) ||
                string.IsNullOrWhiteSpace(item.Source.SourceReferenceId)) ||
            source.Select(item => item.SearchRecordId).Distinct(StringComparer.Ordinal).Count() != source.Length)
        {
            throw new ArgumentException("Derived search projections require unique IDs and source-owned authority references.", nameof(source));
        }
    }

    private static ImmutableArray<IndexedRecord> Build(IEnumerable<DerivedSearchRecord> source) =>
        source.Select(item => new IndexedRecord(item, Vectorize(item.Title + " " + item.SearchableText))).ToImmutableArray();

    private static IndexRefreshResult Result(DerivedSearchRecord[] ordered)
    {
        var identity = string.Join('\n', ordered.Select(item => string.Join('|', item.SearchRecordId, item.CustomerId,
            item.RequiredAuthorizationContextId, item.ResultType, item.Title, item.SearchableText,
            item.AuthoritativeOwner, item.Source.SourceObjectType, item.Source.SourceObjectId,
            item.Source.SourceReferenceId, item.Source.ProvenanceReferenceId, item.Source.CalculationLineageReferenceId)));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new(ordered.Length, fingerprint, SearchAuthority.DerivedRebuildable);
    }

    private static string[] Tokens(string value) => WordPattern().Matches(value.ToLowerInvariant())
        .Select(match => match.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static double LexicalScore(IEnumerable<string> query, IReadOnlyCollection<string> candidate)
    {
        var terms = query.ToArray();
        return terms.Length == 0 ? 0 : (double)terms.Count(candidate.Contains) / terms.Length;
    }

    private static double[] Vectorize(string value)
    {
        var vector = new double[32];
        foreach (var token in Tokens(value))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            vector[BitConverter.ToUInt32(hash, 0) % (uint)vector.Length] += 1;
        }
        return vector;
    }

    private static double Cosine(double[] left, double[] right)
    {
        var dot = 0d;
        var leftMagnitude = 0d;
        var rightMagnitude = 0d;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }
        return leftMagnitude == 0 || rightMagnitude == 0 ? 0 : dot / Math.Sqrt(leftMagnitude * rightMagnitude);
    }

    [GeneratedRegex("[a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}
