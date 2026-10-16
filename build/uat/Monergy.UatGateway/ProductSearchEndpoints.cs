using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.UatGateway;

public sealed record ProductSearchRequest(string Query, string Scope);

public static class ProductSearchEndpoints
{
    public static void AddProductSearch(this WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;
        builder.Services.AddSingleton<ProductSearchClient>();
    }

    public static void MapProductSearch(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;

        app.MapPost("/product-api/v1/search", async (
            ProductSearchRequest submission,
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductSearchClient search) =>
        {
            var query = submission.Query?.Trim() ?? "";
            var scope = submission.Scope?.Trim().ToLowerInvariant() ?? "";
            if (query.Length is < 1 or > 256)
                throw new TenantBoundaryException("SEARCH_QUERY_INVALID", 400);
            if (scope is not ("all" or "documents" or "financial"))
                throw new TenantBoundaryException("SEARCH_SCOPE_INVALID", 400);

            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(
                session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var decision = await access.EvaluateCustomerAsync(
                http, "search.query.execute", customer.CustomerId).ConfigureAwait(false);
            if (decision.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);
            var security = Security(session, customer.CustomerId, decision);

            var combined = new List<SearchHit>();
            if (scope is "all" or "documents")
            {
                var documents = await search.DocumentsAsync(
                    session.TenantId,
                    session.AuthenticationContextId,
                    Request(
                        SearchContractNames.SearchDocuments,
                        security,
                        new SearchDocuments(customer.CustomerId, query, SearchMatchMode.Lexical, 20)),
                    http.RequestAborted).ConfigureAwait(false);
                combined.AddRange(ReadResult(documents, query, SearchResultType.Document));
            }

            if (scope is "all" or "financial")
            {
                var financial = await search.FinancialAsync(
                    session.TenantId,
                    session.AuthenticationContextId,
                    Request(
                        SearchContractNames.SearchFinancialData,
                        security,
                        new SearchFinancialData(customer.CustomerId, query, SearchMatchMode.Lexical, 20)),
                    http.RequestAborted).ConfigureAwait(false);
                combined.AddRange(ReadResult(financial, query, SearchResultType.Financial));
            }

            var items = combined
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SearchRecordId, StringComparer.Ordinal)
                .Take(20)
                .ToImmutableArray();
            return Results.Ok(new SearchResults(query, SearchMatchMode.Lexical, items));
        });
    }

    private static ContractRequest<TPayload> Request<TPayload>(
        string contractName,
        TrustedSecurityContext security,
        TPayload payload)
    {
        var requestId = "request-product-search-" + Guid.NewGuid().ToString("N");
        return new ContractRequest<TPayload>(
            contractName,
            ContractGuard.CurrentVersion,
            requestId,
            "correlation-product-search-" + Guid.NewGuid().ToString("N"),
            null,
            security,
            null,
            payload);
    }

    private static IReadOnlyList<SearchHit> ReadResult(
        ContractResult<SearchResults> result,
        string query,
        SearchResultType expectedType)
    {
        if (result.Outcome != ContractOutcome.Success || result.Data is null)
            throw new TenantBoundaryException(result.Error?.Category switch
            {
                ContractErrorCategory.AccessDenied or ContractErrorCategory.AuthenticationRequired => "ACCESS_DENIED",
                ContractErrorCategory.DependencyFailure or ContractErrorCategory.TemporarilyUnavailable => "SEARCH_OWNER_UNAVAILABLE",
                ContractErrorCategory.UnsupportedOperation => "SEARCH_MODE_UNSUPPORTED",
                _ => "SEARCH_REJECTED",
            }, result.Error?.Category switch
            {
                ContractErrorCategory.AccessDenied or ContractErrorCategory.AuthenticationRequired => 403,
                ContractErrorCategory.DependencyFailure or ContractErrorCategory.TemporarilyUnavailable => 503,
                ContractErrorCategory.UnsupportedOperation => 422,
                _ => 400,
            });

        var data = result.Data;
        if (!string.Equals(data.Query, query, StringComparison.Ordinal) ||
            data.MatchMode != SearchMatchMode.Lexical ||
            data.Items.Any(item =>
                item.ResultType != expectedType ||
                item.RepresentationState != "DERIVED_REBUILDABLE" ||
                item.Source.CalculationLineageReferenceId is not null ||
                string.IsNullOrWhiteSpace(item.SearchRecordId) ||
                string.IsNullOrWhiteSpace(item.Title) ||
                string.IsNullOrWhiteSpace(item.Source.SourceObjectId) ||
                string.IsNullOrWhiteSpace(item.Source.SourceReferenceId) ||
                expectedType == SearchResultType.Document &&
                    (item.AuthoritativeOwner != "Evidence Service" ||
                     item.Source.SourceObjectType != "DocumentVersion") ||
                expectedType == SearchResultType.Financial &&
                    (item.AuthoritativeOwner != "Financial Profile Service" ||
                     item.Source.SourceObjectType != "FinancialFact" ||
                     string.IsNullOrWhiteSpace(item.Source.ProvenanceReferenceId))))
            throw new TenantBoundaryException("SEARCH_OWNER_RESPONSE_INVALID", 503);

        return data.Items;
    }

