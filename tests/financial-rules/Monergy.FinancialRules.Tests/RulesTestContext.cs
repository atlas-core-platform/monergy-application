using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Audit.Application;
using Monergy.Services.Audit.Infrastructure;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Monergy.Services.FinancialRules.Application;
using Monergy.Services.FinancialRules.Domain;
using Monergy.Services.FinancialRules.Infrastructure;
using Xunit;

namespace Monergy.FinancialRules.Tests;

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-20T00:00:00Z", CultureInfo.InvariantCulture);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class TestTelemetry : ILifecycleTelemetry
{
    public ConcurrentQueue<LifecycleSignal> Signals { get; } = [];
    public void Record(LifecycleSignal signal) => Signals.Enqueue(signal);
}

// A serialized contract adapter to the real Financial Profile application, not its repository.
internal sealed class OwnerReader(FinancialProfileApplication owner) : IFinancialInputReader
{
    public Action? BeforeProvenance { get; set; }
    public bool Unavailable { get; set; }
    public bool MissingProvenance { get; set; }
    public bool Drift { get; set; }
    public bool Mismatch { get; set; }
    public string? InvalidProvenanceField { get; set; }
    public ConcurrentQueue<string> Calls { get; } = [];

    public async Task<ContractResult<AuthoritativeFinancialFact>> GetFactAsync(ContractRequest<GetFinancialFact> request, CancellationToken cancellationToken)
    {
        Calls.Enqueue("CID-032");
        if (Unavailable) { throw new HttpRequestException("synthetic dependency failure"); }
        var response = await owner.GetFinancialFactAsync(Harness.Wire(request), cancellationToken);
        return Harness.Wire(Mismatch && response.Data is not null
            ? response with { Data = response.Data with { CustomerId = "other-customer" } } : response);
    }

    public async Task<ContractResult<FinancialProvenance>> GetProvenanceAsync(ContractRequest<GetFinancialProvenance> request, CancellationToken cancellationToken)
    {
        Calls.Enqueue("CID-033");
        BeforeProvenance?.Invoke();
        if (Unavailable) { throw new HttpRequestException("synthetic dependency failure"); }
        if (MissingProvenance)
        {
            return ContractResult<FinancialProvenance>.Rejected(request, "history.missing", ContractErrorCategory.NotFound, "Synthetic absent history");
        }

        var result = await owner.GetFinancialProvenanceAsync(Harness.Wire(request), cancellationToken);
        if (result.Data is not null && InvalidProvenanceField is not null)
        {
            result = result with
            {
                Data = InvalidProvenanceField switch
                {
                    "extraction" => result.Data with { ExtractionVersion = "" },
                    "validation" => result.Data with { ValidationVersion = "" },
                    "actor" => result.Data with { ActorId = "" },
                    "workload" => result.Data with { WorkloadIdentityId = "" },
                    "time" => result.Data with { RecordedAt = default },
                    _ => result.Data with { CorrelationId = "" },
                }
            };
        }
        return Harness.Wire(Drift && result.Data is not null
            ? result with { Data = result.Data with { NormalizationVersion = "changed" } } : result);
    }
}

internal sealed class Harness
{
    public const string Customer = "synthetic-customer";
    public TestClock Clock { get; } = new();
    public TestTelemetry Telemetry { get; } = new();
    public TrustedSecurityContext Security { get; }
    public ReferenceCalculationAccessPolicy Policy { get; }
    public ReferenceRuleRegistry Registry { get; }
    public InMemoryCalculationRepository Repository { get; }
    public FinancialProfileApplication Financial { get; }
    public OwnerReader Reader { get; }
    public FinancialRulesApplication App { get; }
    public AuditApplication Audit { get; }
    public ImmutableArray<CalculationInputReference> References { get; private set; }

    public static IConfiguration Config(string zone = "LOCAL") => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Monergy:ReferenceAdapters"] = "true", ["Monergy:ExecutionZone"] = zone }).Build();

