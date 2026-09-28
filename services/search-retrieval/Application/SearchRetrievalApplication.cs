using System.Collections.Immutable;
using Monergy.Contracts;
using Monergy.Services.SearchRetrieval.Domain;

namespace Monergy.Services.SearchRetrieval.Application;

public sealed class SearchRetrievalApplication(
    IDerivedSearchIndex index,
    ISearchProjectionSource projectionSource,
    ISearchAuthorizationPolicy authorizationPolicy)
{
    private static readonly ImmutableHashSet<SearchResultType> DocumentTypes =
        ImmutableHashSet.Create(SearchResultType.Document);
    private static readonly ImmutableHashSet<SearchResultType> FinancialTypes =
        ImmutableHashSet.Create(SearchResultType.Financial, SearchResultType.Calculation);

    public Task<ContractResult<SearchResults>> SearchDocumentsAsync(
        ContractRequest<SearchDocuments> request,
        CancellationToken cancellationToken = default) =>
        SearchAsync(request, SearchContractNames.SearchDocuments, DocumentTypes, cancellationToken);

    public Task<ContractResult<SearchResults>> SearchFinancialDataAsync(
        ContractRequest<SearchFinancialData> request,
        CancellationToken cancellationToken = default) =>
        SearchAsync(request, SearchContractNames.SearchFinancialData, FinancialTypes, cancellationToken);

    public async Task<ContractResult<RetrievalContext>> BuildRetrievalContextAsync(
        ContractRequest<BuildRetrievalContext> request,
        CancellationToken cancellationToken = default)
    {
        var contractError = ContractGuard.Validate(request, SearchContractNames.BuildRetrievalContext);
        if (contractError is not null) { return Rejected<BuildRetrievalContext, RetrievalContext>(request, contractError); }
        if (!ValidCustomer(request.Payload.CustomerId, request.Security) ||
            request.Payload.SearchRecordIds.IsDefaultOrEmpty ||
            request.Payload.SearchRecordIds.Any(string.IsNullOrWhiteSpace) ||
            request.Payload.SearchRecordIds.Distinct(StringComparer.Ordinal).Count() != request.Payload.SearchRecordIds.Length)
        {
            return ContractResult<RetrievalContext>.Rejected(request, "search.context.invalid",
                ContractErrorCategory.ValidationError, "A customer-scoped unique result selection is required.");
        }

        var accessError = await authorizationPolicy.AuthorizeAsync(request.Security, request.Payload.CustomerId,
            request.ContractName, request.CorrelationId, cancellationToken).ConfigureAwait(false);
        if (accessError is not null) { return Rejected<BuildRetrievalContext, RetrievalContext>(request, accessError); }

        var records = index.FindAuthorized(request.Payload.CustomerId,
            request.Security.Access.AuthorizationContextId, request.Payload.SearchRecordIds);
        if (records.Length != request.Payload.SearchRecordIds.Length)
        {
            return ContractResult<RetrievalContext>.Rejected(request, "search.context.access-denied",
                ContractErrorCategory.AccessDenied, "The requested retrieval context is unavailable in the current access boundary.");
        }

        var items = records.Select(record => ToHit(record, 1)).ToImmutableArray();
        return ContractResult<RetrievalContext>.Succeeded(request,
            new(items, SearchAuthority.DerivedRebuildable));
    }

    public async Task<ContractResult<IndexRefreshResult>> RefreshAsync(
        ContractRequest<IndexRefreshRequested> request,
        CancellationToken cancellationToken = default)
    {
        var contractError = ContractGuard.Validate(request, SearchContractNames.IndexRefreshRequested);
        if (contractError is not null) { return Rejected<IndexRefreshRequested, IndexRefreshResult>(request, contractError); }
        if (!ValidCustomer(request.Payload.CustomerId, request.Security))
        {
            return ContractResult<IndexRefreshResult>.Rejected(request, "search.refresh.invalid",
                ContractErrorCategory.ValidationError, "The refresh request must match the current customer boundary.");
        }

        var accessError = await authorizationPolicy.AuthorizeAsync(request.Security, request.Payload.CustomerId,
            request.ContractName, request.CorrelationId, cancellationToken).ConfigureAwait(false);
        if (accessError is not null) { return Rejected<IndexRefreshRequested, IndexRefreshResult>(request, accessError); }
        return ContractResult<IndexRefreshResult>.Succeeded(request, index.Rebuild(projectionSource.ReadAll()));
    }

    private async Task<ContractResult<SearchResults>> SearchAsync<TPayload>(
        ContractRequest<TPayload> request,
        string expectedContract,
        ImmutableHashSet<SearchResultType> resultTypes,
        CancellationToken cancellationToken)
        where TPayload : notnull
    {
        var contractError = ContractGuard.Validate(request, expectedContract);
        if (contractError is not null) { return Rejected<TPayload, SearchResults>(request, contractError); }

        var (customerId, query, matchMode, limit) = request.Payload switch
        {
            SearchDocuments value => (value.CustomerId, value.Query, value.MatchMode, value.Limit),
            SearchFinancialData value => (value.CustomerId, value.Query, value.MatchMode, value.Limit),
            _ => throw new InvalidOperationException("Unsupported search payload."),
        };
        if (!ValidCustomer(customerId, request.Security) || string.IsNullOrWhiteSpace(query) ||
            query.Length > 256 || limit is < 1 or > 50 || !Enum.IsDefined(matchMode))
        {
            return ContractResult<SearchResults>.Rejected(request, "search.query.invalid",
                ContractErrorCategory.ValidationError, "A customer-scoped query, valid match mode and limit from 1 to 50 are required.");
        }

        var accessError = await authorizationPolicy.AuthorizeAsync(request.Security, customerId,
            request.ContractName, request.CorrelationId, cancellationToken).ConfigureAwait(false);
        if (accessError is not null) { return Rejected<TPayload, SearchResults>(request, accessError); }

        var results = index.Search(customerId, request.Security.Access.AuthorizationContextId,
            resultTypes, query, matchMode, limit).Select(item => ToHit(item.Record, item.Score)).ToImmutableArray();
        return ContractResult<SearchResults>.Succeeded(request, new(query.Trim(), matchMode, results));
    }

    private static bool ValidCustomer(string customerId, TrustedSecurityContext security) =>
        !string.IsNullOrWhiteSpace(customerId) && customerId == security.Access.CustomerId;

    private static SearchHit ToHit(DerivedSearchRecord record, double score) =>
        new(record.SearchRecordId, record.ResultType, record.Title, record.SearchableText,
            record.AuthoritativeOwner, SearchAuthority.DerivedRebuildable, score, record.Source);

    private static ContractResult<TResult> Rejected<TPayload, TResult>(
        ContractRequest<TPayload> request,
        ContractError error) =>
        new(request.ContractName, request.ContractVersion, request.RequestId, request.CorrelationId,
            ContractOutcome.Rejected, default, error);
}