    private static TrustedSecurityContext Security(
        CustomerOwnerSession session,
        string customerId,
        CustomerAccessDecision decision) => new(
        new ActorContext(session.ActorId, "Customer", DateTimeOffset.UtcNow, session.AuthenticationContextId),
        new WorkloadContext("customer-web", "local-uat-workspace:" + session.TenantId),
        new AccessContext("customer-advice", null, decision.DecisionId, customerId));
}

public sealed class ProductSearchClient : IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1_048_576 };
    private readonly Uri endpoint;

    public ProductSearchClient(IConfiguration configuration)
    {
        endpoint = TenantAccessIntegration.LoopbackRoot(
            TenantAccessIntegration.Required(configuration, "Monergy:Product:SearchRetrievalUrl"));
    }

    public Task<ContractResult<SearchResults>> DocumentsAsync(
        string tenantId,
        string sessionId,
        ContractRequest<SearchDocuments> request,
        CancellationToken cancellationToken) =>
        SendAsync<SearchDocuments, SearchResults>(
            tenantId, sessionId, "contracts/cid-042/v1", request, cancellationToken);

    public Task<ContractResult<SearchResults>> FinancialAsync(
        string tenantId,
        string sessionId,
        ContractRequest<SearchFinancialData> request,
        CancellationToken cancellationToken) =>
        SendAsync<SearchFinancialData, SearchResults>(
            tenantId, sessionId, "contracts/cid-043/v1", request, cancellationToken);

    private async Task<ContractResult<TData>> SendAsync<TPayload, TData>(
        string tenantId,
        string sessionId,
        string path,
        ContractRequest<TPayload> request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, path))
        {
            Content = JsonContent.Create(request, options: ContractJson.Options),
        };
        message.Headers.Add("X-Monergy-Tenant", tenantId);
        message.Headers.Add("X-Monergy-Session", sessionId);

        try
        {
            using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new TenantBoundaryException("TENANT_SESSION_INVALID", 401);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new TenantBoundaryException("ACCESS_DENIED", 403);
            if ((int)response.StatusCode >= 500 || (int)response.StatusCode is >= 300 and < 400)
                throw new TenantBoundaryException("SEARCH_OWNER_UNAVAILABLE", 503);

            var result = await response.Content.ReadFromJsonAsync<ContractResult<TData>>(
                ContractJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("SEARCH_OWNER_RESPONSE_INVALID", 503);
            if (result.ContractName != request.ContractName ||
                result.ContractVersion != request.ContractVersion ||
                result.RequestId != request.RequestId ||
                result.CorrelationId != request.CorrelationId)
                throw new TenantBoundaryException("SEARCH_OWNER_RESPONSE_INVALID", 503);
            return result;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        {
            throw new TenantBoundaryException("SEARCH_OWNER_UNAVAILABLE", 503);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TenantBoundaryException("SEARCH_OWNER_UNAVAILABLE", 503);
        }
    }

    public void Dispose() => client.Dispose();
}
