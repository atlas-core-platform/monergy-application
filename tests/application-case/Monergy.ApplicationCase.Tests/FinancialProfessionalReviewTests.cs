using Monergy.Services.ApplicationCase.Application;
using Xunit;

namespace Monergy.ApplicationCase.Tests;

public sealed class FinancialProfessionalReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
    private readonly FinancialProfessionalContext professional = new("T001", "A600", 9, 2);
    private readonly FinancialProfessionalReviewContext authority = new("T001", "A600", "C001", 9, 2, 4, 7);

    [Fact]
    public async Task AssignedProfessionalReviewsSourcesRecordsNoteAndSignsSourceSnapshot()
    {
        using var owner = Owner();
        var initial = Assert.Single(await owner.ListAsync(professional, Authorize(authority), default));
        var profile = await owner.ReviewSourceAsync(initial.ReviewId, "profile-summary", professional,
            new(Op('a'), 1), Authorize(authority), default);
        var evidence = await owner.ReviewSourceAsync(initial.ReviewId, "supporting-evidence", professional,
            new(Op('b'), 2), Authorize(authority), default);
        var note = await owner.AddNoteAsync(initial.ReviewId, professional,
            new(Op('c'), 3, "Reviewed the customer-confirmed source record.", ["profile-summary", "supporting-evidence"]),
            Authorize(authority), default);
        var certified = await owner.CertifyAsync(initial.ReviewId, professional,
            new(Op('d'), 4, "Certified with exceptions", "Certification is limited to the assigned source versions."),
            Authorize(authority), default);

        Assert.Equal("Under review", profile.State);
        Assert.All(evidence.Sources, source => Assert.True(source.Reviewed));
        Assert.Single(note.Notes);
        Assert.Equal("Certified", certified.State);
        Assert.Equal("A600", certified.Certification?.CertifiedBy);
        Assert.Equal(7, certified.Certification?.ConsentVersion);
        Assert.Equal(2, certified.Certification?.Sources.Count);
        Assert.Equal("Temporary data", certified.DataDurability);
    }

    [Fact]
    public async Task ForeignActorTenantAndChangedAuthorityFailClosed()
    {
        using var owner = Owner();
        Assert.Empty(await owner.ListAsync(professional with { ActorId = "A601" },
            Authorize(authority with { ActorId = "A601" }), default));
        Assert.Empty(await owner.ListAsync(professional with { TenantId = "T002" },
            Authorize(authority with { TenantId = "T002" }), default));
        var foreign = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.ReviewSourceAsync(
            "review-001", "profile-summary", professional with { ActorId = "A601" }, new(Op('a'), 1),
            Authorize(authority with { ActorId = "A601" }), default));
        Assert.Equal("FINANCIAL_REVIEW_NOT_FOUND", foreign.Code);
        var changed = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.ReviewSourceAsync(
            "review-001", "profile-summary", professional, new(Op('b'), 1),
            Authorize(authority with { ConsentVersion = 0 }), default));
        Assert.Equal("FINANCIAL_PROFESSIONAL_AUTHORITY_CHANGED", changed.Code);
    }

    [Fact]
    public async Task RetryIsIdempotentAndCertificationRequiresEveryCurrentSource()
    {
        using var owner = Owner();
        var request = new ReviewFinancialSource(Op('a'), 1);
        var first = await owner.ReviewSourceAsync("review-001", "profile-summary", professional,
            request, Authorize(authority), default);
        var replay = await owner.ReviewSourceAsync("review-001", "profile-summary", professional,
            request, Authorize(authority), default);
        Assert.Equal(first.Version, replay.Version);
        var incomplete = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.CertifyAsync(
            "review-001", professional, new(Op('b'), 2, "Unable to certify", "Evidence still needs review."),
            Authorize(authority), default));
        Assert.Equal("FINANCIAL_SOURCE_REVIEW_INCOMPLETE", incomplete.Code);
        var stale = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.AddNoteAsync(
            "review-001", professional, new(Op('c'), 1, "Stale note.", ["profile-summary"]),
            Authorize(authority), default));
        Assert.Equal("FINANCIAL_REVIEW_VERSION_CHANGED", stale.Code);
    }

    private static FinancialProfessionalReview Owner()
    {
        var owner = new FinancialProfessionalReview(new FixedClock(Now));
        owner.AddFixture("T001", "A600", "C001", "review-001", "Financial review 001",
            [("profile-summary", "Customer-reviewed financial profile", "Financial Profile", "financial-profile-source-001", 1),
             ("supporting-evidence", "Supporting evidence index", "Evidence", "evidence-source-001", 1)],
            Now.AddDays(-1));
        return owner;
    }

    private static string Op(char value) => new(value, 32);
    private static Func<string, CancellationToken, Task<FinancialProfessionalReviewContext>> Authorize(
        FinancialProfessionalReviewContext value) => (customer, _) =>
            Task.FromResult(customer == value.CustomerId ? value : throw new ApplicationCaseException("CUSTOMER_CONTEXT_INVALID", 400));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
