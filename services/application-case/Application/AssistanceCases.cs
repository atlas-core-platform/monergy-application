using System.Security.Cryptography;
using Monergy.Platform;

namespace Monergy.Services.ApplicationCase.Application;

public sealed class ApplicationCaseException(string code, int status) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed record CaseActorContext(
    string TenantId,
    string ActorId,
    string? CustomerId,
    long PolicyVersion,
    long SubjectVersion,
    string CapabilityId);

public sealed record AssistanceCaseView(
    string CaseId,
    string RequestType,
    string State,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AcceptedAt,
    bool AssignedToMe,
    string DataDurability);

public sealed record AssistanceCaseEventView(
    string EventId,
    string Action,
    int Version,
    DateTimeOffset OccurredAt);

public sealed record AssistanceCaseHistory(
    string CaseId,
    IReadOnlyList<AssistanceCaseEventView> Events,
    string DataDurability);

public sealed record CreateAssistanceCase(string OperationId);
public sealed record AcceptAssistanceCase(string OperationId, int ExpectedVersion);

public sealed class AssistanceCaseApplication(TimeProvider clock) : IDisposable
{
    public const string CustomerCapability = "application-case.case.create";
    public const string DsaCapability = "application-case.case.accept";
    private const string RequestType = "Financial assistance";
    private const string Durability = "Temporary data";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, CaseRecord> cases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationReceipt> operations = new(StringComparer.Ordinal);
    private readonly List<CaseEvent> events = [];

