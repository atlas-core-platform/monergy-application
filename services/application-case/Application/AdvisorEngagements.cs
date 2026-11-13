using System.Security.Cryptography;
using Monergy.Platform;

namespace Monergy.Services.ApplicationCase.Application;

public sealed record AdvisorEngagementContext(
    string TenantId,
    string ActorId,
    string CustomerId,
    long PolicyVersion,
    long SubjectVersion,
    long CustomerVersion,
    long ConsentVersion);

public sealed record AdvisorActivityView(
    string ActivityId,
    string Kind,
    string? Body,
    IReadOnlyList<string> SourceFactIds,
    string? ApplicationStage,
    int Version,
    DateTimeOffset OccurredAt);

public sealed record AdvisorEngagementView(
    string CustomerId,
    int Version,
    string ApplicationStage,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<AdvisorActivityView> Activities,
    string DataDurability);

public sealed record AddAdvisorNote(
    string OperationId,
    int ExpectedVersion,
    string Note,
    IReadOnlyList<string> SourceFactIds);

public sealed record RecordCustomerCommunication(
    string OperationId,
    int ExpectedVersion,
    string Message);

public sealed record AdvanceAdvisorApplication(
    string OperationId,
    int ExpectedVersion,
    string Stage);

public interface IAdvisorEngagementApplication
{
    Task<AdvisorEngagementView> ReadAsync(AdvisorEngagementContext context, CancellationToken ct);
    Task<AdvisorEngagementView> AddNoteAsync(AdvisorEngagementContext before, AddAdvisorNote request,
        Func<CancellationToken, Task<AdvisorEngagementContext>> confirm, CancellationToken ct);
    Task<AdvisorEngagementView> RecordCommunicationAsync(AdvisorEngagementContext before,
        RecordCustomerCommunication request,
        Func<CancellationToken, Task<AdvisorEngagementContext>> confirm, CancellationToken ct);
    Task<AdvisorEngagementView> AdvanceApplicationAsync(AdvisorEngagementContext before,
        AdvanceAdvisorApplication request,
        Func<CancellationToken, Task<AdvisorEngagementContext>> confirm, CancellationToken ct);
}

public static class AdvisorEngagementPolicy
{
    public const string Capability = "application-case.advisor-engagement.manage";
    public const string InitialStage = "Not started";
    public static readonly string[] Stages =
        [InitialStage, "Preparing", "Documents requested", "Ready for submission"];

    public static string Operation(string value)
    {
        if (value.Length != 32 || value.Any(character => !char.IsAsciiHexDigitLower(character)))
            throw new ApplicationCaseException("OPERATION_ID_INVALID", 400);
        return value;
    }

    public static string Text(string value, string code)
    {
        var text = value.Trim();
        if (text.Length is < 1 or > 600 || text.Any(char.IsControl))
            throw new ApplicationCaseException(code, 400);
        return text;
    }

    public static string[] Sources(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count is < 1 or > 20 ||
            values.Any(value => !TenantAccessClient.Identifier(value, 128)) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ApplicationCaseException("ADVISOR_SOURCE_REFERENCE_INVALID", 400);
        return values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    public static string NextStage(string current, string requested)
    {
        var currentIndex = Array.IndexOf(Stages, current);
        var requestedIndex = Array.IndexOf(Stages, requested);
        if (currentIndex < 0 || requestedIndex != currentIndex + 1)
            throw new ApplicationCaseException("ADVISOR_APPLICATION_STAGE_INVALID", 409);
        return requested;
    }

    public static void ExpectedVersion(int value)
    {
        if (value < 0) throw new ApplicationCaseException("ADVISOR_ENGAGEMENT_VERSION_INVALID", 400);
    }

    public static void RequireCurrent(AdvisorEngagementContext before, AdvisorEngagementContext current)
    {
        if (before != current) throw new ApplicationCaseException("ADVISOR_AUTHORITY_CHANGED", 409);
    }
}

public sealed class AdvisorEngagementApplication(TimeProvider clock) : IAdvisorEngagementApplication, IDisposable
{
    private const string Durability = "Temporary data";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, EngagementRecord> engagements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationRecord> operations = new(StringComparer.Ordinal);

    public async Task<AdvisorEngagementView> ReadAsync(AdvisorEngagementContext context, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return View(context, Find(context)); }
        finally { gate.Release(); }
    }

    public Task<AdvisorEngagementView> AddNoteAsync(
        AdvisorEngagementContext before,
        AddAdvisorNote request,
        Func<CancellationToken, Task<AdvisorEngagementContext>> confirm,
        CancellationToken ct)
    {
        var note = AdvisorEngagementPolicy.Text(request.Note, "ADVISOR_NOTE_INVALID");
        var sources = AdvisorEngagementPolicy.Sources(request.SourceFactIds);
        return MutateAsync(before, request.OperationId, request.ExpectedVersion, "NOTE", note,
            sources, null, confirm, ct);
    }

