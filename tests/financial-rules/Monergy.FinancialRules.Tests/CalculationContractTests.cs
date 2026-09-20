using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Domain;
using Monergy.Services.FinancialRules.Infrastructure;
using Xunit;

namespace Monergy.FinancialRules.Tests;

public sealed class CalculationContractTests
{
    private static readonly string[] ExpectedLifecycleFields =
        ["Service", "Operation", "Outcome", "RequestId", "CorrelationId", "CausationId", "SubjectType", "SubjectId", "AdapterKind"];
    private static readonly (string Service, string Operation, string Outcome, string SubjectType)[] ExpectedCalculationCategories =
    [
        ("Financial Profile Service", Vs02ContractNames.NormalizeSourceFacts, "PROMOTED", "financial-profile"),
        ("Financial Profile Service", Vs02ContractNames.GetFinancialFact, "READ", "financial-fact"),
        ("Financial Profile Service", Vs02ContractNames.GetFinancialProvenance, "READ", "financial-provenance"),
        ("Financial Rules Service", FinancialRulesContractNames.ExecuteCalculation, "RECORDED", "financial-calculation"),
        ("Financial Rules Service", FinancialRulesContractNames.ExecuteCalculation, "REPLAYED", "financial-calculation"),
        ("Audit Service", "ConsumeGovernedEvent", "APPENDED", "financial-calculation"),
        ("Audit Service", "ConsumeGovernedEvent", "REPLAYED", "financial-calculation"),
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedAndFailedEventsAreAtomicReplaySafeAndIndependentlyAuditable(bool failure)
    {
        var h = new Harness();
        await h.SeedAsync(right: failure ? 0 : 25);
        var request = h.Execute(rule: failure ? "engineering.ratio" : "engineering.sum");
        var result = await h.App.ExecuteAsync(request);
        Assert.Equal(failure ? ContractOutcome.Failed : ContractOutcome.Success, result.Outcome);
        var outcome = Assert.Single(h.Repository.PendingEvents());
        Assert.Equal(failure ? "CID-041" : "CID-040", outcome.ContractId);
        Assert.Equal(request.RequestId, outcome.CausationId);
        Assert.Equal("correlation", outcome.CorrelationId);
        Assert.Equal(outcome.SubjectId, outcome.Payload.CalculationResultId);
        var sink = new AuditSink(h.Audit) { FailBeforeConsume = true };
        var dispatcher = new CalculationOutboxDispatcher(h.Repository, sink);
        await Assert.ThrowsAsync<IOException>(() => dispatcher.DispatchAsync());
        Assert.Single(h.Repository.PendingEvents());
        Assert.Equal(result.Data, (await h.App.ExecuteAsync(request)).Data);
        Assert.Equal(result.Error, (await h.App.ExecuteAsync(request)).Error);
        sink.FailBeforeConsume = false;
        sink.LoseAcknowledgement = true;
        await Assert.ThrowsAsync<IOException>(() => dispatcher.DispatchAsync());
        Assert.Single(await h.Audit.ReadAllAsync());
        Assert.Single(h.Repository.PendingEvents());
        sink.LoseAcknowledgement = false;
        Assert.Equal(1, await dispatcher.DispatchAsync());
        Assert.Empty(h.Repository.PendingEvents());
        var audit = Assert.Single(await h.Audit.ReadAllAsync());
        Assert.Equal(outcome.EventId, audit.SourceEventId);
        Assert.Equal(outcome.SubjectId, audit.SubjectId);
        Assert.Equal(outcome.ContractId, audit.SourceContractId);
        Assert.All(h.Telemetry.Signals, AssertCalculationLifecycleMetadataOnly);
        // Require the governed activities, not an incidental total record count.
        var categories = h.Telemetry.Signals.Select(signal =>
            (signal.Service, signal.Operation, signal.Outcome, signal.SubjectType)).ToHashSet();
        Assert.All(ExpectedCalculationCategories, category => Assert.Contains(category, categories));
        Assert.All(h.Telemetry.Signals.Where(signal => signal.Service == "Financial Rules Service"), signal =>
        {
            Assert.Equal(request.RequestId, signal.RequestId);
            Assert.Equal(request.CorrelationId, signal.CorrelationId);
            Assert.Equal(request.CausationId, signal.CausationId);
            Assert.Equal(outcome.SubjectId, signal.SubjectId);
        });
        Assert.All(h.Telemetry.Signals.Where(signal => signal.Service == "Audit Service"), signal =>
        {
            Assert.Equal(outcome.EventId, signal.RequestId);
            Assert.Equal(outcome.CorrelationId, signal.CorrelationId);
            Assert.Equal(outcome.CausationId, signal.CausationId);
            Assert.Equal(outcome.SubjectId, signal.SubjectId);
        });
    }

    [Fact]
    public void CalculationTelemetryPrivacyAllowsOpaqueIdentifiersContainingFinancialDigits()
    {
        AssertCalculationLifecycleMetadataOnly(PrivacyRegressionSignal());
    }

    [Theory]
    [InlineData("Service")]
    [InlineData("Operation")]
    [InlineData("Outcome")]
    [InlineData("SubjectType")]
    [InlineData("AdapterKind")]
    public void CalculationTelemetryPrivacyRejectsFinancialPayloadInSemanticMetadata(string field)
    {
        var signal = PrivacyRegressionSignal();
        const string payload = "{\"inputs\":[100,25],\"result\":{\"value\":125,\"unit\":\"INR\"}}";
        var leaking = field switch
        {
            "Service" => signal with { Service = payload },
            "Operation" => signal with { Operation = payload },
            "Outcome" => signal with { Outcome = payload },
            "SubjectType" => signal with { SubjectType = payload },
            "AdapterKind" => signal with { AdapterKind = payload },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertCalculationLifecycleMetadataOnly(leaking));
    }

    private static LifecycleSignal PrivacyRegressionSignal() => new(
        "Audit Service", "ConsumeGovernedEvent", "APPENDED",
        "9e88b7fa3599422685f121125b12b6c6", "correlation-125", "causation-125",
        "financial-calculation", "subject-125", "IN_MEMORY_REFERENCE");

    // Scoped to this D05 calculation/outbox scenario, not a global telemetry policy.
    private static void AssertCalculationLifecycleMetadataOnly(LifecycleSignal signal)
    {
        // Exact public wire fields exclude input/result payload, amount/value and provenance fields.
        var emitted = JsonSerializer.SerializeToElement(signal);
        Assert.Equal(
            ExpectedLifecycleFields.Order(StringComparer.Ordinal),
            emitted.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Contains((signal.Service, signal.Operation, signal.Outcome, signal.SubjectType), ExpectedCalculationCategories);
        Assert.Equal("IN_MEMORY_REFERENCE", signal.AdapterKind);
        // Identifiers are opaque; equality with governed scenario IDs is asserted by the caller.
        Assert.False(string.IsNullOrWhiteSpace(signal.RequestId));
        Assert.False(string.IsNullOrWhiteSpace(signal.CorrelationId));
        Assert.False(string.IsNullOrWhiteSpace(signal.SubjectId));
        Assert.False(string.IsNullOrWhiteSpace(signal.CausationId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothEventConsumersRejectMalformedOwnershipVersionAndLineage(bool failed)
    {
        var h = new Harness();
        await h.SeedAsync(right: failed ? 0 : 25);
        await h.App.ExecuteAsync(h.Execute(rule: failed ? "engineering.ratio" : "engineering.sum"));
        var source = Assert.Single(h.Repository.PendingEvents());
        var bad = new[]
        {
            source with { Producer = "other" },
            source with { EventName = "other" },
            source with { EventVersion = "2.0.0" },
            source with { CorrelationId = "" },
            source with { CausationId = null },
            source with { EventId = "" },
            source with { Payload = source.Payload with { CalculationLineageId = "" } },
            source with { Payload = source.Payload with { CalculationResultId = "mismatch" } },
            source with { Payload = source.Payload with { FailureCode = failed ? null : "invented" } },
            source with { Payload = null! },
        };
        foreach (var value in bad)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => h.Audit.ConsumeCalculationAsync(Harness.Wire(value)));
        }
        Assert.Empty(await h.Audit.ReadAllAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueriesValidateContractAndPayloadBeforeReading(bool explain)
    {
        var h = new Harness();
        await h.SeedAsync();
        var result = (await h.App.ExecuteAsync(h.Execute())).Data!;
        if (explain)
        {
            Assert.Equal(ContractErrorCategory.UnsupportedOperation,
                (await h.App.ExplainAsync(h.Explain(result.CalculationResultId) with { ContractVersion = "2" })).Error?.Category);
            Assert.Equal(ContractErrorCategory.ValidationError, (await h.App.ExplainAsync(h.Explain(""))).Error?.Category);
            Assert.Equal(ContractErrorCategory.NotFound, (await h.App.ExplainAsync(h.Explain("absent"))).Error?.Category);
        }
        else
        {
            Assert.Equal(ContractErrorCategory.UnsupportedOperation,
                (await h.App.GetResultAsync(h.Get(result.CalculationResultId) with { ContractVersion = "2" })).Error?.Category);
            Assert.Equal(ContractErrorCategory.ValidationError, (await h.App.GetResultAsync(h.Get(""))).Error?.Category);
            Assert.Equal(ContractErrorCategory.NotFound, (await h.App.GetResultAsync(h.Get("absent"))).Error?.Category);
        }
        Assert.Single(h.Repository.PendingEvents());
    }

    [Fact]
    public async Task SerializedContractsRejectUnknownMembersAndInvalidDecimalTypes()
    {
        var h = new Harness();
        await h.SeedAsync();
        var json = JsonSerializer.Serialize(h.Execute(), ContractJson.Options);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ContractRequest<ExecuteCalculation>>(
            json.Insert(1, "\"unexpected\":true,"), ContractJson.Options));
        var result = (await h.App.ExecuteAsync(h.Execute())).Data!;
        var resultJson = JsonSerializer.Serialize(result, ContractJson.Options);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CalculationResult>(
            resultJson.Replace("\"value\":125", "\"value\":\"untrusted\"", StringComparison.Ordinal), ContractJson.Options));
        Assert.Equal(Enumerable.Range(37, 5).Select(number => $"CID-{number:000}"), D05ContractCatalog.All.Select(item => item.Id));
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public async Task TypedDecimalRulesAreCultureIndependentAndExplicitlyRounded(string culture)
    {
        var h = new Harness();
        await h.SeedAsync(1, 6);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var result = (await h.App.ExecuteAsync(h.Execute(rule: "engineering.ratio"))).Data!;
            Assert.Equal(0.1667m, result.Value);
            Assert.Equal("ratio", result.Unit);
            Assert.Contains("ToEven", result.NumericSemantics, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("currency")]
    [InlineData("overflow")]
    [InlineData("roles")]
    public async Task NumericFailureCreatesOnlyFailedOutcomeNeverFinancialTruth(string scenario)
    {
        var h = new Harness();
        await h.SeedAsync(scenario == "overflow" ? decimal.MaxValue : 100, 25, rightUnit: scenario == "currency" ? "USD" : "INR");
        var request = h.Execute();
        if (scenario == "roles") { request = request with { Payload = request.Payload with { Inputs = [request.Payload.Inputs[0] with { Role = "unknown" }, request.Payload.Inputs[1]] } }; }
        var response = await h.App.ExecuteAsync(request);
        Assert.Equal(ContractOutcome.Failed, response.Outcome);
        Assert.Null(response.Data);
        Assert.Equal("CID-041", Assert.Single(h.Repository.PendingEvents()).ContractId);
        var authority = await h.Financial.GetFinancialFactAsync(h.Request(Vs02ContractNames.GetFinancialFact, new GetFinancialFact(h.References[0].FinancialFactId, Harness.Customer)));
        Assert.Equal(1, authority.Data!.Revision);
    }

    [Theory]
    [InlineData("mismatch")]
    [InlineData("absent")]
    [InlineData("unavailable")]
    public async Task InvalidAuthoritativeInputsDoNotCreateResultsOrEvents(string mode)
    {
        var h = new Harness();
        await h.SeedAsync();
        h.Reader.Mismatch = mode == "mismatch";
        h.Reader.MissingProvenance = mode == "absent";
        h.Reader.Unavailable = mode == "unavailable";
        Assert.NotEqual(ContractOutcome.Success, (await h.App.ExecuteAsync(h.Execute())).Outcome);
        Assert.Empty(h.Repository.PendingEvents());
    }

    [Fact]
    public async Task FinancialProfileTransportRejectsMalformedDependencyResponse()
    {
        using var handler = new BadResponseHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://synthetic.invalid/") };
        var reader = new FinancialProfileContractClient(client);
        var h = new Harness();
        await Assert.ThrowsAsync<HttpRequestException>(() => reader.GetFactAsync(
            h.Request(Vs02ContractNames.GetFinancialFact, new GetFinancialFact("fact", Harness.Customer)), CancellationToken.None));
    }

    private sealed class BadResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{malformed", Encoding.UTF8, "application/json") });
    }

    [Theory]
    [InlineData("extraction")]
    [InlineData("validation")]
    [InlineData("actor")]
    [InlineData("workload")]
    [InlineData("time")]
    [InlineData("correlation")]
    public async Task IncompleteAuthoritativeProvenanceCannotBecomeCalculationLineage(string field)
    {
        var h = new Harness();
        await h.SeedAsync();
        h.Reader.InvalidProvenanceField = field;
        var result = await h.App.ExecuteAsync(h.Execute());
        Assert.Equal("calculation.provenance.invalid", result.Error?.Code);
        Assert.Null(result.Data);
        Assert.Empty(h.Repository.PendingEvents());
    }
}
