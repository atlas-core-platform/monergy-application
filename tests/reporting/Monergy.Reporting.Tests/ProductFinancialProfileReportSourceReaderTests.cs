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
    [Fact]
    public async Task ReadsOnlyCurrentSupportedFinancialFactsWithExactProvenance()
    {
        var handler = new FinancialProfileHandler();
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
        Assert.Equal(7, snapshot.Items.Length);
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
        Assert.Contains(snapshot.Items, item => item.Label == "Tax source figure" && item.Source.SourceId == "ff-tax" && item.Source.FinancialProvenanceReferenceId == "provenance-tax" && item.Source.CalculationLineageReferenceId is null);
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
            }).Build();
        return new(new TestHttpClientFactory(handler), accessor, configuration);
    }

    private static IHttpContextAccessor Accessor()
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
        public bool CrossCustomerProvenance { get; set; }
        public List<(string Path, string Tenant, string Session, string Workload)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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
                "/contracts/cid-033/v1" => Provenance(body),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Profile(string body)
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<ContractRequest<GetFinancialProfile>>(
                body, ContractJson.Options)!;
            var at = DateTimeOffset.Parse("2026-10-16T09:00:00Z");
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
                    "ASSET", "Asset source figure", 900000m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-asset"),
                new AuthoritativeFinancialFact(
                    "ff-investment", "financial-profile-reference-customer", "reference-customer",
                    "INVESTMENT", "Investment source figure", 500m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-investment"),
                new AuthoritativeFinancialFact(
                    "ff-insurance", "financial-profile-reference-customer", "reference-customer",
                    "INSURANCE", "Insurance source figure", 100m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-insurance"),
                new AuthoritativeFinancialFact(
                    "ff-tax", "financial-profile-reference-customer", "reference-customer",
                    "TAX", "Tax source figure", 14000m, "USD",
                    new DateOnly(2026, 10, 1), 1, "provenance-tax"),
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
