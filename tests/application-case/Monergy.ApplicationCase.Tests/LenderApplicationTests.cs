using Monergy.Services.ApplicationCase.Application;
using Xunit;

namespace Monergy.ApplicationCase.Tests;

public sealed class LenderApplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private readonly LenderContext lender = new("T001", "A500", 8, 2);

    [Fact]
    public async Task AssignedLenderReviewsRequiredDocumentsAndRecordsOwnedDecision()
    {
        using var owner = Owner();
        var initial = Assert.Single(await owner.ListAsync(lender, default));
        Assert.Equal("Assigned", initial.State);
        Assert.Equal(3, initial.RequiredDocuments.Count);

        var identity = await owner.ReviewDocumentAsync(initial.ApplicationId, "identity", lender,
            new(Op('a'), 1), Confirm(lender), default);
        var income = await owner.ReviewDocumentAsync(initial.ApplicationId, "income", lender,
            new(Op('b'), 2), Confirm(lender), default);
        var bank = await owner.ReviewDocumentAsync(initial.ApplicationId, "bank-statements", lender,
            new(Op('c'), 3), Confirm(lender), default);
        var decided = await owner.RecordDecisionAsync(initial.ApplicationId, lender,
            new(Op('d'), 4, "Approved", "Approved under the lender's governed review policy."),
            Confirm(lender), default);

        Assert.Equal("Under review", identity.State);
        Assert.Equal(3, income.Version);
        Assert.All(bank.RequiredDocuments, document => Assert.True(document.Reviewed));
        Assert.Equal("Decided", decided.State);
        Assert.Equal("Approved", decided.Decision?.Decision);
        Assert.Equal("Temporary data", decided.DataDurability);
    }

    [Fact]
    public async Task ForeignLenderAndTenantCannotEnumerateOrMutateAssignment()
    {
        using var owner = Owner();
        Assert.Empty(await owner.ListAsync(lender with { ActorId = "A501" }, default));
        Assert.Empty(await owner.ListAsync(lender with { TenantId = "T002" }, default));
        var failure = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.ReviewDocumentAsync(
            "loan-001", "identity", lender with { ActorId = "A501" }, new(Op('a'), 1),
            Confirm(lender with { ActorId = "A501" }), default));
        Assert.Equal("LENDER_APPLICATION_NOT_FOUND", failure.Code);
    }

    [Fact]
    public async Task RetryIsIdempotentAndStaleOrChangedAuthorityFailsClosed()
    {
        using var owner = Owner();
        var request = new ReviewLenderDocument(Op('a'), 1);
        var first = await owner.ReviewDocumentAsync("loan-001", "identity", lender, request, Confirm(lender), default);
        var replay = await owner.ReviewDocumentAsync("loan-001", "identity", lender, request, Confirm(lender), default);
        Assert.Equal(first.ApplicationId, replay.ApplicationId);
        Assert.Equal(first.Version, replay.Version);
        Assert.Equal(first.State, replay.State);
        Assert.Equal(first.RequiredDocuments, replay.RequiredDocuments);

        var stale = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.ReviewDocumentAsync(
            "loan-001", "income", lender, new(Op('b'), 1), Confirm(lender), default));
        Assert.Equal("LENDER_APPLICATION_VERSION_CHANGED", stale.Code);
        var changed = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.ReviewDocumentAsync(
            "loan-001", "income", lender, new(Op('c'), 2),
            Confirm(lender with { SubjectVersion = 3 }), default));
        Assert.Equal("LENDER_AUTHORITY_CHANGED", changed.Code);
    }

    [Fact]
    public async Task DecisionRequiresEveryPresentDocumentAndCannotBeDuplicated()
    {
        using var owner = Owner();
        var incomplete = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.RecordDecisionAsync(
            "loan-001", lender, new(Op('a'), 1, "Declined", "Missing the required document review."),
            Confirm(lender), default));
        Assert.Equal("LENDER_DOCUMENT_REVIEW_INCOMPLETE", incomplete.Code);

        var version = 1;
        foreach (var code in new[] { "identity", "income", "bank-statements" })
        {
            await owner.ReviewDocumentAsync("loan-001", code, lender,
                new(Op((char)('a' + version)), version), Confirm(lender), default);
            version++;
        }
        _ = await owner.RecordDecisionAsync("loan-001", lender,
            new(Op('e'), version, "More information required", "A current supporting schedule is required."),
            Confirm(lender), default);
        var duplicate = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.RecordDecisionAsync(
            "loan-001", lender, new(Op('f'), version + 1, "Approved", "Duplicate decision attempt."),
            Confirm(lender), default));
        Assert.Equal("LENDER_APPLICATION_ALREADY_DECIDED", duplicate.Code);
    }

    private static LenderApplication Owner()
    {
        var owner = new LenderApplication(new FixedClock(Now));
        owner.AddFixture("T001", "A500", "loan-001", "Lending application 001",
            [("identity", "Identity document", true), ("income", "Income evidence", true),
                ("bank-statements", "Bank statements", true)], Now.AddDays(-1));
        return owner;
    }

    private static string Op(char value) => new(value, 32);
    private static Func<CancellationToken, Task<LenderContext>> Confirm(LenderContext value) =>
        _ => Task.FromResult(value);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
