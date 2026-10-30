using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Reporting.Application;
using Monergy.Services.Reporting.Infrastructure;
using Xunit;

namespace Monergy.Reporting.Tests;

public sealed class ProductFinancialProfileReportSourceReaderTests
{
    private static readonly string[] ReviewReceiptIds =
    ["allocation-review", "cashflow-rate-review", "emi-share-review", "coverage-review"];

    [Theory]
    [InlineData("ASSET", false)]
    [InlineData("PROPERTY", false)]
    [InlineData("VEHICLE", false)]
    [InlineData("ASSET", true)]
    [InlineData("PROPERTY", true)]
    [InlineData("VEHICLE", true)]
    public async Task ReadsOnlyCurrentSupportedFinancialFactsWithExactProvenance(string assetType, bool canonicalCategories)
    {
        var handler = new FinancialProfileHandler { AssetType = assetType, CanonicalCategories = canonicalCategories };
        var accessor = Accessor();
        var reader = Reader(handler, accessor);

        var snapshot = await reader.ReadAsync(new(
            "reference-customer",
            Security(),
            "report-request-1",
            "report-correlation-1",
            null), CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("reference-customer", snapshot.CustomerId);
        Assert.Equal(9, snapshot.Items.Length);
        Assert.Contains(snapshot.Items, item =>
            item.Label == "Employment income" &&
            item.Value == 125000m &&
            item.Unit == "USD" &&
            item.Source.SourceId == "ff-income" &&
            item.Source.EvidenceReferenceId == "evidence-income" &&
            item.Source.FinancialProvenanceReferenceId == "provenance-income" &&
            item.Source.CalculationLineageReferenceId is null);
        Assert.Contains(snapshot.Items, item =>
            item.Label == "Rent" &&
            item.Source.SourceId == "ff-expense" &&
            item.Source.EvidenceReferenceId == "evidence-expense");
        Assert.Contains(snapshot.Items, item => item.Label == "Loan source amount" && item.Value == 85000m && item.Source.SourceId == "ff-loan" && item.Source.FinancialProvenanceReferenceId == "provenance-loan" && item.Source.CalculationLineageReferenceId is null);
        Assert.Contains(snapshot.Items, item =>
            item.Label == "Asset source figure" &&
            item.Value == 900000m &&
            item.Source.SourceId == "ff-asset" &&
            item.Source.EvidenceReferenceId == "evidence-asset" &&
            item.Source.FinancialProvenanceReferenceId == "provenance-asset" &&
            item.Source.CalculationLineageReferenceId is null);
        Assert.Contains(snapshot.Items, item =>
            item.Label == "Investment source figure" &&
            item.Value == 500m &&
            item.Source.SourceId == "ff-investment" &&
            item.Source.EvidenceReferenceId == "evidence-investment" &&
            item.Source.FinancialProvenanceReferenceId == "provenance-investment" &&
            item.Source.CalculationLineageReferenceId is null);
        Assert.Contains(snapshot.Items, item => item.Label == "Insurance source figure" && item.Source.SourceId == "ff-insurance" && item.Source.FinancialProvenanceReferenceId == "provenance-insurance" && item.Source.CalculationLineageReferenceId is null);
        Assert.Contains(snapshot.Items, item => item.Label == "Tax source figure" && item.Value == 14000m && item.Source.SourceId == "ff-tax" && item.Source.EvidenceReferenceId == "evidence-tax" && item.Source.FinancialProvenanceReferenceId == "provenance-tax" && item.Source.CalculationLineageReferenceId is null);
        Assert.Contains(snapshot.Items, item =>
            item.Label == "Income tax return — tax paid" && item.Value == 14000m && item.Unit == "USD" &&
            item.Source.SourceType == "TaxRecord" && item.Source.SourceId == "tax-record-001" &&
            item.Source.FinancialFactReferenceId == "ff-tax" && item.Source.FinancialFactRevision == 1 &&
            item.Source.EvidenceReferenceId == "evidence-tax" &&
            item.Source.FinancialProvenanceReferenceId == "provenance-tax" &&
            item.Source.DocumentVersionReferenceId == "document-version-tax" &&
            item.Source.CalculationLineageReferenceId is null);
        Assert.Contains(snapshot.Items, item =>
            item.Label == "Emergency fund — customer-defined target" && item.Value == 25000m &&
            item.Unit == "USD" && item.Source.SourceType == "FinancialGoal" &&
            item.Source.SourceId == "financial-goal-001" && item.Source.FinancialGoal is
            {
                Revision: 3,
                OperationId: "dddddddddddddddddddddddddddddddd",
                LifecycleStatus: "PAUSED",
                PriorityLevel: "HIGH",
            });
        Assert.DoesNotContain(snapshot.Items, item => item.Source.SourceId == "ff-unsupported");
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("T001", request.Tenant);
            Assert.Equal("customer-session", request.Session);
            Assert.Equal("reporting", request.Workload);
        });
    }

    [Fact]
    public async Task MissingReportingBoundaryProofFailsClosedBeforeOwnerRead()
    {
        var handler = new FinancialProfileHandler();
        var reader = Reader(handler, new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        var failure = await Assert.ThrowsAsync<ReportSourceException>(() => reader.ReadAsync(new(
            "reference-customer",
            Security(),
            "report-request-1",
            "report-correlation-1",
            null), CancellationToken.None));

        Assert.Equal("report.source.boundary-invalid", failure.Error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CrossCustomerProvenanceFailsClosed()
    {
        var handler = new FinancialProfileHandler { CrossCustomerProvenance = true };
        var reader = Reader(handler, Accessor());

        var failure = await Assert.ThrowsAsync<ReportSourceException>(() => reader.ReadAsync(new(
            "reference-customer",
            Security(),
            "report-request-1",
            "report-correlation-1",
            null), CancellationToken.None));

        Assert.Equal("report.source.provenance-boundary-invalid", failure.Error.Code);
    }

    [Fact]
    public async Task TaxSourceWithMismatchedFactProvenanceFailsClosed()
    {
        var handler = new FinancialProfileHandler { MismatchedTaxProvenance = true };
        var failure = await Assert.ThrowsAsync<ReportSourceException>(() => Reader(handler, Accessor())
            .ReadAsync(new("reference-customer", Security(), "tax-request", "tax-correlation", null),
                CancellationToken.None));
        Assert.Equal("report.source.provenance-boundary-invalid", failure.Error.Code);
    }

    [Fact]
    public async Task ReviewedTaxRecordUsesExactHistoricalSourceAndProvenance()
    {
        var handler = new FinancialProfileHandler();
        var snapshot = await Reader(handler, Accessor()).ReadAsync(new(
            "reference-customer", Security(), "tax-record-request", "tax-record-correlation", null),
            CancellationToken.None);

        var item = Assert.Single(snapshot!.Items, value => value.Source.SourceType == "TaxRecord");
        Assert.Equal("tax-record-001", item.Source.SourceId);
        Assert.Equal("ff-tax", item.Source.FinancialFactReferenceId);
        Assert.Equal(1, item.Source.FinancialFactRevision);
        Assert.Equal("provenance-tax", item.Source.FinancialProvenanceReferenceId);
        Assert.Equal("evidence-tax", item.Source.EvidenceReferenceId);
        Assert.Equal("document-version-tax", item.Source.DocumentVersionReferenceId);
        Assert.Contains(handler.Requests,
            request => request.Path == "/local/v1/customers/reference-customer/tax-records");
        Assert.Contains(handler.Requests, request => request.Path == "/contracts/cid-032/v1");
    }

    [Theory]
    [InlineData("customer", "report.source.tax-record-boundary-invalid")]
    [InlineData("source", "report.source.tax-record-boundary-invalid")]
    [InlineData("provenance", "report.source.tax-record-boundary-invalid")]
    [InlineData("evidence", "report.source.tax-record-boundary-invalid")]
    [InlineData("document", "report.source.tax-record-boundary-invalid")]
    [InlineData("fact-type", "report.source.tax-record-boundary-invalid")]
    [InlineData("duplicate", "report.source.tax-record-corpus-incomplete")]
    [InlineData("has-more", "report.source.tax-record-corpus-incomplete")]
    public async Task InvalidOrIncompleteReviewedTaxRecordReportFailsClosed(string mutation, string code)
    {
        var failure = await Assert.ThrowsAsync<ReportSourceException>(() =>
            Reader(new FinancialProfileHandler { TaxRecordMutation = mutation }, Accessor()).ReadAsync(new(
                "reference-customer", Security(), "tax-record-request", "tax-record-correlation", null),
                CancellationToken.None));

        Assert.Equal(code, failure.Error.Code);
    }

    [Fact]
    public async Task CustomerDefinedGoalUsesExactCurrentOwnerRevisionAndOperationReceipt()
    {
        var handler = new FinancialProfileHandler();
        var snapshot = await Reader(handler, Accessor()).ReadAsync(new(
            "reference-customer", Security(), "goal-report-request", "goal-report-correlation", null),
            CancellationToken.None);

        var item = Assert.Single(snapshot!.Items, value => value.Source.SourceType == "FinancialGoal");
        Assert.Equal("financial-goal-001", item.Source.SourceId);
        Assert.Null(item.Source.EvidenceReferenceId);
        Assert.Null(item.Source.FinancialProvenanceReferenceId);
        Assert.Null(item.Source.CalculationLineageReferenceId);
        Assert.Equal(3, item.Source.FinancialGoal!.Revision);
        Assert.Equal(new string('d', 32), item.Source.FinancialGoal.OperationId);
        Assert.Equal(new DateOnly(2027, 12, 31), item.Source.FinancialGoal.TargetDate);
        Assert.Equal("PAUSED", item.Source.FinancialGoal.LifecycleStatus);
        Assert.Equal(new DateOnly(2026, 10, 20), item.Source.FinancialGoal.LifecycleAsOfDate);
        Assert.Equal("HIGH", item.Source.FinancialGoal.PriorityLevel);
        Assert.Equal(new DateOnly(2026, 10, 21), item.Source.FinancialGoal.PriorityAsOfDate);
        Assert.Contains(handler.Requests,
            request => request.Path == "/local/v1/customers/reference-customer/financial-goals");
        Assert.Contains(handler.Requests,
            request => request.Path == "/local/v1/customers/reference-customer/financial-goals/financial-goal-001");
    }

    [Fact]
    public async Task CustomerDefinedGoalRemainsReportableWithoutASeparateSourceFactProfile()
    {
        var snapshot = await Reader(new FinancialProfileHandler { ProfileNotFound = true }, Accessor())
            .ReadAsync(new("reference-customer", Security(), "goal-only-request", "goal-only-correlation", null),
                CancellationToken.None);

        var item = Assert.Single(snapshot!.Items);
        Assert.Equal("FinancialGoal", item.Source.SourceType);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T10:00:00Z", CultureInfo.InvariantCulture),
            snapshot.AsOf);
    }

    [Theory]
    [InlineData("customer", "report.source.financial-goal-boundary-invalid")]
    [InlineData("operation", "report.source.financial-goal-boundary-invalid")]
    [InlineData("lifecycle", "report.source.financial-goal-boundary-invalid")]
    [InlineData("priority", "report.source.financial-goal-boundary-invalid")]
    [InlineData("exact", "report.source.financial-goal-boundary-invalid")]
    [InlineData("duplicate", "report.source.financial-goal-corpus-incomplete")]
    [InlineData("has-more", "report.source.financial-goal-corpus-incomplete")]
    public async Task InvalidOrIncompleteCustomerDefinedGoalReportFailsClosed(string mutation, string code)
    {
        var failure = await Assert.ThrowsAsync<ReportSourceException>(() =>
            Reader(new FinancialProfileHandler { FinancialGoalMutation = mutation }, Accessor()).ReadAsync(new(
                "reference-customer", Security(), "goal-report-request", "goal-report-correlation", null),
                CancellationToken.None));

        Assert.Equal(code, failure.Error.Code);
    }

    [Fact]
    public async Task ExactPhase2ReceiptsAreRefetchedValidatedAndPreservedWithRuleProvenance()
    {
        var handler = new FinancialProfileHandler { IncludeFinancialReview = true };
        var selection = new FinancialReviewReceiptSelection(
            "allocation-review", "cashflow-rate-review", "emi-share-review", "coverage-review");

        var snapshot = await Reader(handler, Accessor()).ReadAsync(new(
            "reference-customer", Security(), "review-request", "review-correlation", null, selection),
            CancellationToken.None);

        Assert.NotNull(snapshot);
        var calculations = snapshot.Items.Where(item => item.Source.SourceType == "FinancialCalculation").ToArray();
        Assert.Equal(4, calculations.Select(item => item.Source.RuleId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ReviewReceiptIds.Order(),
            calculations.Select(item => item.Source.SourceId).Distinct(StringComparer.Ordinal).Order());
        Assert.All(calculations, item =>
        {
            Assert.Equal("Financial Rules Service", item.Source.AuthoritativeOwner);
            Assert.Equal(item.Source.SourceId, item.Source.CalculationLineageReferenceId);
            Assert.Equal(64, item.Source.DefinitionHash?.Length);
            Assert.NotEmpty(item.Source.SourceCalculationIds!);
        });
        Assert.Equal(4, handler.RuleRequests.Count);
        Assert.All(handler.RuleRequests, request =>
        {
            Assert.Equal("T001", request.Tenant);
            Assert.Equal("customer-session", request.Session);
        });
    }

    private static ProductFinancialProfileReportSourceReader Reader(
        HttpMessageHandler handler,
        IHttpContextAccessor accessor)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Monergy:ExecutionZone"] = "LOCAL",
                ["Monergy:ProductReporting:Enabled"] = "true",
                ["Monergy:ProductReporting:FinancialProfileUrl"] = "http://127.0.0.1:5113/",
                ["Monergy:ProductReporting:FinancialRulesUrl"] = "http://127.0.0.1:5114/",
            }).Build();
        return new(new TestHttpClientFactory(handler), accessor, configuration);
    }

    private static HttpContextAccessor Accessor()
    {
        var http = new DefaultHttpContext();
        http.Features.Set(new TenantBoundaryProof(
            "T001",
            "A100",
            "customer-session",
            "reference-customer",
            ReportingContractNames.GenerateReport,
            "reporting.report.generate",
            "decision-reporting"));
        return new HttpContextAccessor { HttpContext = http };
    }

    private static TrustedSecurityContext Security() => new(
        new ActorContext("A100", "Customer", DateTimeOffset.UnixEpoch, "customer-session"),
        new WorkloadContext("customer-web", "local-uat-workspace:T001"),
        new AccessContext("customer-advice", null, "decision-reporting", "reference-customer"));

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class FinancialProfileHandler : HttpMessageHandler
    {
        public string AssetType { get; set; } = "ASSET";
        public bool CanonicalCategories { get; set; }
        public bool CrossCustomerProvenance { get; set; }
        public bool MismatchedTaxProvenance { get; set; }
        public bool IncludeFinancialReview { get; set; }
        public bool ProfileNotFound { get; set; }
        public string? TaxRecordMutation { get; set; }
        public string? FinancialGoalMutation { get; set; }
        public List<(string Path, string Tenant, string Session, string Workload)> Requests { get; } = [];
        public List<(string Path, string Tenant, string Session)> RuleRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/tax-records", StringComparison.Ordinal))
                {
                    Requests.Add((request.RequestUri.AbsolutePath,
                        request.Headers.GetValues("X-Monergy-Tenant").Single(),
                        request.Headers.GetValues("X-Monergy-Session").Single(), "reporting"));
                    return TaxRecords();
                }
                if (request.RequestUri.AbsolutePath.EndsWith("/financial-goals", StringComparison.Ordinal))
                {
                    Requests.Add((request.RequestUri.AbsolutePath,
                        request.Headers.GetValues("X-Monergy-Tenant").Single(),
                        request.Headers.GetValues("X-Monergy-Session").Single(), "reporting"));
                    return FinancialGoals();
                }
                if (request.RequestUri.AbsolutePath.Contains("/financial-goals/", StringComparison.Ordinal))
                {
                    Requests.Add((request.RequestUri.AbsolutePath,
                        request.Headers.GetValues("X-Monergy-Tenant").Single(),
                        request.Headers.GetValues("X-Monergy-Session").Single(), "reporting"));
                    return FinancialGoal(exact: true);
                }
                RuleRequests.Add((request.RequestUri!.AbsolutePath,
                    request.Headers.GetValues("X-Monergy-Tenant").Single(),
                    request.Headers.GetValues("X-Monergy-Session").Single()));
                return IncludeFinancialReview ? Review(request.RequestUri.AbsolutePath) :
                    new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = System.Text.Json.JsonDocument.Parse(body);
            Requests.Add((
                request.RequestUri!.AbsolutePath,
                request.Headers.GetValues("X-Monergy-Tenant").Single(),
                request.Headers.GetValues("X-Monergy-Session").Single(),
                document.RootElement.GetProperty("security").GetProperty("workload").GetProperty("workloadId").GetString()!));

            return request.RequestUri.AbsolutePath switch
            {
                "/contracts/cid-030/v1" => Profile(body),
                "/contracts/cid-032/v1" => TaxFact(body),
                "/contracts/cid-033/v1" => Provenance(body),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private HttpResponseMessage TaxRecords()
        {
            var record = new ReviewedTaxRecord(
                "tax-record-001", new string('a', 32),
                TaxRecordMutation == "customer" ? "other-customer" : "reference-customer",
                new TaxRecordDraft("Income tax return", "India", "Income tax", 14000m, "PAID", "2025-26",
                    new DateOnly(2025, 4, 1), new DateOnly(2026, 3, 31),
                    TaxRecordMutation == "source" ? "ff-other" : "ff-tax", 1),
                "USD", TaxRecordMutation == "provenance" ? "provenance-other" : "provenance-tax",
                TaxRecordMutation == "document" ? "document-version-other" : "document-version-tax",
                TaxRecordMutation == "evidence" ? "evidence-other" : "evidence-tax", "A100",
                DateTimeOffset.Parse("2026-10-16T10:00:00Z", CultureInfo.InvariantCulture),
                "CUSTOMER_REVIEWED");
            IReadOnlyList<ReviewedTaxRecord> items = TaxRecordMutation == "duplicate" ? [record, record] : [record];
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new TaxRecordList(items, TaxRecordMutation == "has-more"),
                    options: ContractJson.Options),
            };
        }

        private HttpResponseMessage TaxFact(string body)
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<ContractRequest<GetFinancialFact>>(
                body, ContractJson.Options)!;
            return Success(request, new AuthoritativeFinancialFact(
                "ff-tax", "financial-profile-reference-customer", "reference-customer",
                TaxRecordMutation == "fact-type" ? "EXPENSE" : "TAX_RECORD", "Tax source figure", 14000m,
                "USD", new DateOnly(2026, 10, 1), 1, "provenance-tax"));
        }

        private HttpResponseMessage FinancialGoals()
        {
            var content = FinancialGoalRecord(exact: false);
            IReadOnlyList<CustomerFinancialGoal> items = FinancialGoalMutation == "duplicate"
                ? [content, content]
                : [content];
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new FinancialGoalList(items,
                    FinancialGoalMutation == "has-more"), options: ContractJson.Options),
            };
        }

        private HttpResponseMessage FinancialGoal(bool exact)
        {
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(FinancialGoalRecord(exact), options: ContractJson.Options),
            };
        }

        private CustomerFinancialGoal FinancialGoalRecord(bool exact) =>
            new(
                "financial-goal-001",
                FinancialGoalMutation == "operation" ? new string('D', 32) : new string('d', 32),
                FinancialGoalMutation == "customer" ? "other-customer" : "reference-customer",
                new FinancialGoalDraft("Emergency fund",
                    exact && FinancialGoalMutation == "exact" ? 26000m : 25000m,
                    "USD", new DateOnly(2027, 12, 31)),
                "A100", DateTimeOffset.Parse("2026-10-29T10:00:00Z", CultureInfo.InvariantCulture),
                "CUSTOMER_DEFINED", 3,
                FinancialGoalMutation == "lifecycle" ? "VERIFIED" : "PAUSED",
                new DateOnly(2026, 10, 20), "CUSTOMER_REPORTED",
                FinancialGoalMutation == "priority" ? "URGENT" : "HIGH",
                new DateOnly(2026, 10, 21), "CUSTOMER_REPORTED");

        private static HttpResponseMessage Review(string path)
        {
            var receipts = ReviewReceipts();
            object value = path switch
            {
                "/local/v1/customers/reference-customer/net-worth/asset-allocations/allocation-review" => receipts.Allocation,
                "/local/v1/customers/reference-customer/monthly-cashflow/net-cashflow-rates/cashflow-rate-review" => receipts.Rate,
                "/local/v1/customers/reference-customer/monthly-cashflow/emi-shares/emi-share-review" => receipts.Share,
                "/local/v1/customers/reference-customer/monthly-cashflow/debt-service-coverages/coverage-review" => receipts.Coverage,
                _ => throw new InvalidOperationException("Unexpected Financial Rules path: " + path),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(value, options: ContractJson.Options),
            };
        }

        private static (AssetAllocationCalculation Allocation, MonthlyNetCashflowRateCalculation Rate,
            MonthlyEmiShareCalculation Share, MonthlyDebtServiceCoverageCalculation Coverage) ReviewReceipts()
        {
            const string customer = "reference-customer";
            const string actor = "A100";
            var day = new DateOnly(2026, 9, 30);
            var at = DateTimeOffset.Parse("2026-10-02T09:00:00Z", CultureInfo.InvariantCulture);
            var asset = new NetWorthLineDraft(new string('a', 32), "ASSET", "CASH", "Reviewed cash",
                "1000.00", "USD", day, "Customer reviewed");
            var netPosition = new ReviewedNetWorthPosition("net-position", new string('b', 32), customer,
                new(day, "USD", [asset], true, true), [new(asset, null)], actor, at);
            var netWorth = new NetWorthCalculation("net-worth-review", new string('c', 32), customer,
                netPosition, NetWorthDefinition.Id, NetWorthDefinition.Version, NetWorthDefinition.DefinitionHash,
                NetWorthDefinition.Semantics, "CALCULATED", "1000.00", "0.00", "1000.00", [], actor, at);
            var allocation = new AssetAllocationCalculation("allocation-review", new string('d', 32), customer,
                netWorth, [new("CASH", "1000.00", "100.00")], AssetAllocationDefinition.Id,
                AssetAllocationDefinition.Version, AssetAllocationDefinition.DefinitionHash,
                AssetAllocationDefinition.Semantics, "CALCULATED", "1000.00", [], actor, at);

            var income = new MonthlyCashflowLineDraft(new string('e', 32), "INCOME", "SALARY", "Salary",
                "10000.00", "USD", day, "Customer reviewed");
            var expense = new MonthlyCashflowLineDraft(new string('f', 32), "EXPENSE", "HOUSEHOLD", "Household",
                "5000.00", "USD", day, "Customer reviewed");
            var cashPosition = new ReviewedMonthlyCashflowPosition("cash-position", new string('1', 32), customer,
                new(day, "USD", [income, expense], true, true, Household: "Self"),
                [new(income, null), new(expense, null)], actor, at);
            var cashflow = new MonthlyCashflowCalculation("cashflow-review", new string('2', 32), customer,
                cashPosition, MonthlyCashflowDefinition.Id, MonthlyCashflowDefinition.Version,
                MonthlyCashflowDefinition.DefinitionHash, MonthlyCashflowDefinition.Semantics, "CALCULATED",
                "10000.00", "5000.00", "5000.00", [], actor, at);
            var rate = new MonthlyNetCashflowRateCalculation("cashflow-rate-review", new string('3', 32), customer,
                cashflow, MonthlyNetCashflowRateDefinition.Id, MonthlyNetCashflowRateDefinition.Version,
                MonthlyNetCashflowRateDefinition.DefinitionHash, MonthlyNetCashflowRateDefinition.Semantics,
                "CALCULATED", "10000.00", "5000.00", "5000.00", "50.00", [], actor, at);
            var loan = new ReviewedLoanRecord("loan-review", new string('4', 32), customer,
                new("Home loan", "Bank", "HOME", 100000m, 80000m, 8m, 2500m, 60,
                    new(2026, 1, 1), new(2030, 12, 31), day, "fact-loan", 1), "USD",
                "provenance-loan", "document-version-loan", "evidence-loan", actor, at, 2, "ACTIVE", day);
            var share = new MonthlyEmiShareCalculation("emi-share-review", new string('5', 32), customer,
                cashflow, [loan], true, MonthlyEmiShareDefinition.Id, MonthlyEmiShareDefinition.Version,
                MonthlyEmiShareDefinition.DefinitionHash, MonthlyEmiShareDefinition.Semantics, "CALCULATED",
                "10000.00", "2500.00", "25.00", [], actor, at);
            var coverage = new MonthlyDebtServiceCoverageCalculation("coverage-review", new string('6', 32), customer,
                cashflow, share, true, MonthlyDebtServiceCoverageDefinition.Id,
                MonthlyDebtServiceCoverageDefinition.Version, MonthlyDebtServiceCoverageDefinition.DefinitionHash,
                MonthlyDebtServiceCoverageDefinition.Semantics, "CALCULATED", "10000.00", "5000.00",
                "2500.00", "5000.00", "7500.00", "3.00", [], actor, at);
            return (allocation, rate, share, coverage);
        }

        private HttpResponseMessage Profile(string body)
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<ContractRequest<GetFinancialProfile>>(
                body, ContractJson.Options)!;
            if (ProfileNotFound)
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        ContractResult<FinancialProfileDetails>.Rejected(request, "financial-profile.not-found",
                            ContractErrorCategory.NotFound, "No financial profile is available."),
                        options: ContractJson.Options),
                };
            var at = DateTimeOffset.Parse("2026-10-16T09:00:00Z", CultureInfo.InvariantCulture);
            var facts = new[]
            {
                new AuthoritativeFinancialFact(
                    "ff-income", "financial-profile-reference-customer", "reference-customer",
                    "INCOME", "Employment income", 125000m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-income"),
                new AuthoritativeFinancialFact(
                    "ff-expense", "financial-profile-reference-customer", "reference-customer",
                    "EXPENSE", "Rent", 2500m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-expense"),
                new AuthoritativeFinancialFact(
                    "ff-loan", "financial-profile-reference-customer", "reference-customer",
                    "LOAN", "Loan source amount", 85000m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-loan"),
                new AuthoritativeFinancialFact(
                    "ff-asset", "financial-profile-reference-customer", "reference-customer",
                    AssetType, "Asset source figure", 900000m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-asset"),
                new AuthoritativeFinancialFact(
                    "ff-investment", "financial-profile-reference-customer", "reference-customer",
                    "INVESTMENT", "Investment source figure", 500m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-investment"),
                new AuthoritativeFinancialFact(
                    "ff-insurance", "financial-profile-reference-customer", "reference-customer",
                    CanonicalCategories ? FinancialObjectTypes.InsurancePolicy : "INSURANCE", "Insurance source figure", 100m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-insurance"),
                new AuthoritativeFinancialFact(
                    "ff-tax", "financial-profile-reference-customer", "reference-customer",
                    CanonicalCategories ? FinancialObjectTypes.TaxRecord : "TAX", "Tax source figure", 14000m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-tax"),
                new AuthoritativeFinancialFact(
                    "ff-unsupported", "financial-profile-reference-customer", "reference-customer",
                    "UNSUPPORTED", "Unsupported source", 10m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-unsupported"),
            };
            return Success(request, new FinancialProfileDetails(
                "financial-profile-reference-customer",
                "reference-customer",
                3,
                at,
                facts));
        }

        private HttpResponseMessage Provenance(string body)
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<ContractRequest<GetFinancialProvenance>>(
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
                income ? "ff-income" : loan ? "ff-loan" : asset ? "ff-asset" : investment ? "ff-investment" : insurance ? "ff-insurance" : tax ? (MismatchedTaxProvenance ? "ff-wrong-tax" : "ff-tax") : "ff-expense",
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
