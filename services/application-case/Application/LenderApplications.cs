using System.Security.Cryptography;

namespace Monergy.Services.ApplicationCase.Application;

public sealed record LenderContext(
    string TenantId,
    string ActorId,
    long PolicyVersion,
    long SubjectVersion);

public sealed record LenderDocumentView(
    string Code,
    string Label,
    bool Present,
    bool Reviewed,
    DateTimeOffset? ReviewedAt);

public sealed record LenderDecisionView(
    string Decision,
    string Reason,
    DateTimeOffset DecidedAt);

public sealed record LenderApplicationView(
    string ApplicationId,
    string ApplicationReference,
    string State,
    int Version,
    DateTimeOffset AssignedAt,
    IReadOnlyList<LenderDocumentView> RequiredDocuments,
    LenderDecisionView? Decision,
    string DataDurability);

public sealed record ReviewLenderDocument(string OperationId, int ExpectedVersion);
public sealed record RecordLenderDecision(
    string OperationId,
    int ExpectedVersion,
    string Decision,
    string Reason);

public interface ILenderApplication
{
    Task<IReadOnlyList<LenderApplicationView>> ListAsync(LenderContext context, CancellationToken ct);
    Task<LenderApplicationView> ReviewDocumentAsync(
        string applicationId,
        string documentCode,
        LenderContext before,
        ReviewLenderDocument request,
        Func<CancellationToken, Task<LenderContext>> confirm,
        CancellationToken ct);
    Task<LenderApplicationView> RecordDecisionAsync(
        string applicationId,
        LenderContext before,
        RecordLenderDecision request,
        Func<CancellationToken, Task<LenderContext>> confirm,
        CancellationToken ct);
}

public static class LenderApplicationPolicy
{
    public const string Capability = "application-case.lender-application.review";
    public static readonly string[] Decisions = ["Approved", "Declined", "More information required"];

    public static string Identifier(string value, string code, int maximum = 128)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
            throw new ApplicationCaseException(code, 400);
        return value;
    }

    public static string Operation(string value)
    {
        if (value.Length != 32 || value.Any(character => !char.IsAsciiHexDigitLower(character)))
            throw new ApplicationCaseException("OPERATION_ID_INVALID", 400);
        return value;
    }

    public static void ExpectedVersion(int value)
    {
        if (value < 1) throw new ApplicationCaseException("LENDER_APPLICATION_VERSION_INVALID", 400);
    }

    public static string Decision(string value)
    {
        if (!Decisions.Contains(value, StringComparer.Ordinal))
            throw new ApplicationCaseException("LENDER_DECISION_INVALID", 400);
        return value;
    }

    public static string Reason(string value)
    {
        var reason = value.Trim();
        if (reason.Length is < 3 or > 600 || reason.Any(char.IsControl))
            throw new ApplicationCaseException("LENDER_DECISION_REASON_INVALID", 400);
        return reason;
    }

    public static void RequireCurrent(LenderContext before, LenderContext current)
    {
        if (before != current) throw new ApplicationCaseException("LENDER_AUTHORITY_CHANGED", 409);
    }
}

public sealed class LenderApplication(TimeProvider clock) : ILenderApplication, IDisposable
{
    private const string Durability = "Temporary data";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ApplicationRecord> applications = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationRecord> operations = new(StringComparer.Ordinal);

    public void AddFixture(string tenantId, string actorId, string applicationId, string reference,
        IReadOnlyList<(string Code, string Label, bool Present)> documents, DateTimeOffset assignedAt)
    {
        var row = new ApplicationRecord(tenantId, actorId, applicationId, reference, "Assigned", 1,
            assignedAt, documents.Select(value => new DocumentRecord(value.Code, value.Label, value.Present, null)).ToArray(), null);
        applications.Add(Key(tenantId, applicationId), row);
    }