    public async Task<AssistanceCaseView> CreateAsync(
        CaseActorContext before,
        CreateAssistanceCase request,
        Func<CancellationToken, Task<CaseActorContext>> confirm,
        CancellationToken ct)
    {
        RequireCustomer(before);
        Operation(request.OperationId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var operationKey = Key(before.TenantId, before.ActorId, request.OperationId);
            if (operations.TryGetValue(operationKey, out var replay))
            {
                if (replay.Action != "CREATE")
                    throw new ApplicationCaseException("OPERATION_REUSE_CONFLICT", 409);
                return View(RequireCase(before.TenantId, replay.CaseId), before.ActorId);
            }

            var current = await confirm(ct).ConfigureAwait(false);
            RequireUnchanged(before, current);
            var existing = cases.Values.SingleOrDefault(value =>
                value.TenantId == before.TenantId && value.CustomerId == before.CustomerId &&
                value.State is "Submitted" or "Accepted");
            if (existing is not null)
            {
                operations.Add(operationKey, new(existing.CaseId, "CREATE"));
                return View(existing, before.ActorId);
            }

            var now = clock.GetUtcNow();
            var id = "case-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
            var created = new CaseRecord(id, before.TenantId, before.CustomerId!, before.ActorId,
                RequestType, "Submitted", 1, now, null, null);
            cases.Add(id, created);
            operations.Add(operationKey, new(id, "CREATE"));
            events.Add(new(EventId(), id, before.TenantId, "Submitted", 1, now));
            return View(created, before.ActorId);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<AssistanceCaseView>> ListCustomerAsync(
        CaseActorContext context,
        CancellationToken ct)
    {
        RequireCustomer(context);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return cases.Values.Where(value => value.TenantId == context.TenantId &&
                    value.CustomerId == context.CustomerId && value.CustomerActorId == context.ActorId)
                .OrderByDescending(value => value.CreatedAt)
                .Select(value => View(value, context.ActorId)).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<AssistanceCaseView>> ListDsaAsync(
        CaseActorContext context,
        CancellationToken ct)
    {
        RequireDsa(context);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return cases.Values.Where(value => value.TenantId == context.TenantId &&
                    (value.State == "Submitted" || value.AcceptedBy == context.ActorId))
                .OrderBy(value => value.State == "Submitted" ? 0 : 1)
                .ThenBy(value => value.CreatedAt)
                .Select(value => View(value, context.ActorId)).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<AssistanceCaseView> AcceptAsync(
        string caseId,
        CaseActorContext before,
        AcceptAssistanceCase request,
        Func<CancellationToken, Task<CaseActorContext>> confirm,
        CancellationToken ct)
    {
        RequireDsa(before);
        Identifier(caseId, 64, "CASE_ID_INVALID");
        Operation(request.OperationId);
        if (request.ExpectedVersion < 1) throw new ApplicationCaseException("CASE_VERSION_INVALID", 400);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var operationKey = Key(before.TenantId, before.ActorId, request.OperationId);
            if (operations.TryGetValue(operationKey, out var replay))
            {
                if (replay.Action != "ACCEPT" || replay.CaseId != caseId)
                    throw new ApplicationCaseException("OPERATION_REUSE_CONFLICT", 409);
                return View(RequireCase(before.TenantId, caseId), before.ActorId);
            }

            var row = RequireCase(before.TenantId, caseId);
            if (row.State != "Submitted") throw new ApplicationCaseException("CASE_ALREADY_ACCEPTED", 409);
            if (row.Version != request.ExpectedVersion) throw new ApplicationCaseException("CASE_VERSION_CHANGED", 409);
            var current = await confirm(ct).ConfigureAwait(false);
            RequireUnchanged(before, current);
            var now = clock.GetUtcNow();
            var accepted = row with
            {
                State = "Accepted",
                Version = row.Version + 1,
                AcceptedAt = now,
                AcceptedBy = before.ActorId,
            };
            cases[caseId] = accepted;
            operations.Add(operationKey, new(caseId, "ACCEPT"));
            events.Add(new(EventId(), caseId, before.TenantId, "Accepted", accepted.Version, now));
            return View(accepted, before.ActorId);
        }
        finally { gate.Release(); }
    }

    public async Task<AssistanceCaseHistory> HistoryAsync(
        string caseId,
        CaseActorContext context,
        CancellationToken ct)
    {
        RequireCustomer(context);
        Identifier(caseId, 64, "CASE_ID_INVALID");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var row = RequireCase(context.TenantId, caseId);
            if (row.CustomerId != context.CustomerId || row.CustomerActorId != context.ActorId)
                throw new ApplicationCaseException("CASE_NOT_FOUND", 404);
            return new(caseId, events.Where(value => value.TenantId == context.TenantId && value.CaseId == caseId)
                .OrderBy(value => value.Version)
                .Select(value => new AssistanceCaseEventView(value.EventId, value.Action, value.Version, value.OccurredAt))
                .ToArray(), Durability);
        }
        finally { gate.Release(); }
    }

    private CaseRecord RequireCase(string tenant, string caseId)
    {
        if (!cases.TryGetValue(caseId, out var value) || value.TenantId != tenant)
            throw new ApplicationCaseException("CASE_NOT_FOUND", 404);
        return value;
    }

    private static AssistanceCaseView View(CaseRecord value, string actor) =>
        new(value.CaseId, value.RequestType, value.State, value.Version, value.CreatedAt,
            value.AcceptedAt, value.AcceptedBy == actor, Durability);

    private static void RequireCustomer(CaseActorContext context)
    {
        if (context.CapabilityId != CustomerCapability || context.CustomerId is null)
            throw new ApplicationCaseException("CUSTOMER_CASE_ACCESS_REQUIRED", 403);
        Identifier(context.CustomerId, 128, "CUSTOMER_CONTEXT_INVALID");
    }

    private static void RequireDsa(CaseActorContext context)
    {
        if (context.CapabilityId != DsaCapability || context.CustomerId is not null)
            throw new ApplicationCaseException("DSA_CASE_ACCESS_REQUIRED", 403);
    }

    private static void RequireUnchanged(CaseActorContext before, CaseActorContext current)
    {
        if (before != current) throw new ApplicationCaseException("CASE_AUTHORITY_CHANGED", 409);
    }

    private static void Operation(string value)
    {
        if (value.Length != 32 || value.Any(character => !char.IsAsciiHexDigitLower(character)))
            throw new ApplicationCaseException("OPERATION_ID_INVALID", 400);
    }

    private static void Identifier(string value, int maximum, string code)
    {
        if (!TenantAccessClient.Identifier(value, maximum)) throw new ApplicationCaseException(code, 400);
    }

    private static string Key(string tenant, string actor, string operation) => tenant + ":" + actor + ":" + operation;
    private static string EventId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public void Dispose() => gate.Dispose();

    private sealed record CaseRecord(string CaseId, string TenantId, string CustomerId,
        string CustomerActorId, string RequestType, string State, int Version,
        DateTimeOffset CreatedAt, DateTimeOffset? AcceptedAt, string? AcceptedBy);
    private sealed record OperationReceipt(string CaseId, string Action);
    private sealed record CaseEvent(string EventId, string CaseId, string TenantId,
        string Action, int Version, DateTimeOffset OccurredAt);
}
