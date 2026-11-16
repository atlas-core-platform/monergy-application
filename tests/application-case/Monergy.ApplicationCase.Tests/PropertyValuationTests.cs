using Monergy.Services.ApplicationCase.Application;
using Xunit;

namespace Monergy.ApplicationCase.Tests;

public sealed class PropertyValuationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly PropertyValuerContext valuer = new("T001", "A700", 10, 2);
    private readonly PropertyValuationContext authority = new("T001", "A700", "C001", 10, 2, 4, 7);

    [Fact]
    public async Task AssignedValuerInspectsExactEvidenceAndRecordsVersionedValuation()
    {
        using var owner = Owner();
        var initial = Assert.Single(await owner.ListAsync(valuer, Authorize(authority), default));
        var property = await owner.InspectEvidenceAsync(initial.ValuationId, "property-record", valuer,
            new(Op('a'), 1), Authorize(authority), default);
        var evidence = await owner.InspectEvidenceAsync(initial.ValuationId, "title-evidence", valuer,
            new(Op('b'), 2), Authorize(authority), default);
        var valued = await owner.RecordValuationAsync(initial.ValuationId, valuer,
            new(Op('c'), 3, 425000m, "USD", new DateOnly(2026, 11, 15),
                "Comparable sales inspection", "The conclusion is limited to the assigned evidence versions."),
            Authorize(authority), default);

        Assert.Equal("Inspection in progress", property.State);
        Assert.All(evidence.Evidence, item => Assert.True(item.Inspected));
        Assert.Equal("Valued", valued.State);
        var revision = Assert.Single(valued.Revisions);
        Assert.Equal(425000m, revision.MarketValue);
        Assert.Equal("A700", revision.RecordedBy);
        Assert.Equal(7, revision.ConsentVersion);
        Assert.Equal(2, revision.Evidence.Count);
    }

    [Fact]
    public async Task RevisionHistoryIsAppendOnlyAndCustomerSeesLatestProfessionalResult()
    {
        using var owner = Owner();
        await InspectAll(owner);
        var first = await owner.RecordValuationAsync("valuation-001", valuer,
            new(Op('c'), 3, 425000m, "USD", new DateOnly(2026, 11, 15),
                "Comparable sales", "Initial inspected conclusion."), Authorize(authority), default);
        var second = await owner.RecordValuationAsync("valuation-001", valuer,
            new(Op('d'), 4, 430000m, "USD", new DateOnly(2026, 11, 16),
                "Comparable sales", "Revised conclusion after professional review."), Authorize(authority), default);

        Assert.Equal([1, 2], second.Revisions.Select(item => item.Revision));
        Assert.Equal(425000m, first.Revisions[0].MarketValue);
        var customer = Assert.Single(await owner.ListCustomerResultsAsync(
            new("T001", "A100", "C001", 1, 1, AssistanceCaseApplication.CustomerCapability), default));
        Assert.Equal(2, customer.Revision);
        Assert.Equal(430000m, customer.MarketValue);
        Assert.Equal("Temporary data", customer.DataDurability);
    }

    [Fact]
    public async Task ForeignActorTenantAndChangedAuthorityFailClosed()
    {
        using var owner = Owner();
        Assert.Empty(await owner.ListAsync(valuer with { ActorId = "A701" },
            Authorize(authority with { ActorId = "A701" }), default));
        Assert.Empty(await owner.ListAsync(valuer with { TenantId = "T002" },
            Authorize(authority with { TenantId = "T002" }), default));
        var foreign = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.InspectEvidenceAsync(
            "valuation-001", "property-record", valuer with { ActorId = "A701" }, new(Op('a'), 1),
            Authorize(authority with { ActorId = "A701" }), default));
        Assert.Equal("PROPERTY_VALUATION_NOT_FOUND", foreign.Code);
        var changed = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.InspectEvidenceAsync(
            "valuation-001", "property-record", valuer, new(Op('b'), 1),
            Authorize(authority with { ConsentVersion = 0 }), default));
        Assert.Equal("PROPERTY_VALUER_AUTHORITY_CHANGED", changed.Code);
        Assert.Empty(await owner.ListCustomerResultsAsync(
            new("T002", "A100", "C001", 1, 1, AssistanceCaseApplication.CustomerCapability), default));
    }

    [Fact]
    public async Task RetryIsIdempotentAndEvidenceVersionDriftRequiresReassignment()
    {
        using var owner = Owner();
        var request = new InspectPropertyEvidence(Op('a'), 1);
        var first = await owner.InspectEvidenceAsync("valuation-001", "property-record", valuer,
            request, Authorize(authority), default);
        var replay = await owner.InspectEvidenceAsync("valuation-001", "property-record", valuer,
            request, Authorize(authority), default);
        Assert.Equal(first.Version, replay.Version);
        var reused = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.InspectEvidenceAsync(
            "valuation-001", "title-evidence", valuer, request, Authorize(authority), default));
        Assert.Equal("OPERATION_REUSE_CONFLICT", reused.Code);
        owner.ChangeEvidenceVersion("T001", "valuation-001", "title-evidence", 2);
        var stale = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.InspectEvidenceAsync(
            "valuation-001", "title-evidence", valuer, new(Op('b'), 2), Authorize(authority), default));
        Assert.Equal("PROPERTY_EVIDENCE_VERSION_CHANGED", stale.Code);
        var incomplete = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.RecordValuationAsync(
            "valuation-001", valuer, new(Op('c'), 2, 425000m, "USD", new DateOnly(2026, 11, 15),
                "Comparable sales", "Exact evidence is required."), Authorize(authority), default));
        Assert.Equal("PROPERTY_INSPECTION_INCOMPLETE", incomplete.Code);
    }

    private static PropertyValuationApplication Owner()
    {
        var owner = new PropertyValuationApplication(new FixedClock(Now));
        owner.AddFixture("T001", "A700", "C001", "valuation-001", "property-saved", "Family home",
            [("property-record", "Customer-reviewed property record", "Financial Profile", "property-saved", 1),
             ("title-evidence", "Assigned title evidence", "Evidence", "evidence-source-001", 1)],
            Now.AddDays(-1));
        return owner;
    }

    private async Task InspectAll(PropertyValuationApplication owner)
    {
        _ = await owner.InspectEvidenceAsync("valuation-001", "property-record", valuer,
            new(Op('a'), 1), Authorize(authority), default);
        _ = await owner.InspectEvidenceAsync("valuation-001", "title-evidence", valuer,
            new(Op('b'), 2), Authorize(authority), default);
    }

    private static string Op(char value) => new(value, 32);
    private static Func<string, CancellationToken, Task<PropertyValuationContext>> Authorize(
        PropertyValuationContext value) => (customer, _) =>
            Task.FromResult(customer == value.CustomerId ? value :
                throw new ApplicationCaseException("CUSTOMER_CONTEXT_INVALID", 400));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
