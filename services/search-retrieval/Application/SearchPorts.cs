using System.Collections.Immutable;
using Monergy.Contracts;
using Monergy.Services.SearchRetrieval.Domain;

namespace Monergy.Services.SearchRetrieval.Application;

public interface ISearchAuthorizationPolicy
{
    Task<ContractError?> AuthorizeAsync(
        TrustedSecurityContext context,
        string customerId,
        string operation,
        string correlationId,
        CancellationToken cancellationToken);
}

public interface ISearchProjectionSource
{
    ImmutableArray<DerivedSearchRecord> ReadAll();
    ImmutableArray<DerivedSearchRecord> ReadForCustomer(string customerId);
}

public interface IDerivedSearchIndex
{
    IndexRefreshResult Rebuild(IEnumerable<DerivedSearchRecord> records);
    IndexRefreshResult RebuildCustomer(string customerId, IEnumerable<DerivedSearchRecord> records);
    ImmutableArray<RankedSearchRecord> Search(
        string customerId,
        string authorizationContextId,
        ImmutableHashSet<SearchResultType> resultTypes,
        string query,
        SearchMatchMode matchMode,
        int limit);
    ImmutableArray<DerivedSearchRecord> FindAuthorized(
        string customerId,
        string authorizationContextId,
        IEnumerable<string> searchRecordIds);
}