    public Harness()
    {
        Security = new(new("synthetic-actor", "CUSTOMER", Clock.Now, "authentication"),
            new("reporting-reference", "reporting-workload"), new("engineering-calculation", "consent", "authorization", Customer));
        Policy = new(Config(), Clock);
        Grant();
        Registry = new(Config());
        Registry.Register(new EngineeringRule("engineering.sum", "1.0.0"));
        Registry.Register(new EngineeringRule("engineering.sum", "2.0.0"));
        Registry.Register(new EngineeringRule("engineering.ratio", "1.0.0"));
        Repository = new(Config());
        Financial = new(new InMemoryFinancialProfileRepository(), Telemetry, Clock);
        Reader = new(Financial);
        App = new(Registry, Reader, Policy, Repository, Telemetry, Clock);
        Audit = new(new AppendOnlyInMemoryAuditRepository(), Telemetry, Clock);
    }

    public void Grant(bool revoked = false, bool consentRevoked = false, DateTimeOffset? expiry = null, TrustedSecurityContext? security = null, bool consentRequired = true) =>
        Policy.SetGrant(new(security ?? Security, expiry ?? Clock.Now.AddHours(1), revoked, consentRequired, Clock.Now.AddMinutes(30), consentRevoked));

    public ContractRequest<T> Request<T>(string name, T payload, string? key = null, TrustedSecurityContext? security = null) =>
        new(name, ContractGuard.CurrentVersion, "request-" + Guid.NewGuid().ToString("N"), "correlation", "causation", security ?? Security, key, payload);

    public ContractRequest<ExecuteCalculation> Execute(string key = "execute-1", string rule = "engineering.sum", string version = "1.0.0") =>
        Wire(Request(FinancialRulesContractNames.ExecuteCalculation, new ExecuteCalculation(Customer, rule, version, References), key));

    public ContractRequest<GetCalculationResult> Get(string id) => Wire(Request(FinancialRulesContractNames.GetCalculationResult, new GetCalculationResult(Customer, id)));
    public ContractRequest<ExplainCalculation> Explain(string id) => Wire(Request(FinancialRulesContractNames.ExplainCalculation, new ExplainCalculation(Customer, id)));

    public async Task SeedAsync(decimal left = 100, decimal right = 25, int generation = 1, string rightUnit = "INR")
    {
        var facts = new[] { Fact("left", left, generation, "INR"), Fact("right", right, generation, rightUnit) };
        await NormalizeAsync(facts, "seed-" + generation);
    }

    public async Task NormalizeAsync(IReadOnlyList<ValidatedSourceFact> facts, string key)
    {
        var normalized = Wire(await Financial.NormalizeSourceFactsAsync(Wire(Request(Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts("profile", Customer, "processing", facts, "fixture-normalization/1", Clock.Now), key))));
        Assert.Equal(ContractOutcome.Success, normalized.Outcome);
        References = normalized.Data!.Facts.Select((fact, index) =>
            new CalculationInputReference(index == 0 ? "left" : "right", fact.FinancialFactId, null)).ToImmutableArray();
    }

    private static ValidatedSourceFact Fact(string role, decimal value, int generation, string currency) =>
        new("source-" + role + "-" + generation, Customer, role == "left" ? "INCOME" : "EXPENSE", role,
            value, currency, new DateOnly(2026, 9, 20), "synthetic:1", 1, "evidence", "document-version",
            "fixture-extraction/1", "fixture-validation/1");

    public static T Wire<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ContractJson.Options), ContractJson.Options)!;
}

internal sealed class AuditSink(AuditApplication audit) : ICalculationEventSink
{
    public bool FailBeforeConsume { get; set; }
    public bool LoseAcknowledgement { get; set; }
    public async Task PublishAsync(DomainEvent<CalculationOutcomePayload> outcome, CancellationToken cancellationToken)
    {
        if (FailBeforeConsume) { throw new IOException("Synthetic receiver unavailable"); }
        await audit.ConsumeCalculationAsync(Harness.Wire(outcome), cancellationToken);
        if (LoseAcknowledgement) { throw new IOException("Synthetic acknowledgement loss"); }
    }
}