    public async Task<IReadOnlyList<LenderApplicationView>> ListAsync(LenderContext context, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return applications.Values
                .Where(value => value.TenantId == context.TenantId && value.AssignedLenderActorId == context.ActorId)
                .OrderBy(value => value.AssignedAt).ThenBy(value => value.ApplicationId, StringComparer.Ordinal)
                .Select(View).ToArray();
        }
        finally { gate.Release(); }
    }

    public Task<LenderApplicationView> ReviewDocumentAsync(
        string applicationId, string documentCode, LenderContext before, ReviewLenderDocument request,
        Func<CancellationToken, Task<LenderContext>> confirm, CancellationToken ct) =>
        MutateAsync(applicationId, before, request.OperationId, request.ExpectedVersion,
            "DOCUMENT_REVIEW", documentCode, null, null, confirm, ct);

    public Task<LenderApplicationView> RecordDecisionAsync(
        string applicationId, LenderContext before, RecordLenderDecision request,
        Func<CancellationToken, Task<LenderContext>> confirm, CancellationToken ct) =>
        MutateAsync(applicationId, before, request.OperationId, request.ExpectedVersion,
            "DECISION", null, LenderApplicationPolicy.Decision(request.Decision),
            LenderApplicationPolicy.Reason(request.Reason), confirm, ct);

    private async Task<LenderApplicationView> MutateAsync(
        string applicationId, LenderContext before, string operationId, int expectedVersion,
        string action, string? documentCode, string? decision, string? reason,
        Func<CancellationToken, Task<LenderContext>> confirm, CancellationToken ct)
    {
        LenderApplicationPolicy.Identifier(applicationId, "LENDER_APPLICATION_ID_INVALID");
        LenderApplicationPolicy.Operation(operationId);
        LenderApplicationPolicy.ExpectedVersion(expectedVersion);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = Key(before.TenantId, applicationId);
            var operationKey = before.TenantId + ":" + before.ActorId + ":" + operationId;
            if (operations.TryGetValue(operationKey, out var replay))
            {
                if (replay.ApplicationId != applicationId || replay.Action != action)
                    throw new ApplicationCaseException("OPERATION_REUSE_CONFLICT", 409);
                return View(Assigned(key, before.ActorId));
            }
            var row = Assigned(key, before.ActorId);
            if (row.Version != expectedVersion)
                throw new ApplicationCaseException("LENDER_APPLICATION_VERSION_CHANGED", 409);
            if (row.Decision is not null)
                throw new ApplicationCaseException("LENDER_APPLICATION_ALREADY_DECIDED", 409);
            LenderApplicationPolicy.RequireCurrent(before, await confirm(ct).ConfigureAwait(false));
            var now = clock.GetUtcNow();
            var documents = row.Documents.ToArray();
            DecisionRecord? recordedDecision = null;
            if (action == "DOCUMENT_REVIEW")
            {
                var index = Array.FindIndex(documents, value => value.Code == documentCode);
                if (index < 0 || !documents[index].Present)
                    throw new ApplicationCaseException("LENDER_DOCUMENT_UNAVAILABLE", 409);
                if (documents[index].ReviewedAt is not null)
                    throw new ApplicationCaseException("LENDER_DOCUMENT_ALREADY_REVIEWED", 409);
                documents[index] = documents[index] with { ReviewedAt = now };
            }
            else
            {
                if (documents.Any(value => !value.Present || value.ReviewedAt is null))
                    throw new ApplicationCaseException("LENDER_DOCUMENT_REVIEW_INCOMPLETE", 409);
                recordedDecision = new(decision!, reason!, now);
            }
            var updated = row with
            {
                State = recordedDecision is null ? "Under review" : "Decided",
                Version = row.Version + 1,
                Documents = documents,
                Decision = recordedDecision,
            };
            applications[key] = updated;
            operations.Add(operationKey, new(applicationId, action));
            return View(updated);
        }
        finally { gate.Release(); }
    }

    private ApplicationRecord Assigned(string key, string actorId)
    {
        if (!applications.TryGetValue(key, out var row) || row.AssignedLenderActorId != actorId)
            throw new ApplicationCaseException("LENDER_APPLICATION_NOT_FOUND", 404);
        return row;
    }

    private static LenderApplicationView View(ApplicationRecord row) => new(
        row.ApplicationId, row.ApplicationReference, row.State, row.Version, row.AssignedAt,
        row.Documents.Select(value => new LenderDocumentView(value.Code, value.Label, value.Present,
            value.ReviewedAt is not null, value.ReviewedAt)).ToArray(),
        row.Decision is null ? null : new(row.Decision.Decision, row.Decision.Reason, row.Decision.DecidedAt),
        Durability);

    private static string Key(string tenantId, string applicationId) => tenantId + ":" + applicationId;
    public void Dispose() => gate.Dispose();

    private sealed record ApplicationRecord(string TenantId, string AssignedLenderActorId,
        string ApplicationId, string ApplicationReference, string State, int Version,
        DateTimeOffset AssignedAt, IReadOnlyList<DocumentRecord> Documents, DecisionRecord? Decision);
    private sealed record DocumentRecord(string Code, string Label, bool Present, DateTimeOffset? ReviewedAt);
    private sealed record DecisionRecord(string Decision, string Reason, DateTimeOffset DecidedAt);
    private sealed record OperationRecord(string ApplicationId, string Action);
}
