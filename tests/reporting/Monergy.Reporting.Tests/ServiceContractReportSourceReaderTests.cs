using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.Reporting.Application;
using Monergy.Services.Reporting.Infrastructure;
using Xunit;

namespace Monergy.Reporting.Tests;

public sealed class ServiceContractReportSourceReaderTests : IDisposable
{
    private static readonly string[] FinancialFactIds = ["fact-income", "fact-expense"];
    private readonly string statePath = Path.Combine(Path.GetTempPath(), $"d11-scenario-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task ReadsPersistedOwnerContractsWithReportingWorkloadAndExactLineage()
    {
        WriteState();
        var handler = new OwnerContractHandler();
        var reader = Reader(handler);
        var security = Security("customer-a");
        var result = await reader.ReadAsync(new("customer-a", security, "request-1", "correlation-1", null),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result.Items.Length);
        Assert.Contains(result.Items, item => item.Source.SourceId == "fact-income" &&
            item.Source.FinancialProvenanceReferenceId == "provenance-income" &&
            item.Source.EvidenceReferenceId == "evidence-a");
        Assert.Contains(result.Items, item => item.Source.SourceId == "calculation-a" &&
            item.Source.CalculationLineageReferenceId == "lineage-a");
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("correlation-1", request.GetProperty("correlationId").GetString());
            Assert.Equal("reporting", request.GetProperty("security").GetProperty("workload")
                .GetProperty("workloadId").GetString());
            Assert.Equal("customer-a", request.GetProperty("security").GetProperty("access")
                .GetProperty("customerId").GetString());
        });
    }

    [Fact]
    public async Task MissingScenarioReturnsNoSourceAndNeverUsesReferenceSnapshots()
    {
        var handler = new OwnerContractHandler();
        var reader = Reader(handler);
        var result = await reader.ReadAsync(new("customer-a", Security("customer-a"), "request-1",
            "correlation-1", null), CancellationToken.None);
        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MixedCustomerOwnerResponseFailsClosed()
    {
        WriteState();
        var handler = new OwnerContractHandler { ReturnMixedCustomerProfile = true };
        var exception = await Assert.ThrowsAsync<ReportSourceException>(() => Reader(handler).ReadAsync(
            new("customer-a", Security("customer-a"), "request-1", "correlation-1", null),
            CancellationToken.None));
        Assert.Equal("report.source.profile-boundary-invalid", exception.Error.Code);
    }

    [Fact]
    public async Task DeniedOwnerReadPreservesClassifiedFailureWithoutFallback()
    {
        WriteState();
        var handler = new OwnerContractHandler { DenyFinancialProfile = true };
        var exception = await Assert.ThrowsAsync<ReportSourceException>(() => Reader(handler).ReadAsync(
            new("customer-a", Security("customer-a"), "request-1", "correlation-1", null),
            CancellationToken.None));
        Assert.Equal("financial.profile.denied", exception.Error.Code);
        Assert.Equal(ContractErrorCategory.AccessDenied, exception.Error.Category);
    }

    [Theory]
    [InlineData("wrong-profile", "report.source.profile-identity-invalid")]
    [InlineData("wrong-fact", "report.source.fact-identity-invalid")]
    [InlineData("wrong-provenance", "report.source.provenance-identity-invalid")]
    [InlineData("wrong-calculation-consistent", "report.source.calculation-identity-invalid")]
    [InlineData("cross-customer-explanation", "report.source.explanation-boundary-invalid")]
    [InlineData("changed-input-value", "report.source.lineage-inconsistent")]
    [InlineData("changed-input-unit", "report.source.lineage-inconsistent")]
    [InlineData("explanation-changed-input-value", "report.source.calculation-inconsistent")]
    [InlineData("explanation-changed-input-unit", "report.source.calculation-inconsistent")]
    [InlineData("explanation-changed-input-provenance", "report.source.calculation-inconsistent")]
    [InlineData("malformed-explanation-inputs", "report.source.response-malformed")]
    [InlineData("null-explanation-result", "report.source.response-malformed")]
    [InlineData("missing-explanation-result", "report.source.response-malformed")]
    [InlineData("missing-input", "report.source.calculation-lineage-invalid")]
    [InlineData("duplicate-input", "report.source.calculation-lineage-invalid")]
    [InlineData("mixed-evidence-version", "report.source.evidence-version-inconsistent")]
    [InlineData("duplicate-profile-fact", "report.source.profile-inconsistent")]
    [InlineData("null-evidence-version", "report.source.evidence-version-inconsistent")]
    [InlineData("duplicate-evidence-version", "report.source.evidence-version-inconsistent")]
    public async Task ExactRequestedSourceAndLineageMismatchFailsClosed(string mutation, string expectedCode)
    {
        WriteState();
        var handler = new OwnerContractHandler { Mutation = mutation };

        var exception = await Assert.ThrowsAsync<ReportSourceException>(() => Reader(handler).ReadAsync(
            new("customer-a", Security("customer-a"), "request-1", "correlation-1", null),
            CancellationToken.None));

        Assert.Equal(expectedCode, exception.Error.Code);
    }

    [Fact]
    public async Task MalformedScenarioIsClassifiedAndDoesNotCallOwners()
    {
        File.WriteAllText(statePath, "{ malformed");
        var handler = new OwnerContractHandler();

        var exception = await Assert.ThrowsAsync<ReportSourceException>(() => Reader(handler).ReadAsync(
            new("customer-a", Security("customer-a"), "request-1", "correlation-1", null),
            CancellationToken.None));

        Assert.Equal("report.source.scenario-invalid", exception.Error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DuplicateScenarioFactSelectionIsRejectedBeforeOwnerRead()
    {
        WriteState(["fact-income", "fact-income"]);
        var handler = new OwnerContractHandler();

        var exception = await Assert.ThrowsAsync<ReportSourceException>(() => Reader(handler).ReadAsync(
            new("customer-a", Security("customer-a"), "request-1", "correlation-1", null),
            CancellationToken.None));

        Assert.Equal("report.source.scenario-invalid", exception.Error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NullScenarioCustomerIsClassifiedBeforeOwnerRead()
    {
        File.WriteAllText(statePath, """
            {"profile":"persisted-reporting","customers":[null]}
            """);
        var handler = new OwnerContractHandler();

        var exception = await Assert.ThrowsAsync<ReportSourceException>(() => Reader(handler).ReadAsync(
            new("customer-a", Security("customer-a"), "request-1", "correlation-1", null),
            CancellationToken.None));

        Assert.Equal("report.source.scenario-invalid", exception.Error.Code);
        Assert.Empty(handler.Requests);
    }

    public void Dispose()
    {
        if (File.Exists(statePath)) File.Delete(statePath);
        GC.SuppressFinalize(this);
    }

    private ServiceContractReportSourceReader Reader(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monergy:ExecutionZone"] = "LOCAL",
            ["Monergy:D11:Profile"] = "persisted-reporting",
            ["Monergy:D11:ScenarioStatePath"] = statePath,
            ["Monergy:D11:ReportingWorkloadIdentityId"] = "d11-reporting-workload",
        }).Build();
        return new(new TestHttpClientFactory(handler), configuration);
    }

    private void WriteState(IReadOnlyList<string>? financialFactIds = null) => File.WriteAllText(statePath, JsonSerializer.Serialize(new
    {
        profile = "persisted-reporting",
        customers = new[]
        {
            new
            {
                customerId = "customer-a",
                financialProfileId = "profile-a",
                financialFactIds = financialFactIds ?? FinancialFactIds,
                calculationResultId = "calculation-a",
                documentId = "document-a",
                documentVersionId = "version-a",
                evidenceId = "evidence-a",
            },
        },
    }, ContractJson.Options));

    private static TrustedSecurityContext Security(string customer) => new(
        new("d11-synthetic-actor-customer-a", "SYNTHETIC_HUMAN", DateTimeOffset.UnixEpoch,
            "d11-authentication-customer-a"),
        new("customer-web", "d11-customer-web"),
        new("D11_PERSISTED_REPORTING", "d11-consent-customer-a",
            "d11-customer-web-customer-a-authorization", customer));

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://owner/") };
    }

    private sealed class OwnerContractHandler : HttpMessageHandler
    {
        private readonly DateTimeOffset occurredAt = new(2026, 10, 1, 1, 0, 0, TimeSpan.Zero);
        public List<JsonElement> Requests { get; } = [];
        public bool ReturnMixedCustomerProfile { get; set; }
        public bool DenyFinancialProfile { get; set; }
        public string? Mutation { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            Requests.Add(document.RootElement.Clone());
            return request.RequestUri!.AbsolutePath switch
            {
                "/contracts/cid-030/v1" => Profile(body),
                "/contracts/cid-032/v1" => Fact(body),
                "/contracts/cid-033/v1" => Provenance(body),
                "/contracts/cid-021/v1" => Metadata(body),
                "/contracts/cid-022/v1" => Evidence(body),
                "/contracts/cid-038/v1" => Calculation(body),
                "/contracts/cid-039/v1" => Explanation(body),
                _ => new(HttpStatusCode.NotFound),
            };
        }

        private HttpResponseMessage Profile(string body)
        {
            var request = Read<GetFinancialProfile>(body);
            if (DenyFinancialProfile) return Rejected<GetFinancialProfile, FinancialProfileDetails>(request,
                "financial.profile.denied", ContractErrorCategory.AccessDenied);
            var customer = ReturnMixedCustomerProfile ? "customer-b" : "customer-a";
            var profileId = Mutation == "wrong-profile" ? "profile-other" : "profile-a";
            IReadOnlyList<AuthoritativeFinancialFact> facts = Mutation == "duplicate-profile-fact"
                ? [Income(), Income(), Expense()]
                : [Income(), Expense()];
            return Success(request, new FinancialProfileDetails(profileId, customer, 1, occurredAt, facts));
        }

        private HttpResponseMessage Fact(string body)
        {
            var request = Read<GetFinancialFact>(body);
            var fact = request.Payload.FinancialFactId == "fact-income" ? Income() : Expense();
            if (Mutation == "wrong-fact") fact = fact with { FinancialFactId = "fact-other" };
            return Success(request, fact);
        }

        private HttpResponseMessage Provenance(string body)
        {
            var request = Read<GetFinancialProvenance>(body);
            var income = request.Payload.FinancialProvenanceId == "provenance-income";
            var provenanceId = income ? "provenance-income" : "provenance-expense";
            if (Mutation == "wrong-provenance") provenanceId = "provenance-other";
            return Success(request, ProvenanceValue(income ? "fact-income" : "fact-expense",
                provenanceId, income ? "source-income" : "source-expense"));
        }

        private HttpResponseMessage Metadata(string body)
        {
            var request = Read<GetEvidenceMetadata>(body);
            var version = Mutation == "mixed-evidence-version" ? "version-other" : "version-a";
            var selected = new EvidenceVersionMetadata(version, "evidence-a", 1, "synthetic", "statement.txt",
                "text/plain", "ABC", occurredAt);
            IReadOnlyList<EvidenceVersionMetadata> versions = Mutation switch
            {
                "null-evidence-version" => [null!],
                "duplicate-evidence-version" => [selected, selected],
                _ => [selected],
            };
            return Success(request, new EvidenceMetadata("document-a", "customer-a", versions));
        }

        private static HttpResponseMessage Evidence(string body)
        {
            var request = Read<GetEvidenceReference>(body);
            return Success(request, new EvidenceReference("evidence-a", "document-a", "version-a", "customer-a",
                "content-a", "ABC", "text/plain"));
        }

        private HttpResponseMessage Calculation(string body)
        {
            var request = Read<GetCalculationResult>(body);
            return Success(request, CalculationValue());
        }

        private HttpResponseMessage Explanation(string body)
        {
            var request = Read<ExplainCalculation>(body);
            var result = CalculationValue(explanation: true);
            if (Mutation == "cross-customer-explanation") result = result with { CustomerId = "customer-b" };
            var response = Success(request, new CalculationExplanation(result, "Engineering fixture only."));
            if (Mutation is not ("malformed-explanation-inputs" or "null-explanation-result" or
                "missing-explanation-result")) return response;
            var json = response.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var root = JsonNode.Parse(json)!;
            if (Mutation == "malformed-explanation-inputs") root["data"]!["result"]!["inputs"] = null;
            else if (Mutation == "null-explanation-result") root["data"]!["result"] = null;
            else root["data"]!.AsObject().Remove("result");
            response.Content = new StringContent(root.ToJsonString(ContractJson.Options),
                System.Text.Encoding.UTF8, "application/json");
            return response;
        }

        private CalculationResult CalculationValue(bool explanation = false)
        {
            var income = new CalculationInputLineage("left", "fact-income", 1,
                Mutation == "changed-input-value" || (explanation && Mutation == "explanation-changed-input-value")
                    ? 999m : 125000m,
                Mutation == "changed-input-unit" || (explanation && Mutation == "explanation-changed-input-unit")
                    ? "USD" : "INR",
                ProvenanceValue("fact-income", "provenance-income",
                    explanation && Mutation == "explanation-changed-input-provenance" ? "source-other" : "source-income"));
            var expense = new CalculationInputLineage("right", "fact-expense", 1, 80000m, "INR",
                ProvenanceValue("fact-expense", "provenance-expense", "source-expense"));
            var inputs = Mutation switch
            {
                "missing-input" => ImmutableArray.Create(income),
                "duplicate-input" => ImmutableArray.Create(income, income),
                _ => ImmutableArray.Create(income, expense),
            };
            return new(Mutation == "wrong-calculation-consistent" ? "calculation-other" : "calculation-a",
                1, "lineage-a", "customer-a", "engineering.sum", "1.0.0", "implementation", "definition",
                "decimal", inputs, 205000m, "INR", "left + right", occurredAt, "actor",
                "d11-reporting-workload", "D11_PERSISTED_REPORTING", "correlation-1", "request-1", "event-a");
        }

        private FinancialProvenance ProvenanceValue(string fact, string provenance, string source) =>
            new(provenance, fact, "customer-a", "evidence-a", "version-a", source, "extract/1",
                "validate/1", "normalize/1", "actor", "workload", occurredAt, "correlation-1", "request-1");

        private static AuthoritativeFinancialFact Income() => new("fact-income", "profile-a", "customer-a",
            "INCOME", "Synthetic income", 125000m, "INR", new DateOnly(2026, 9, 30), 1, "provenance-income");

        private static AuthoritativeFinancialFact Expense() => new("fact-expense", "profile-a", "customer-a",
            "EXPENSE", "Synthetic expense", 80000m, "INR", new DateOnly(2026, 9, 30), 1, "provenance-expense");

        private static ContractRequest<T> Read<T>(string body) =>
            JsonSerializer.Deserialize<ContractRequest<T>>(body, ContractJson.Options)!;

        private static HttpResponseMessage Success<TPayload, TResult>(ContractRequest<TPayload> request, TResult data) =>
            Response(HttpStatusCode.OK, ContractResult<TResult>.Succeeded(request, data));

        private static HttpResponseMessage Rejected<TPayload, TResult>(ContractRequest<TPayload> request,
            string code, ContractErrorCategory category) => Response(HttpStatusCode.Forbidden,
                ContractResult<TResult>.Rejected(request, code, category, "Denied."));

        private static HttpResponseMessage Response<T>(HttpStatusCode status, T value) => new(status)
        {
            Content = JsonContent.Create(value, options: ContractJson.Options),
        };
    }
}
