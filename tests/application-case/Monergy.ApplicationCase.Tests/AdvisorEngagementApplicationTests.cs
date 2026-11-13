using Monergy.Services.ApplicationCase.Application;
using Xunit;

namespace Monergy.ApplicationCase.Tests;

public sealed class AdvisorEngagementApplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
    private readonly AdvisorEngagementContext context = new("T001", "A300", "C001", 7, 3, 4, 2);

    [Fact]
    public async Task RecordsSourceBackedNoteCommunicationAndBoundedApplicationStages()
    {
        using var owner = new AdvisorEngagementApplication(new FixedClock(Now));

        var note = await owner.AddNoteAsync(context,
            new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0, "Prepare affordability discussion", ["fact-income-1"]),
            _ => Task.FromResult(context), default);
        var communication = await owner.RecordCommunicationAsync(context,
            new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 1, "Please confirm the supporting income period."),
            _ => Task.FromResult(context), default);
        var preparing = await owner.AdvanceApplicationAsync(context,
            new("cccccccccccccccccccccccccccccccc", 2, "Preparing"),
            _ => Task.FromResult(context), default);

        Assert.Equal(3, preparing.Version);
        Assert.Equal("Preparing", preparing.ApplicationStage);
        Assert.Collection(preparing.Activities,
            activity =>
            {
                Assert.Equal("NOTE", activity.Kind);
                Assert.Equal(["fact-income-1"], activity.SourceFactIds);
                Assert.Equal("Prepare affordability discussion", activity.Body);
            },
            activity =>
            {
                Assert.Equal("COMMUNICATION", activity.Kind);
                Assert.Empty(activity.SourceFactIds);
            },
            activity =>
            {
                Assert.Equal("APPLICATION_STAGE", activity.Kind);
                Assert.Equal("Preparing", activity.ApplicationStage);
            });
        Assert.Equal("Temporary data", note.DataDurability);
        Assert.Equal(2, communication.Version);
    }

    [Fact]
    public async Task RetryIsIdempotentAndOperationCannotBeReusedForAnotherAction()
    {
        using var owner = new AdvisorEngagementApplication(new FixedClock(Now));
        var request = new AddAdvisorNote("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0, "Bounded note", ["fact-1"]);

        var first = await owner.AddNoteAsync(context, request, _ => Task.FromResult(context), default);
        var replay = await owner.AddNoteAsync(context, request, _ => Task.FromResult(context), default);

        Assert.Equal(first.Version, replay.Version);
        Assert.Equal(first.ApplicationStage, replay.ApplicationStage);
        Assert.Single(replay.Activities);
        Assert.Equal(first.Activities[0], replay.Activities[0]);
        var failure = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.RecordCommunicationAsync(context,
            new(request.OperationId, 1, "Different action"), _ => Task.FromResult(context), default));
        Assert.Equal("OPERATION_REUSE_CONFLICT", failure.Code);
    }

    [Fact]
    public async Task ChangedAuthorityAndStaleVersionFailBeforeMutation()
    {
        using var owner = new AdvisorEngagementApplication(new FixedClock(Now));
        var changed = context with { ConsentVersion = context.ConsentVersion + 1 };

        var authority = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.AddNoteAsync(context,
            new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0, "Bounded note", ["fact-1"]),
            _ => Task.FromResult(changed), default));
        Assert.Equal("ADVISOR_AUTHORITY_CHANGED", authority.Code);
        Assert.Equal(0, (await owner.ReadAsync(context, default)).Version);

        _ = await owner.AddNoteAsync(context,
            new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 0, "Bounded note", ["fact-1"]),
            _ => Task.FromResult(context), default);
        var stale = await Assert.ThrowsAsync<ApplicationCaseException>(() => owner.RecordCommunicationAsync(context,
            new("cccccccccccccccccccccccccccccccc", 0, "Stale update"),
            _ => Task.FromResult(context), default));
        Assert.Equal("ADVISOR_ENGAGEMENT_VERSION_CHANGED", stale.Code);
    }

    [Fact]
    public async Task RejectsUnreferencedNotesInvalidTextAndStageSkipping()
    {
        using var owner = new AdvisorEngagementApplication(new FixedClock(Now));

        Assert.Equal("ADVISOR_SOURCE_REFERENCE_INVALID", (await Assert.ThrowsAsync<ApplicationCaseException>(() =>
            owner.AddNoteAsync(context, new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0, "No source", []),
                _ => Task.FromResult(context), default))).Code);
        Assert.Equal("ADVISOR_COMMUNICATION_INVALID", (await Assert.ThrowsAsync<ApplicationCaseException>(() =>
            owner.RecordCommunicationAsync(context,
                new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 0, "unsafe\ntext"),
                _ => Task.FromResult(context), default))).Code);
        Assert.Equal("ADVISOR_APPLICATION_STAGE_INVALID", (await Assert.ThrowsAsync<ApplicationCaseException>(() =>
            owner.AdvanceApplicationAsync(context,
                new("cccccccccccccccccccccccccccccccc", 0, "Ready for submission"),
                _ => Task.FromResult(context), default))).Code);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
