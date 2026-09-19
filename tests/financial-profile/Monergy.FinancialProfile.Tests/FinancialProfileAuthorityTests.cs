using Monergy.Contracts;
using Monergy.Services.Audit.Application;
using Monergy.Services.Audit.Infrastructure;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Xunit;

namespace Monergy.FinancialProfile.Tests;

public sealed class FinancialProfileAuthorityTests
{
    [Fact]
    public async Task AllGovernedObjectTypesBecomeOneAuthorizedProviderNeutralProfile()
    {
        var repository = new InMemoryFinancialProfileRepository();
        var telemetry = new CapturingTelemetry();
        var application = new FinancialProfileApplication(repository, telemetry, TimeProvider.System);
        var facts = FinancialObjectTypes.All
            .Order(StringComparer.Ordinal)
            .Select((type, index) => Fact(type, $"source-{index + 1:00}", $"Object {index + 1:00}", index + 1))
            .ToArray();

        var normalized = await application.NormalizeSourceFactsAsync(NormalizeRequest(facts, "request-all", "idempotency-all"));
        var profile = await application.GetFinancialProfileAsync(FinancialProfileTestContext.Request(
            Vs02ContractNames.GetFinancialProfile,
            new GetFinancialProfile("financial-profile-001", FinancialProfileTestContext.CustomerId),
            "request-profile"));

        Assert.Equal(ContractOutcome.Success, normalized.Outcome);
        Assert.Equal(13, normalized.Data?.Facts.Count);
        Assert.All(normalized.Data!.Facts, fact => Assert.True(fact.Created));
        Assert.Equal(FinancialObjectTypes.All.Order(), profile.Data?.Facts.Select(fact => fact.FactType).Order());
        Assert.Equal(1, profile.Data?.Revision);
        Assert.Equal(14, repository.DrainOutbox().Count);
        Assert.All(telemetry.Signals, signal => Assert.Equal("IN_MEMORY_REFERENCE", signal.AdapterKind));
        Assert.DoesNotContain(telemetry.Signals, signal => signal.ToString()?.Contains("Object 01", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task QueriesReturnCurrentAuthorityAndFailClosedAcrossCustomers()
    {
        var repository = new InMemoryFinancialProfileRepository();
        var application = new FinancialProfileApplication(repository, new CapturingTelemetry(), TimeProvider.System);
        var normalized = await application.NormalizeSourceFactsAsync(
            NormalizeRequest([Fact(FinancialObjectTypes.BankAccount, "source-account", "Primary account", 100m)], "request-create", "key-create"));
        var factId = normalized.Data!.Facts.Single().FinancialFactId;

        var allowed = await application.GetFinancialFactAsync(FinancialProfileTestContext.Request(
            Vs02ContractNames.GetFinancialFact,
            new GetFinancialFact(factId, FinancialProfileTestContext.CustomerId),
            "request-fact"));
        var hidden = await application.GetFinancialFactAsync(FinancialProfileTestContext.Request(
            Vs02ContractNames.GetFinancialFact,
            new GetFinancialFact(factId, "customer-002"),
            "request-fact-hidden",
            security: FinancialProfileTestContext.Security("customer-002")));
        var mismatched = await application.GetFinancialProfileAsync(FinancialProfileTestContext.Request(
            Vs02ContractNames.GetFinancialProfile,
            new GetFinancialProfile("financial-profile-001", FinancialProfileTestContext.CustomerId),
            "request-profile-mismatch",
            security: FinancialProfileTestContext.Security("customer-002")));

        Assert.Equal(ContractOutcome.Success, allowed.Outcome);
        Assert.Equal(ContractErrorCategory.NotFound, hidden.Error?.Category);
        Assert.Equal(ContractErrorCategory.ValidationError, mismatched.Error?.Category);
    }

    [Fact]
    public async Task RevisionsPreserveHistoryProvenanceAndReplaySafety()
    {
        var repository = new InMemoryFinancialProfileRepository();
        var application = new FinancialProfileApplication(repository, new CapturingTelemetry(), TimeProvider.System);
        var createRequest = NormalizeRequest(
            [Fact(FinancialObjectTypes.Income, "source-income-1", "Salary", 100m)],
            "request-income-1",
            "key-income-1");
        var created = await application.NormalizeSourceFactsAsync(createRequest);
        repository.DrainOutbox();
        var replay = await application.NormalizeSourceFactsAsync(createRequest);
        Assert.False(replay.Data!.Facts.Single().Created);
        Assert.Empty(repository.DrainOutbox());

        var updated = await application.NormalizeSourceFactsAsync(NormalizeRequest(
            [Fact(FinancialObjectTypes.Income, "source-income-2", "Salary", 110m)],
            "request-income-2",
            "key-income-2"));
        var original = created.Data!.Facts.Single();
        var current = updated.Data!.Facts.Single();
        var events = repository.DrainOutbox();

        Assert.Equal(original.FinancialFactId, current.FinancialFactId);
        Assert.NotEqual(original.FinancialProvenanceId, current.FinancialProvenanceId);
        Assert.Equal(2, current.Revision);
        Assert.Equal([1, 2], repository.ReadFactHistory(current.FinancialFactId).Select(fact => fact.Revision));
        Assert.Contains(events, item => item is DomainEvent<FinancialFactChangedPayload> changed && changed.ContractId == "CID-035");
        Assert.Contains(events, item => item is DomainEvent<FinancialProfileChangedPayload> changed && changed.ContractId == "CID-036" && changed.Payload.Revision == 2);

        var conflict = await application.NormalizeSourceFactsAsync(createRequest with
        {
            Payload = createRequest.Payload with { Facts = [Fact(FinancialObjectTypes.Income, "source-conflict", "Salary", 200m)] },
        });
        Assert.Equal(ContractErrorCategory.Conflict, conflict.Error?.Category);
    }

    [Fact]
    public async Task IdempotencyIdentityIncludesContractVersionCustomerScopeAndCallerKey()
    {
        var repository = new InMemoryFinancialProfileRepository();
        var application = new FinancialProfileApplication(repository, new CapturingTelemetry(), TimeProvider.System);
        var customerOne = NormalizeRequest(
            [Fact(FinancialObjectTypes.Income, "source-one", "Salary", 100m)],
            "request-one",
            "shared-caller-key");
        var customerTwo = FinancialProfileTestContext.Request(
            Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts(
                "financial-profile-002",
                "customer-002",
                "processing-two",
                [Fact(FinancialObjectTypes.Expense, "source-two", "Rent", 20m) with { CustomerId = "customer-002" }],
                "provider-neutral-normalization/1.0.0",
                DateTimeOffset.UtcNow),
            "request-two",
            "shared-caller-key",
            FinancialProfileTestContext.Security("customer-002"));

        var first = await application.NormalizeSourceFactsAsync(customerOne);
        var second = await application.NormalizeSourceFactsAsync(customerTwo);
        var firstIdentity = FinancialNormalizationIdentity.From(customerOne);
        var secondIdentity = FinancialNormalizationIdentity.From(customerTwo);

        Assert.Equal(ContractOutcome.Success, first.Outcome);
        Assert.Equal(ContractOutcome.Success, second.Outcome);
        Assert.NotEqual(firstIdentity, secondIdentity);
        Assert.Equal(Vs02ContractNames.NormalizeSourceFacts, firstIdentity.ContractName);
        Assert.Equal(ContractGuard.CurrentVersion, firstIdentity.ContractVersion);
        Assert.Equal(FinancialProfileTestContext.CustomerId, firstIdentity.CustomerId);
        Assert.Equal("shared-caller-key", firstIdentity.IdempotencyKey);
    }

    [Fact]
    public async Task ApplicationOwnedProfileChangePreservesCorrelationAndCausation()
    {
        var repository = new InMemoryFinancialProfileRepository();
        var application = new FinancialProfileApplication(repository, new CapturingTelemetry(), TimeProvider.System);

        await application.NormalizeSourceFactsAsync(NormalizeRequest(
            [Fact(FinancialObjectTypes.BankAccount, "source-correlation", "Primary", 50m)],
            "request-profile-change",
            "key-profile-change"));
        var profileChanged = Assert.IsType<DomainEvent<FinancialProfileChangedPayload>>(
            repository.DrainOutbox().Single(item => item is DomainEvent<FinancialProfileChangedPayload>));

        Assert.Equal("CID-036", profileChanged.ContractId);
        Assert.Equal(FinancialProfileTestContext.CorrelationId, profileChanged.CorrelationId);
        Assert.Equal("request-profile-change", profileChanged.CausationId);
        Assert.Equal("Financial Profile Service", profileChanged.Producer);
    }

    [Fact]
    public async Task InvalidOrAmbiguousFactsNeverCreateAuthority()
    {
        var repository = new InMemoryFinancialProfileRepository();
        var application = new FinancialProfileApplication(repository, new CapturingTelemetry(), TimeProvider.System);
        var unsupported = await application.NormalizeSourceFactsAsync(NormalizeRequest(
            [Fact("HEALTH_SCORE", "source-score", "Score", 700m)],
            "request-score",
            "key-score"));
        var duplicate = Fact(FinancialObjectTypes.Expense, "source-expense-1", "Rent", 10m);
        var ambiguous = await application.NormalizeSourceFactsAsync(NormalizeRequest(
            [duplicate, duplicate with { SourceFactId = "source-expense-2" }],
            "request-duplicate",
            "key-duplicate"));

        Assert.Equal(ContractErrorCategory.ValidationError, unsupported.Error?.Category);
        Assert.Equal(ContractErrorCategory.ValidationError, ambiguous.Error?.Category);
        Assert.Empty(repository.DrainOutbox());
    }

    [Fact]
    public async Task FinancialGoalAuthorityAddsNoCalculationOrRecommendationSemantics()
    {
        var repository = new InMemoryFinancialProfileRepository();
        var application = new FinancialProfileApplication(repository, new CapturingTelemetry(), TimeProvider.System);
        var normalized = await application.NormalizeSourceFactsAsync(NormalizeRequest(
            [Fact(FinancialObjectTypes.FinancialGoal, "source-goal", "Emergency fund target", 500000m)],
            "request-goal",
            "key-goal"));

        var goal = normalized.Data!.Facts.Single();
        Assert.Equal(FinancialObjectTypes.FinancialGoal, goal.FactType);
        Assert.Equal("Emergency fund target", goal.Label);
        Assert.DoesNotContain("score", goal.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recommend", goal.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FactEventsAppendSafeAuditEvidenceWithoutTransferringAuthority()
    {
        var repository = new InMemoryFinancialProfileRepository();
        var financial = new FinancialProfileApplication(repository, new CapturingTelemetry(), TimeProvider.System);
        var audit = new AuditApplication(new AppendOnlyInMemoryAuditRepository(), new CapturingTelemetry(), TimeProvider.System);
        await financial.NormalizeSourceFactsAsync(NormalizeRequest(
            [Fact(FinancialObjectTypes.TaxRecord, "source-tax", "Assessment year", 1m)],
            "request-tax",
            "key-tax"));
        var outbox = repository.DrainOutbox();
        var factEvent = Assert.IsType<DomainEvent<FinancialFactChangedPayload>>(outbox.Single(item => item is DomainEvent<FinancialFactChangedPayload>));

        var first = await audit.ConsumeAsync(AuditEventMapper.From(factEvent));
        var replay = await audit.ConsumeAsync(AuditEventMapper.From(factEvent));

        Assert.Equal(first, replay);
        Assert.Single(await audit.ReadAllAsync());
        Assert.DoesNotContain("Assessment year", first.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => audit.ConsumeAsync(AuditEventMapper.From(
            Assert.IsType<DomainEvent<FinancialProfileChangedPayload>>(outbox.Single(item => item is DomainEvent<FinancialProfileChangedPayload>)))).GetAwaiter().GetResult());
    }

    private static ValidatedSourceFact Fact(string type, string sourceFactId, string label, decimal value) =>
        new(
            sourceFactId,
            FinancialProfileTestContext.CustomerId,
            type,
            label,
            value,
            "INR",
            new DateOnly(2026, 9, 1),
            "source:1",
            1m,
            "evidence-d04",
            "document-version-d04",
            "extraction/1.0.0",
            "validation/1.0.0");

    private static ContractRequest<NormalizeSourceFacts> NormalizeRequest(
        IReadOnlyList<ValidatedSourceFact> facts,
        string requestId,
        string idempotencyKey) =>
        FinancialProfileTestContext.Request(
            Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts(
                "financial-profile-001",
                FinancialProfileTestContext.CustomerId,
                "processing-d04",
                facts,
                "provider-neutral-normalization/1.0.0",
                DateTimeOffset.UtcNow),
            requestId,
            idempotencyKey);
}
