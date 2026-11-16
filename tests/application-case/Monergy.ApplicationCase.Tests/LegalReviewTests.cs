using Monergy.Services.ApplicationCase.Application;
using Xunit;

namespace Monergy.ApplicationCase.Tests;

public sealed class LegalReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly LegalAdvisorContext advisor = new("T001", "A800", 10, 2);
    private readonly LegalReviewContext authority = new("T001", "A800", "C001", 10, 2, 4, 7);

    [Fact]
    public async Task AssignedAdvisorInspectsExactDocumentsAndRecordsVersionedOutcome()
    {
        using var owner = Owner();
        var initial = Assert.Single(await owner.ListAsync(advisor, Authorize(authority), default));
        var inspected = await owner.InspectEvidenceAsync(initial.ReviewId, "estate-document", advisor,
            new(Op('a'), 1), Authorize(authority), default);
        inspected = await owner.InspectEvidenceAsync(initial.ReviewId, "identity-document", advisor,
            new(Op('d'), 2), Authorize(authority), default);
        var reviewed = await owner.RecordReviewAsync(initial.ReviewId, advisor,
            new(Op('b'), 3, "ACTION_REQUIRED", "Update the named beneficiary before execution."),
            Authorize(authority), default);

        Assert.All(inspected.Evidence, item => Assert.True(item.Inspected));
        Assert.Equal("Reviewed", reviewed.State);
        var revision = Assert.Single(reviewed.Revisions);
        Assert.Equal("ACTION_REQUIRED", revision.Outcome);
        Assert.Equal("A800", revision.RecordedBy);
        Assert.Equal(7, revision.ConsentVersion);
        Assert.Equal(2, revision.Evidence.Count);
    }

    [Fact]
    public async Task HistoryIsAppendOnlyAndCustomerSeesOnlyLatestSafeResult()
    {
        using var owner = Owner();
        await InspectAll(owner);
        var first = await owner.RecordReviewAsync("review-001", advisor,
            new(Op('c'), 3, "ACTION_REQUIRED", "Initial professional finding."), Authorize(authority), default);
        var second = await owner.RecordReviewAsync("review-001", advisor,
            new(Op('d'), 4, "NO_ISSUE_IDENTIFIED", "Revised professional finding."), Authorize(authority), default);

        Assert.Equal([1, 2], second.Revisions.Select(item => item.Revision));
        Assert.Equal("ACTION_REQUIRED", first.Revisions[0].Outcome);
        var customer = Assert.Single(await owner.ListCustomerResultsAsync(
            new("T001", "A100", "C001", 1, 1, AssistanceCaseApplication.CustomerCapability), default));
        Assert.Equal(2, customer.Revision);
        Assert.Equal("NO_ISSUE_IDENTIFIED", customer.Outcome);
        Assert.Equal("Temporary data", customer.DataDurability);
    }

    [Fact]
    public async Task ForeignActorTenantAndChangedAuthorityFailClosed()
    {
        using var owner = Owner();
        Assert.Empty(await owner.ListAsync(advisor with { ActorId = "A801" },
            Authorize(authority with { ActorId = "A801" }), default));
        Assert.Empty(await owner.ListAsync(advisor with { TenantId = "T002" },
            Authorize(authority with { TenantId = "T002" }), default));
        var foreign = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.InspectEvidenceAsync(
            "review-001", "estate-document", advisor with { ActorId = "A801" }, new(Op('a'), 1),
            Authorize(authority with { ActorId = "A801" }), default));
        Assert.Equal("LEGAL_REVIEW_NOT_FOUND", foreign.Code);
        var changed = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.InspectEvidenceAsync(
            "review-001", "estate-document", advisor, new(Op('b'), 1),
            Authorize(authority with { ConsentVersion = 0 }), default));
        Assert.Equal("LEGAL_ADVISOR_AUTHORITY_CHANGED", changed.Code);
        Assert.Empty(await owner.ListCustomerResultsAsync(
            new("T002", "A100", "C001", 1, 1, AssistanceCaseApplication.CustomerCapability), default));
    }

    [Fact]
    public async Task RetryIsIdempotentAndDocumentVersionDriftRequiresReassignment()
    {
        using var owner = Owner();
        var request = new InspectLegalDocument(Op('a'), 1);
        var first = await owner.InspectEvidenceAsync("review-001", "estate-document", advisor,
            request, Authorize(authority), default);
        var replay = await owner.InspectEvidenceAsync("review-001", "estate-document", advisor,
            request, Authorize(authority), default);
        Assert.Equal(first.Version, replay.Version);
        owner.ChangeEvidenceVersion("T001", "review-001", "identity-document", 2);
        var stale = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.InspectEvidenceAsync(
            "review-001", "identity-document", advisor, new(Op('b'), 2), Authorize(authority), default));
        Assert.Equal("LEGAL_DOCUMENT_VERSION_CHANGED", stale.Code);
        var incomplete = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.RecordReviewAsync(
            "review-001", advisor, new(Op('c'), 2, "UNABLE_TO_CONCLUDE", "Exact documents are required."),
            Authorize(authority), default));
        Assert.Equal("LEGAL_DOCUMENT_INSPECTION_INCOMPLETE", incomplete.Code);
    }

    private static LegalReviewApplication Owner()
    {
        var owner = new LegalReviewApplication(new FixedClock(Now));
        owner.AddFixture("T001", "A800", "C001", "review-001", "matter-estate", "Family estate documents",
            [("estate-document", "Assigned estate document", "Evidence", "evidence-source-001", 1),
             ("identity-document", "Assigned identity document", "Evidence", "evidence-source-002", 1)],
            Now.AddDays(-1));
        return owner;
    }

    private async Task InspectAll(LegalReviewApplication owner)
    {
        _ = await owner.InspectEvidenceAsync("review-001", "estate-document", advisor,
            new(Op('a'), 1), Authorize(authority), default);
        _ = await owner.InspectEvidenceAsync("review-001", "identity-document", advisor,
            new(Op('b'), 2), Authorize(authority), default);
    }

    private static string Op(char value) => new(value, 32);
    private static Func<string, CancellationToken, Task<LegalReviewContext>> Authorize(LegalReviewContext value) =>
        (customer, _) => Task.FromResult(customer == value.CustomerId ? value :
            throw new ApplicationCaseException("CUSTOMER_CONTEXT_INVALID", 400));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
