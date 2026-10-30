using System.Globalization;
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

        var records = await source.ReadCurrentCustomerAsync(
            "reference-customer",
            "decision-search",
            Security(),
            SearchContractNames.SearchDocuments,
            "search-correlation",
            CancellationToken.None);

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

    [Theory]
    [InlineData("ASSET", false)]
    [InlineData("PROPERTY", false)]
    [InlineData("VEHICLE", false)]
    [InlineData("ASSET", true)]
    [InlineData("PROPERTY", true)]
    [InlineData("VEHICLE", true)]
    public async Task BuildsOnlySupportedFinancialFactsWithExactProvenance(string assetType, bool canonicalCategories)
    {
        var handler = new OwnerHandler { AssetType = assetType, CanonicalCategories = canonicalCategories };
        var source = Source(handler, Accessor(SearchContractNames.SearchFinancialData));

        var records = await source.ReadCurrentCustomerAsync(
            "reference-customer",
            "decision-search",
            Security(),
            SearchContractNames.SearchFinancialData,
            "search-correlation",
            CancellationToken.None);

        Assert.Equal(9, records.Length);
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
        Assert.Contains(records, item => item.Source.SourceObjectId == "ff-tax" && item.Source.ProvenanceReferenceId == "provenance-tax" && item.SearchableText.Contains("not assessed liability", StringComparison.Ordinal));
        Assert.Contains(records, item => item.Source.SourceObjectType == "FinancialGoal" &&
            item.Source.SourceObjectId == "goal-001" && item.Source.SourceReferenceId == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" &&
            item.Source.ProvenanceReferenceId is null && item.SearchableText.Contains("not advice", StringComparison.Ordinal));
        Assert.Contains(records, item =>
            item.Source.SourceObjectType == "TaxRecord" &&
            item.Source.SourceObjectId == "tax-record-001" &&
            item.Source.SourceReferenceId == "ff-tax" &&
            item.Source.ProvenanceReferenceId == "provenance-tax" &&
            item.Title == "Income tax return" &&
            item.SearchableText.Contains("not assessed tax liability", StringComparison.Ordinal));
        Assert.DoesNotContain(records, item => item.Source.SourceObjectId == "ff-unsupported");
        Assert.DoesNotContain(records, item => item.ResultType == SearchResultType.Calculation);
    }

    [Fact]
    public async Task SeparateSearchFamiliesReturnIndependentOwnerSnapshots()
    {
        var handler = new OwnerHandler();
        var accessor = Accessor(SearchContractNames.SearchDocuments);
        var documents = Source(handler, accessor);

        var documentRecords = await documents.ReadCurrentCustomerAsync(
            "reference-customer",
            "decision-search",
            Security(),
            SearchContractNames.SearchDocuments,
            "search-correlation-documents",
            CancellationToken.None);

        // The same source instance receives the next current-customer query under the next owner proof.
        accessor.HttpContext = Accessor(SearchContractNames.SearchFinancialData).HttpContext;

        var records = await documents.ReadCurrentCustomerAsync(
            "reference-customer",
            "decision-search",
            Security(),
            SearchContractNames.SearchFinancialData,
            "search-correlation-financial",
            CancellationToken.None);

        Assert.Single(documentRecords);
        Assert.Equal(9, records.Length);
        Assert.DoesNotContain(records, item => item.ResultType == SearchResultType.Document);
        Assert.Equal(9, records.Count(item => item.ResultType == SearchResultType.Financial));
    }

    [Fact]
    public async Task CrossCustomerFinancialProvenanceFailsClosed()
    {
        var handler = new OwnerHandler { CrossCustomerProvenance = true };
        var source = Source(handler, Accessor(SearchContractNames.SearchFinancialData));

        var failure = await Assert.ThrowsAsync<SearchSourceException>(() =>
            source.ReadCurrentCustomerAsync(
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
            source.ReadCurrentCustomerAsync(
                "reference-customer",
                "decision-search",
                Security(),
                SearchContractNames.SearchDocuments,
                "search-correlation",
                CancellationToken.None));

        Assert.Equal("search.source.boundary-invalid", failure.Error.Code);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(SearchContractNames.BuildRetrievalContext)]
    [InlineData(SearchContractNames.IndexRefreshRequested)]
    public async Task RetrievalAndRefreshReadBothOwnersWithoutCachedFamilies(string operation)
    {
        var handler = new OwnerHandler();
        var source = Source(handler, Accessor(operation));
        var records = await source.ReadCurrentCustomerAsync("reference-customer", "decision-search", Security(), operation, "correlation", CancellationToken.None);
        Assert.Equal(10, records.Length);
        Assert.Single(records, x => x.ResultType == SearchResultType.Document);
        Assert.Equal(9, records.Count(x => x.ResultType == SearchResultType.Financial));
        Assert.Contains(handler.Requests, x => x.Path == "/contracts/app-02/evidence-list/v1");
        Assert.Contains(handler.Requests, x => x.Path == "/contracts/cid-030/v1");
        Assert.Throws<InvalidOperationException>(() => source.ReadAll());
        Assert.Throws<InvalidOperationException>(() => source.ReadForCustomer("reference-customer"));
    }

    [Theory]
    [InlineData(SearchContractNames.BuildRetrievalContext)]
    [InlineData(SearchContractNames.IndexRefreshRequested)]
    public async Task CombinedSnapshotDoesNotReturnDocumentsWhenFinancialOwnerFails(string operation)
    {
        var source = Source(new OwnerHandler { CrossCustomerProvenance = true }, Accessor(operation));
        var error = await Assert.ThrowsAsync<SearchSourceException>(() => source.ReadCurrentCustomerAsync("reference-customer", "decision-search", Security(), operation, "correlation", CancellationToken.None));
        Assert.Equal(ContractErrorCategory.AccessDenied, error.Error.Category);
    }

    [Fact]
    public async Task ReviewedTaxRecordUsesExactHistoricalSourceAndProvenance()
    {
        var handler = new OwnerHandler();
        var records = await Source(handler, Accessor(SearchContractNames.SearchFinancialData)).ReadCurrentCustomerAsync(
            "reference-customer", "decision-search", Security(), SearchContractNames.SearchFinancialData,
            "correlation", CancellationToken.None);

        var record = Assert.Single(records, item => item.Source.SourceObjectType == "TaxRecord");
        Assert.Equal("tax-record-001", record.Source.SourceObjectId);
        Assert.Equal("ff-tax", record.Source.SourceReferenceId);
        Assert.Equal("provenance-tax", record.Source.ProvenanceReferenceId);
        Assert.Contains(handler.Requests, request => request.Path == "/local/v1/customers/reference-customer/tax-records");
        Assert.Contains(handler.Requests, request => request.Path == "/contracts/cid-032/v1");
    }

    [Theory]
    [InlineData("customer", "search.source.tax-record-boundary-invalid")]
    [InlineData("source", "search.source.tax-record-boundary-invalid")]
    [InlineData("provenance", "search.source.tax-record-boundary-invalid")]
    [InlineData("duplicate", "search.source.corpus-limit")]
    [InlineData("has-more", "search.source.corpus-limit")]
    public async Task InvalidOrIncompleteTaxRecordProjectionFailsClosed(string mutation, string code)
    {
        var handler = new OwnerHandler { TaxRecordMutation = mutation };
        var error = await Assert.ThrowsAsync<SearchSourceException>(() =>
            Source(handler, Accessor(SearchContractNames.SearchFinancialData)).ReadCurrentCustomerAsync(
                "reference-customer", "decision-search", Security(), SearchContractNames.SearchFinancialData,
                "correlation", CancellationToken.None));
        Assert.Equal(code, error.Error.Code);
    }

    [Fact]
    public async Task CustomerDefinedFinancialGoalUsesExactCurrentOwnerVersion()
    {
        var handler = new OwnerHandler();
        var records = await Source(handler, Accessor(SearchContractNames.SearchFinancialData)).ReadCurrentCustomerAsync(
            "reference-customer", "decision-search", Security(), SearchContractNames.SearchFinancialData,
            "correlation", CancellationToken.None);

        var record = Assert.Single(records, item => item.Source.SourceObjectType == "FinancialGoal");
        Assert.Equal("goal-001", record.Source.SourceObjectId);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", record.Source.SourceReferenceId);
        Assert.Null(record.Source.ProvenanceReferenceId);
        Assert.Contains("target 500000.00 USD by 2030-06-30", record.SearchableText, StringComparison.Ordinal);
        Assert.Contains("reported status Paused", record.SearchableText, StringComparison.Ordinal);
        Assert.Contains("customer priority High", record.SearchableText, StringComparison.Ordinal);
        Assert.Contains("not advice", record.SearchableText, StringComparison.Ordinal);
        Assert.Contains(handler.Requests, request => request.Path == "/local/v1/customers/reference-customer/financial-goals");
        Assert.Contains(handler.Requests, request => request.Path == "/local/v1/customers/reference-customer/financial-goals/goal-001");
    }

    [Fact]
    public async Task CustomerDefinedGoalRemainsSearchableBeforeAnySourceFactProfileExists()
    {
        var handler = new OwnerHandler { ProfileMissing = true };
        var records = await Source(handler, Accessor(SearchContractNames.SearchFinancialData)).ReadCurrentCustomerAsync(
            "reference-customer", "decision-search", Security(), SearchContractNames.SearchFinancialData,
            "correlation", CancellationToken.None);

        var goal = Assert.Single(records);
        Assert.Equal("FinancialGoal", goal.Source.SourceObjectType);
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/local/v1/customers/reference-customer/tax-records");
    }

    [Theory]
    [InlineData("customer", "search.source.financial-goal-boundary-invalid")]
    [InlineData("exact-customer", "search.source.financial-goal-boundary-invalid")]
    [InlineData("exact-revision", "search.source.financial-goal-boundary-invalid")]
    [InlineData("invalid-status", "search.source.financial-goal-boundary-invalid")]
    [InlineData("null", "search.source.corpus-limit")]
    [InlineData("duplicate", "search.source.corpus-limit")]
    [InlineData("has-more", "search.source.corpus-limit")]
    public async Task InvalidOrIncompleteFinancialGoalProjectionFailsClosed(string mutation, string code)
    {
        var handler = new OwnerHandler { FinancialGoalMutation = mutation };
        var error = await Assert.ThrowsAsync<SearchSourceException>(() =>
            Source(handler, Accessor(SearchContractNames.SearchFinancialData)).ReadCurrentCustomerAsync(
                "reference-customer", "decision-search", Security(), SearchContractNames.SearchFinancialData,
                "correlation", CancellationToken.None));
        Assert.Equal(code, error.Error.Code);
    }

    [Fact]
    public async Task ReadsAllPagesBeyondOneHundredWithExactSources()
    {
        var handler = new OwnerHandler { DocumentCount = 205 };
        var records = await Source(handler, Accessor(SearchContractNames.SearchDocuments)).ReadCurrentCustomerAsync(
            "reference-customer", "decision-search", Security(), SearchContractNames.SearchDocuments, "correlation", CancellationToken.None);
        Assert.Equal(205, records.Length);
        Assert.Equal(205, records.Select(x => x.Source.SourceObjectId).Distinct().Count());
        Assert.Contains(records, x => x.Source.SourceObjectId == "document-version-205");
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task CorpusSafetyLimitFailsExplicitlyInsteadOfTruncating()
    {
        var handler = new OwnerHandler { DocumentCount = 10_001 };
        var error = await Assert.ThrowsAsync<SearchSourceException>(() => Source(handler, Accessor(SearchContractNames.SearchDocuments)).ReadCurrentCustomerAsync(
            "reference-customer", "decision-search", Security(), SearchContractNames.SearchDocuments, "correlation", CancellationToken.None));
        Assert.Equal("search.source.corpus-limit", error.Error.Code);
        Assert.Equal(100, handler.Requests.Count);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("duplicate")]
    [InlineData("customer")]
    [InlineData("cursor")]
    public async Task LaterPageFailureDoesNotReturnPartialCorpus(string failure)
    {
        var handler = new OwnerHandler { DocumentCount = 205, PageFailure = failure };
        await Assert.ThrowsAsync<SearchSourceException>(() => Source(handler, Accessor(SearchContractNames.SearchDocuments)).ReadCurrentCustomerAsync(
            "reference-customer", "decision-search", Security(), SearchContractNames.SearchDocuments, "correlation", CancellationToken.None));
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
        public int DocumentCount { get; set; } = 1;
        public string? PageFailure { get; set; }
        public string AssetType { get; set; } = "ASSET";
        public bool CanonicalCategories { get; set; }
        public bool CrossCustomerProvenance { get; set; }
        public bool ProfileMissing { get; set; }
        public string? TaxRecordMutation { get; set; }
        public string? FinancialGoalMutation { get; set; }
        public List<(string Path, string Tenant, string Session, string Workload)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            using var document = body.Length == 0 ? null : JsonDocument.Parse(body);
            Requests.Add((
                request.RequestUri!.AbsolutePath,
                request.Headers.GetValues("X-Monergy-Tenant").Single(),
                request.Headers.GetValues("X-Monergy-Session").Single(),
                document is null ? "search-retrieval" : document.RootElement.GetProperty("security").GetProperty("workload")
                    .GetProperty("workloadId").GetString()!));

            return request.RequestUri.AbsolutePath switch
            {
                "/contracts/app-02/evidence-list/v1" => Evidence(body),
                "/contracts/cid-030/v1" => Profile(body),
                "/contracts/cid-032/v1" => Fact(body),
                "/contracts/cid-033/v1" => Provenance(body),
                "/local/v1/customers/reference-customer/tax-records" => TaxRecords(),
                "/local/v1/customers/reference-customer/financial-goals" => FinancialGoals(),
                "/local/v1/customers/reference-customer/financial-goals/goal-001" => FinancialGoal(true),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private CustomerFinancialGoal Goal(bool exact = false) => new(
            "goal-001", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            FinancialGoalMutation == (exact ? "exact-customer" : "customer") ? "other-customer" : "reference-customer",
            new FinancialGoalDraft("Emergency fund", 500000m, "USD", new DateOnly(2030, 6, 30)),
            "A100", DateTimeOffset.Parse("2026-10-16T11:00:00Z", CultureInfo.InvariantCulture),
            "CUSTOMER_DEFINED", exact && FinancialGoalMutation == "exact-revision" ? 3 : 4,
            FinancialGoalMutation == "invalid-status" ? "UNKNOWN" : "PAUSED", new DateOnly(2026, 10, 15),
            "CUSTOMER_REPORTED", "HIGH", new DateOnly(2026, 10, 15), "CUSTOMER_REPORTED");

        private HttpResponseMessage FinancialGoals()
        {
            var goal = Goal();
            IReadOnlyList<CustomerFinancialGoal> items = FinancialGoalMutation == "duplicate" ? [goal, goal] :
                FinancialGoalMutation == "null" ? [null!] : [goal];
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new FinancialGoalList(items, FinancialGoalMutation == "has-more"), options: ContractJson.Options),
            };
        }

        private HttpResponseMessage FinancialGoal(bool exact) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(Goal(exact), options: ContractJson.Options),
        };

        private HttpResponseMessage TaxRecords()
        {
            var record = new ReviewedTaxRecord(
                "tax-record-001", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                TaxRecordMutation == "customer" ? "other-customer" : "reference-customer",
                new TaxRecordDraft("Income tax return", "India", "Income tax", 14000m, "PAID", "2025-26",
                    new DateOnly(2025, 4, 1), new DateOnly(2026, 3, 31),
                    TaxRecordMutation == "source" ? "ff-other" : "ff-tax", 1),
                "USD", TaxRecordMutation == "provenance" ? "provenance-other" : "provenance-tax",
                "document-version-tax", "evidence-tax", "A100",
                DateTimeOffset.Parse("2026-10-16T10:00:00Z", CultureInfo.InvariantCulture),
                "CUSTOMER_REVIEWED");
            IReadOnlyList<ReviewedTaxRecord> items = TaxRecordMutation == "duplicate" ? [record, record] : [record];
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new TaxRecordList(items, TaxRecordMutation == "has-more"), options: ContractJson.Options),
            };
        }

        private HttpResponseMessage Evidence(string body)
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
                DateTimeOffset.Parse("2026-10-16T08:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-10-16T08:00:00Z", CultureInfo.InvariantCulture));
            var offset = request.Payload.AfterVersionId is null ? 0 : int.Parse(request.Payload.AfterVersionId.Split('-')[^1], CultureInfo.InvariantCulture);
            if (offset > 0 && PageFailure == "denied") return new(HttpStatusCode.Forbidden);
            var items = Enumerable.Range(offset + 1, Math.Min(100, DocumentCount - offset))
                .Select(i => item with { DocumentVersionId = $"document-version-{i:000}", EvidenceId = $"evidence-{i:000}" }).ToArray();
            if (offset > 0 && PageFailure == "duplicate") items[0] = item;
            var next = offset + items.Length < DocumentCount ? items[^1].DocumentVersionId : null;
            if (offset > 0 && PageFailure == "cursor") next = "forged-cursor";
            return Success(request, new CustomerEvidenceLibrary(offset > 0 && PageFailure == "customer" ? "other" : "reference-customer", items, next));
        }

        private HttpResponseMessage Profile(string body)
        {
            var request = JsonSerializer.Deserialize<ContractRequest<GetFinancialProfile>>(
                body, ContractJson.Options)!;
            if (ProfileMissing)
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        ContractResult<FinancialProfileDetails>.Rejected(
                            request, "financial-profile.not-found", ContractErrorCategory.NotFound, "Not found."),
                        options: ContractJson.Options),
                };
            var at = DateTimeOffset.Parse("2026-10-16T09:00:00Z", CultureInfo.InvariantCulture);
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
                    AssetType, "Document-stated asset figure", 900000m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-asset"),
                new(
                    "ff-investment", "financial-profile-reference-customer", "reference-customer",
                    "INVESTMENT", "Document-stated investment figure", 500m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-investment"),
                new(
                    "ff-insurance", "financial-profile-reference-customer", "reference-customer",
                    CanonicalCategories ? FinancialObjectTypes.InsurancePolicy : "INSURANCE", "Document-stated insurance figure", 100m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-insurance"),
                new(
                    "ff-tax", "financial-profile-reference-customer", "reference-customer",
                    CanonicalCategories ? FinancialObjectTypes.TaxRecord : "TAX", "Tax source figure", 14000m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-tax"),
                new(
                    "ff-unsupported", "financial-profile-reference-customer", "reference-customer",
                    "UNSUPPORTED", "Unsupported source fixture", 10m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-tax"),
            ];
            return Success(request, new FinancialProfileDetails(
                "financial-profile-reference-customer",
                "reference-customer",
                3,
                at,
                facts));
        }

        private static HttpResponseMessage Fact(string body)
        {
            var request = JsonSerializer.Deserialize<ContractRequest<GetFinancialFact>>(body, ContractJson.Options)!;
            var fact = new AuthoritativeFinancialFact(
                "ff-tax", "financial-profile-reference-customer", "reference-customer",
                FinancialObjectTypes.TaxRecord, "Tax source figure", 14000m, "USD",
                new DateOnly(2026, 10, 1), 1, "provenance-tax");
            return Success(request, fact);
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
            var tax = request.Payload.FinancialProvenanceId == "provenance-tax";
            var customer = CrossCustomerProvenance ? "other-customer" : "reference-customer";
            return Success(request, new FinancialProvenance(
                request.Payload.FinancialProvenanceId,
                income ? "ff-income" : loan ? "ff-loan" : asset ? "ff-asset" : investment ? "ff-investment" : insurance ? "ff-insurance" : tax ? "ff-tax" : "ff-expense", 
                customer,
                income ? "evidence-income" : loan ? "evidence-loan" : asset ? "evidence-asset" : investment ? "evidence-investment" : insurance ? "evidence-insurance" : tax ? "evidence-tax" : "evidence-expense", 
                income ? "document-version-income" : loan ? "document-version-loan" : asset ? "document-version-asset" : investment ? "document-version-investment" : insurance ? "document-version-insurance" : tax ? "document-version-tax" : "document-version-expense", 
                income ? "source-income" : loan ? "source-loan" : asset ? "source-asset" : investment ? "source-investment" : insurance ? "source-insurance" : tax ? "source-tax" : "source-expense", 
                "extractor/1",
                "validator/1",
                "reviewed-document-facts/1.0.0",
                "A100",
                "document-intelligence",
                DateTimeOffset.Parse("2026-10-16T08:00:00Z", CultureInfo.InvariantCulture),
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