    public Task<AdvisorEngagementView> RecordCommunicationAsync(
        AdvisorEngagementContext before,
        RecordCustomerCommunication request,
        Func<CancellationToken, Task<AdvisorEngagementContext>> confirm,
        CancellationToken ct) =>
        MutateAsync(before, request.OperationId, request.ExpectedVersion, "COMMUNICATION",
            AdvisorEngagementPolicy.Text(request.Message, "ADVISOR_COMMUNICATION_INVALID"),
            [], null, confirm, ct);

    public Task<AdvisorEngagementView> AdvanceApplicationAsync(
        AdvisorEngagementContext before,
        AdvanceAdvisorApplication request,
        Func<CancellationToken, Task<AdvisorEngagementContext>> confirm,
        CancellationToken ct) =>
        MutateAsync(before, request.OperationId, request.ExpectedVersion, "APPLICATION_STAGE",
            null, [], request.Stage, confirm, ct);

    private async Task<AdvisorEngagementView> MutateAsync(
        AdvisorEngagementContext before,
        string operationId,
        int expectedVersion,
        string action,
        string? body,
        IReadOnlyList<string> sources,
        string? requestedStage,
        Func<CancellationToken, Task<AdvisorEngagementContext>> confirm,
        CancellationToken ct)
    {
        AdvisorEngagementPolicy.Operation(operationId);
        AdvisorEngagementPolicy.ExpectedVersion(expectedVersion);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var operationKey = Key(before) + ":" + operationId;
            if (operations.TryGetValue(operationKey, out var replay))
            {
                if (replay.Action != action || replay.CustomerId != before.CustomerId)
                    throw new ApplicationCaseException("OPERATION_REUSE_CONFLICT", 409);
                return View(before, Find(before));
            }

            var row = Find(before);
            if ((row?.Version ?? 0) != expectedVersion)
                throw new ApplicationCaseException("ADVISOR_ENGAGEMENT_VERSION_CHANGED", 409);
            if ((row?.Activities.Count ?? 0) >= 200)
                throw new ApplicationCaseException("ADVISOR_ENGAGEMENT_LIMIT_REACHED", 409);
            var current = await confirm(ct).ConfigureAwait(false);
            AdvisorEngagementPolicy.RequireCurrent(before, current);

            var now = clock.GetUtcNow();
            var nextVersion = expectedVersion + 1;
            var stage = row?.ApplicationStage ?? AdvisorEngagementPolicy.InitialStage;
            if (action == "APPLICATION_STAGE")
                stage = AdvisorEngagementPolicy.NextStage(stage, requestedStage ?? "");
            var activity = new ActivityRecord(
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), action, body,
                sources.ToArray(), action == "APPLICATION_STAGE" ? stage : null, nextVersion, now);
            var activities = (row?.Activities ?? Array.Empty<ActivityRecord>()).Append(activity).ToArray();
            var updated = new EngagementRecord(before.TenantId, before.ActorId, before.CustomerId,
                nextVersion, stage, now, activities);
            engagements[Key(before)] = updated;
            operations.Add(operationKey, new(before.CustomerId, action));
            return View(before, updated);
        }
        finally { gate.Release(); }
    }

    private EngagementRecord? Find(AdvisorEngagementContext context) =>
        engagements.GetValueOrDefault(Key(context));

    private static AdvisorEngagementView View(AdvisorEngagementContext context, EngagementRecord? row) =>
        new(context.CustomerId, row?.Version ?? 0,
            row?.ApplicationStage ?? AdvisorEngagementPolicy.InitialStage, row?.UpdatedAt,
            row?.Activities.Select(value => new AdvisorActivityView(value.ActivityId, value.Kind,
                value.Body, value.SourceFactIds, value.ApplicationStage, value.Version, value.OccurredAt)).ToArray() ?? [],
            Durability);

    private static string Key(AdvisorEngagementContext context) =>
        context.TenantId + ":" + context.ActorId + ":" + context.CustomerId;

    public void Dispose() => gate.Dispose();

    private sealed record EngagementRecord(string TenantId, string AdvisorActorId, string CustomerId,
        int Version, string ApplicationStage, DateTimeOffset UpdatedAt, IReadOnlyList<ActivityRecord> Activities);
    private sealed record ActivityRecord(string ActivityId, string Kind, string? Body,
        IReadOnlyList<string> SourceFactIds, string? ApplicationStage, int Version, DateTimeOffset OccurredAt);
    private sealed record OperationRecord(string CustomerId, string Action);
}
