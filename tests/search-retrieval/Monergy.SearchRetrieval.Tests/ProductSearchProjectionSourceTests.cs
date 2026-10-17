using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.SearchRetrieval.Application;
using Monergy.Services.SearchRetrieval.Infrastructure;
using Xunit;

namespace Monergy.SearchRetrieval.Tests;

public sealed class ProductSearchProjectionSourceTests
{
    [Fact]
    public async Task BuildsDocumentProjectionFromCurrentEvidenceWithSearchWorkload()
    {
        var handler = new OwnerHandler();
        var source = Source(handler, Accessor(SearchContractNames.SearchDocuments));

        await source.PrepareCustomerAsync(
            "reference-customer",
            "decision-search",
            Security(),
            SearchContractNames.SearchDocuments,
            "search-correlation",
            CancellationToken.None);

        var records = source.ReadForCustomer("reference-customer");
        var document = Assert.Single(records);
        Assert.Equal(SearchResultType.Document, document.ResultType);
        Assert.Equal("salary-statement.txt", document.Title);
        Assert.Equal("document-version-001", document.Source.SourceObjectId);
        Assert.Equal("evidence-001", document.Source.SourceReferenceId);
        Assert.Equal("decision-search", document.RequiredAuthorizationContextId);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("T001", request.Tenant);
            Assert.Equal("customer-session", request.Session);
            Assert.Equal("search-retrieval", request.Workload);
        });
    }

    [Fact]
    public async Task BuildsOnlySupportedFinancialFactsWithExactProvenance()
    {
        var handler = new OwnerHandler();
        var source = Source(handler, Accessor(SearchContractNames.SearchFinancialData));

        await source.PrepareCustomerAsync(
            "reference-customer",
            "decision-search",
            Security(),
            SearchContractNames.SearchFinancialData,
            "search-correlation",
            CancellationToken.None);

        var records = source.ReadForCustomer("reference-customer");
        Assert.Equal(6, records.Length);
        Assert.Contains(records, item =>
            item.ResultType == SearchResultType.Financial &&
            item.Title == "Employment income" &&
            item.Source.SourceObjectId == "ff-income" &&
            item.Source.ProvenanceReferenceId == "provenance-income");
        Assert.Contains(records, item =>
            item.Title == "Rent" &&
            item.Source.SourceObjectId == "ff-expense");
        Assert.Contains(records, item =>
            item.Title == "Loan source amount" &&
            item.SearchableText.Contains("not outstanding balance", StringComparison.Ordinal) &&
            item.Source.SourceObjectId == "ff-loan" &&
            item.Source.ProvenanceReferenceId == "provenance-loan");
        Assert.Contains(records, item => item.Title == "Document-stated asset figure" && item.SearchableText.Contains("not verified valuation", StringComparison.Ordinal) && item.Source.SourceObjectId == "ff-asset" && item.Source.ProvenanceReferenceId == "provenance-asset");
        Assert.Contains(records, item =>
            item.Title == "Document-stated investment figure" &&
            item.SearchableText.Contains("not live market value", StringComparison.Ordinal) &&
            item.Source.SourceObjectId == "ff-investment" &&
            item.Source.ProvenanceReferenceId == "provenance-investment");
        Assert.Contains(records, item => item.Source.SourceObjectId == "ff-insurance" && item.Source.ProvenanceReferenceId == "provenance-insurance" && item.SearchableText.Contains("not verified coverage", StringComparison.Ordinal));
        Assert.DoesNotContain(records, item => item.Source.SourceObjectId == "ff-unsupported-tax");
        Assert.DoesNotContain(records, item => item.ResultType == SearchResultType.Calculation);
    }

    [Fact]
    public async Task RefreshingOneSearchFamilyPreservesTheOtherCurrentFamily()
    {
        var handler = new OwnerHandler();
        var accessor = Accessor(SearchContractNames.SearchDocuments);
        var documents = Source(handler, accessor);

        await documents.PrepareCustomerAsync(
            "reference-customer",
            "decision-search",
            Security(),
            SearchContractNames.SearchDocuments,
            "search-correlation-documents",
            CancellationToken.None);

        // The same source instance receives the next current-customer query under the next owner proof.
        accessor.HttpContext = Accessor(SearchContractNames.SearchFinancialData).HttpContext;

        await documents.PrepareCustomerAsync(
            "reference-customer",
            "decision-search",
            Security(),
            SearchContractNames.SearchFinancialData,
            "search-correlation-financial",
            CancellationToken.None);

        var records = documents.ReadForCustomer("reference-customer");
        Assert.Equal(7, records.Length);
        Assert.Single(records, item => item.ResultType == SearchResultType.Document);
        Assert.Equal(6, records.Count(item => item.ResultType == SearchResultType.Financial));
    }

    [Fact]
    public async Task CrossCustomerFinancialProvenanceFailsClosed()
    {
        var handler = new OwnerHandler { CrossCustomerProvenance = true };
        var source = Source(handler, Accessor(SearchContractNames.SearchFinancialData));

        var failure = await Assert.ThrowsAsync<SearchSourceException>(() =>
            source.PrepareCustomerAsync(
                "reference-customer",
                "decision-search",
                Security(),
                SearchContractNames.SearchFinancialData,
                "search-correlation",
                CancellationToken.None));

        Assert.Equal("search.source.financial-boundary-invalid", failure.Error.Code);
    }

    [Fact]
    public async Task MissingSearchBoundaryProofFailsBeforeOwnerRead()
    {
        var handler = new OwnerHandler();
        var source = Source(handler, new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        var failure = await Assert.ThrowsAsync<SearchSourceException>(() =>
            source.PrepareCustomerAsync(
                "reference-customer",
                "decision-search",
                Security(),
                SearchContractNames.SearchDocuments,
                "search-correlation",
                CancellationToken.None));

        Assert.Equal("search.source.boundary-invalid", failure.Error.Code);
        Assert.Empty(handler.Requests);
    }

    private static ProductSearchProjectionSource Source(
        HttpMessageHandler handler,
        IHttpContextAccessor accessor)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Monergy:ExecutionZone"] = "LOCAL",
                ["Monergy:ProductSearch:Enabled"] = "true",
                ["Monergy:ProductSearch:EvidenceUrl"] = "http://127.0.0.1:5111/",
                ["Monergy:ProductSearch:FinancialProfileUrl"] = "http://127.0.0.1:5113/",
            }).Build();
        return new(new TestHttpClientFactory(handler), accessor, configuration);
    }

    private static HttpContextAccessor Accessor(string contractName)
    {
        var http = new DefaultHttpContext();
        http.Features.Set(new TenantBoundaryProof(
            "T001",
            "A100",
            "customer-session",
            "reference-customer",
            contractName,
            "search.query.execute",
            "decision-search"));
        return new HttpContextAccessor { HttpContext = http };
    }

    private static TrustedSecurityContext Security() => new(
        new ActorContext("A100", "Customer", DateTimeOffset.UnixEpoch, "customer-session"),
        new WorkloadContext("customer-web", "local-uat-workspace:T001"),
        new AccessContext("customer-advice", null, "decision-search", "reference-customer"));

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class OwnerHandler : HttpMessageHandler
    {
        public bool CrossCustomerProvenance { get; set; }
        public List<(string Path, string Tenant, string Session, string Workload)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            Requests.Add((
                request.RequestUri!.AbsolutePath,
                request.Headers.GetValues("X-Monergy-Tenant").Single(),
                request.Headers.GetValues("X-Monergy-Session").Single(),
                document.RootElement.GetProperty("security").GetProperty("workload")
                    .GetProperty("workloadId").GetString()!));

            return request.RequestUri.AbsolutePath switch
            {
                "/contracts/app-02/evidence-list/v1" => Evidence(body),
                "/contracts/cid-030/v1" => Profile(body),
                "/contracts/cid-033/v1" => Provenance(body),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Evidence(string body)
        {
            var request = JsonSerializer.Deserialize<ContractRequest<ListCustomerEvidence>>(
                body, ContractJson.Options)!;
            var item = new CustomerEvidenceItem(
                "document-001",
                "document-version-001",
                "evidence-001",
                1,
                "Customer upload",
                "salary-statement.txt",
                "text/plain",
                100,
                new string('A', 64),
                DateTimeOffset.Parse("2026-10-16T08:00:00Z"),
                DateTimeOffset.Parse("2026-10-16T08:00:00Z"));
            return Success(request, new CustomerEvidenceLibrary("reference-customer", [item]));
        }

        private static HttpResponseMessage Profile(string body)
        {
            var request = JsonSerializer.Deserialize<ContractRequest<GetFinancialProfile>>(
                body, ContractJson.Options)!;
            var at = DateTimeOffset.Parse("2026-10-16T09:00:00Z");
            IReadOnlyList<AuthoritativeFinancialFact> facts =
            [
                new(
                    "ff-income", "financial-profile-reference-customer", "reference-customer",
                    "INCOME", "Employment income", 125000m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-income"),
                new(
                    "ff-expense", "financial-profile-reference-customer", "reference-customer",
                    "EXPENSE", "Rent", 2500m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-expense"),
                new(
                    "ff-loan", "financial-profile-reference-customer", "reference-customer",
                    "LOAN", "Loan source amount", 85000m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-loan"),
                new(
                    "ff-asset", "financial-profile-reference-customer", "reference-customer",
                    "ASSET", "Document-stated asset figure", 900000m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-asset"),
                new(
                    "ff-investment", "financial-profile-reference-customer", "reference-customer",
                    "INVESTMENT", "Document-stated investment figure", 500m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-investment"),
                new(
                    "ff-insurance", "financial-profile-reference-customer", "reference-customer",
                    "INSURANCE", "Document-stated insurance figure", 100m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-insurance"),
                new(
                    "ff-unsupported-tax", "financial-profile-reference-customer", "reference-customer",
                    "TAX", "Unsupported tax fixture", 10m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-tax"),
            ];
            return Success(request, new FinancialProfileDetails(
                "financial-profile-reference-customer",
                "reference-customer",
                3,
                at,
                facts));
        }

        private HttpResponseMessage Provenance(string body)
        {
            var request = JsonSerializer.Deserialize<ContractRequest<GetFinancialProvenance>>(
                body, ContractJson.Options)!;
            var income = request.Payload.FinancialProvenanceId == "provenance-income";
            var loan = request.Payload.FinancialProvenanceId == "provenance-loan";
            var asset = request.Payload.FinancialProvenanceId == "provenance-asset";
            var investment = request.Payload.FinancialProvenanceId == "provenance-investment";
            var insurance = request.Payload.FinancialProvenanceId == "provenance-insurance";
            var customer = CrossCustomerProvenance ? "other-customer" : "reference-customer";
            return Success(request, new FinancialProvenance(
                request.Payload.FinancialProvenanceId,
                income ? "ff-income" : loan ? "ff-loan" : asset ? "ff-asset" : investment ? "ff-investment" : insurance ? "ff-insurance" : "ff-expense", 
                customer,
                income ? "evidence-income" : loan ? "evidence-loan" : asset ? "evidence-asset" : investment ? "evidence-investment" : insurance ? "evidence-insurance" : "evidence-expense", 
                income ? "document-version-income" : loan ? "document-version-loan" : asset ? "document-version-asset" : investment ? "document-version-investment" : insurance ? "document-version-insurance" : "document-version-expense", 
                income ? "source-income" : loan ? "source-loan" : asset ? "source-asset" : investment ? "source-investment" : insurance ? "source-insurance" : "source-expense", 
                "extractor/1",
                "validator/1",
                "reviewed-document-facts/1.0.0",
                "A100",
                "document-intelligence",
                DateTimeOffset.Parse("2026-10-16T08:00:00Z"),
                "financial-correlation",
                null));
        }

        private static HttpResponseMessage Success<TPayload, TData>(
            ContractRequest<TPayload> request,
            TData data) =>
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    ContractResult<TData>.Succeeded(request, data),
                    options: ContractJson.Options),
            };
    }
}
