using System.Security.Cryptography;
using System.Text;

namespace Monergy.Services.ApplicationCase.Application;

public sealed record LegalAdvisorContext(
    string TenantId,
    string ActorId,
    long PolicyVersion,
    long SubjectVersion);

public sealed record LegalReviewContext(
    string TenantId,
    string ActorId,
    string CustomerId,
    long PolicyVersion,
    long SubjectVersion,
    long CustomerVersion,
    long ConsentVersion);

public sealed record LegalDocumentView(
    string Code,
    string Label,
    string Owner,
    string Reference,
    long AssignedVersion,
    long CurrentVersion,
    bool Inspected,
    DateTimeOffset? InspectedAt);

public sealed record LegalReviewEvidenceSnapshot(
    string Code,
    string Owner,
    string Reference,
    long Version);

public sealed record LegalReviewRevisionView(
    int Revision,
    string Outcome,
    string Findings,
    DateTimeOffset RecordedAt,
    string RecordedBy,
    long PolicyVersion,
    long SubjectVersion,
    long CustomerVersion,
    long ConsentVersion,
    IReadOnlyList<LegalReviewEvidenceSnapshot> Evidence);

public sealed record LegalReviewView(
    string ReviewId,
    string MatterReference,
    string MatterLabel,
    string State,
    int Version,
    DateTimeOffset AssignedAt,
    IReadOnlyList<LegalDocumentView> Evidence,
    IReadOnlyList<LegalReviewRevisionView> Revisions,
    string DataDurability);

public sealed record CustomerLegalReviewView(
    string ReviewId,
    string MatterReference,
    string MatterLabel,
    int Revision,
    string Outcome,
    string Findings,
    DateTimeOffset RecordedAt,
    string RecordedBy,
    int EvidenceCount,
    string DataDurability);

public sealed record InspectLegalDocument(string OperationId, int ExpectedVersion);
public sealed record RecordLegalReview(
    string OperationId,
    int ExpectedVersion,
    string Outcome,
    string Findings);

public interface ILegalReviewApplication
{
    Task<IReadOnlyList<LegalReviewView>> ListAsync(
        LegalAdvisorContext context,
        Func<string, CancellationToken, Task<LegalReviewContext>> authorize,
        CancellationToken ct);
    Task<IReadOnlyList<CustomerLegalReviewView>> ListCustomerResultsAsync(
        CaseActorContext customer,
        CancellationToken ct);
    Task<LegalReviewView> InspectEvidenceAsync(
        string reviewId,
        string evidenceCode,
        LegalAdvisorContext before,
        InspectLegalDocument request,
        Func<string, CancellationToken, Task<LegalReviewContext>> confirm,
        CancellationToken ct);
    Task<LegalReviewView> RecordReviewAsync(
        string reviewId,
        LegalAdvisorContext before,
        RecordLegalReview request,
        Func<string, CancellationToken, Task<LegalReviewContext>> confirm,
        CancellationToken ct);
}

public static class LegalReviewPolicy
{
    public const string Capability = "application-case.legal-review.manage";

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

    public static string Text(string value, string code, int minimum, int maximum)
    {
        var result = value.Trim();
        if (result.Length < minimum || result.Length > maximum || result.Any(char.IsControl))
            throw new ApplicationCaseException(code, 400);
        return result;
    }

    public static string Outcome(string value)
    {
        return value switch
        {
            "NO_ISSUE_IDENTIFIED" or "ACTION_REQUIRED" or "UNABLE_TO_CONCLUDE" => value,
            _ => throw new ApplicationCaseException("LEGAL_REVIEW_OUTCOME_INVALID", 400),
        };
    }

    public static void ExpectedVersion(int value)
    {
        if (value < 1) throw new ApplicationCaseException("LEGAL_REVIEW_VERSION_INVALID", 400);
    }

    public static void RequireCurrent(LegalAdvisorContext before, LegalReviewContext current)
    {
        if (before.TenantId != current.TenantId || before.ActorId != current.ActorId ||
            before.PolicyVersion != current.PolicyVersion || before.SubjectVersion != current.SubjectVersion ||
            current.CustomerVersion < 1 || current.ConsentVersion < 1)
            throw new ApplicationCaseException("LEGAL_ADVISOR_AUTHORITY_CHANGED", 409);
    }
}

public sealed class LegalReviewApplication(TimeProvider clock) : ILegalReviewApplication, IDisposable
{
    private const string Durability = "Temporary data";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ReviewRecord> reviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationRecord> operations = new(StringComparer.Ordinal);

    public void AddFixture(string tenantId, string actorId, string customerId, string reviewId,
        string matterReference, string matterLabel,
        IReadOnlyList<(string Code, string Label, string Owner, string Reference, long Version)> documents,
        DateTimeOffset assignedAt)
    {
        reviews.Add(Key(tenantId, reviewId), new(tenantId, actorId, customerId, reviewId,
            matterReference, matterLabel, "Assigned", 1, assignedAt,
            documents.Select(value => new DocumentRecord(value.Code, value.Label, value.Owner,
                value.Reference, value.Version, value.Version, null)).ToArray(), []));
    }

    public void ChangeEvidenceVersion(string tenantId, string reviewId, string evidenceCode, long version)
    {
        var key = Key(tenantId, reviewId);
        var row = reviews[key];
        var evidence = row.Evidence.ToArray();
        var index = Array.FindIndex(evidence, item => item.Code == evidenceCode);
        if (index < 0) throw new InvalidOperationException("Evidence fixture is missing.");
        evidence[index] = evidence[index] with { CurrentVersion = version };
        reviews[key] = row with { Evidence = evidence };
    }

    public async Task<IReadOnlyList<LegalReviewView>> ListAsync(
        LegalAdvisorContext context,
        Func<string, CancellationToken, Task<LegalReviewContext>> authorize,
        CancellationToken ct)
    {
        ReviewRecord[] assigned;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            assigned = reviews.Values.Where(value => value.TenantId == context.TenantId &&
                value.AssignedActorId == context.ActorId).OrderBy(value => value.AssignedAt)
                .ThenBy(value => value.ReviewId, StringComparer.Ordinal).ToArray();
        }
        finally { gate.Release(); }
        var result = new List<LegalReviewView>(assigned.Length);
        foreach (var row in assigned)
        {
            var authority = await authorize(row.CustomerId, ct).ConfigureAwait(false);
            LegalReviewPolicy.RequireCurrent(context, authority);
            result.Add(View(row));
        }
        return result;
    }

    public async Task<IReadOnlyList<CustomerLegalReviewView>> ListCustomerResultsAsync(
        CaseActorContext customer, CancellationToken ct)
    {
        if (customer.CustomerId is null)
            throw new ApplicationCaseException("CUSTOMER_CASE_ACCESS_REQUIRED", 403);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return reviews.Values.Where(value => value.TenantId == customer.TenantId &&
                value.CustomerId == customer.CustomerId && value.Revisions.Count > 0)
                .OrderBy(value => value.AssignedAt).ThenBy(value => value.ReviewId, StringComparer.Ordinal)
                .Select(CustomerView).ToArray();
        }
        finally { gate.Release(); }
    }

    public Task<LegalReviewView> InspectEvidenceAsync(
        string reviewId, string evidenceCode, LegalAdvisorContext before,
        InspectLegalDocument request,
        Func<string, CancellationToken, Task<LegalReviewContext>> confirm, CancellationToken ct) =>
        MutateAsync(reviewId, before, request.OperationId, request.ExpectedVersion, "EVIDENCE_INSPECTION",
            LegalReviewPolicy.Identifier(evidenceCode, "LEGAL_DOCUMENT_CODE_INVALID", 64),
            null, confirm, ct);

    public Task<LegalReviewView> RecordReviewAsync(
        string reviewId, LegalAdvisorContext before, RecordLegalReview request,
        Func<string, CancellationToken, Task<LegalReviewContext>> confirm, CancellationToken ct)
    {
        LegalReviewPolicy.ExpectedVersion(request.ExpectedVersion);
        var normalized = request with
        {
            Outcome = LegalReviewPolicy.Outcome(request.Outcome),
            Findings = LegalReviewPolicy.Text(request.Findings, "LEGAL_REVIEW_FINDINGS_INVALID", 3, 2000),
        };
        return MutateAsync(reviewId, before, request.OperationId, request.ExpectedVersion,
            "OUTCOME", null, normalized, confirm, ct);
    }

    private async Task<LegalReviewView> MutateAsync(
        string reviewId, LegalAdvisorContext before, string operationId, int expectedVersion,
        string action, string? evidenceCode, RecordLegalReview? request,
        Func<string, CancellationToken, Task<LegalReviewContext>> confirm, CancellationToken ct)
    {
        LegalReviewPolicy.Identifier(reviewId, "LEGAL_REVIEW_ID_INVALID");
        LegalReviewPolicy.Operation(operationId);
        LegalReviewPolicy.ExpectedVersion(expectedVersion);
        var fingerprint = Fingerprint(action, evidenceCode, request);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var operationKey = before.TenantId + ":" + before.ActorId + ":" + operationId;
            if (operations.TryGetValue(operationKey, out var replay))
            {
                if (replay.ReviewId != reviewId || replay.Action != action ||
                    replay.RequestFingerprint != fingerprint)
                    throw new ApplicationCaseException("OPERATION_REUSE_CONFLICT", 409);
                return View(Assigned(before, reviewId));
            }
            var row = Assigned(before, reviewId);
            if (row.Version != expectedVersion)
                throw new ApplicationCaseException("LEGAL_REVIEW_VERSION_CHANGED", 409);
            var authority = await confirm(row.CustomerId, ct).ConfigureAwait(false);
            LegalReviewPolicy.RequireCurrent(before, authority);
            var now = clock.GetUtcNow();
            var evidence = row.Evidence.ToArray();
            var revisions = row.Revisions.ToList();
            if (action == "EVIDENCE_INSPECTION")
            {
                var index = Array.FindIndex(evidence, value => value.Code == evidenceCode);
                if (index < 0) throw new ApplicationCaseException("LEGAL_DOCUMENT_NOT_FOUND", 404);
                if (evidence[index].AssignedVersion != evidence[index].CurrentVersion)
                    throw new ApplicationCaseException("LEGAL_DOCUMENT_VERSION_CHANGED", 409);
                if (evidence[index].InspectedAt is not null)
                    throw new ApplicationCaseException("LEGAL_DOCUMENT_ALREADY_INSPECTED", 409);
                evidence[index] = evidence[index] with { InspectedAt = now };
            }
            else
            {
                if (evidence.Any(value => value.InspectedAt is null))
                    throw new ApplicationCaseException("LEGAL_DOCUMENT_INSPECTION_INCOMPLETE", 409);
                if (evidence.Any(value => value.AssignedVersion != value.CurrentVersion))
                    throw new ApplicationCaseException("LEGAL_DOCUMENT_VERSION_CHANGED", 409);
                revisions.Add(new(revisions.Count + 1, request!.Outcome, request.Findings, now, before.ActorId,
                    authority.PolicyVersion, authority.SubjectVersion, authority.CustomerVersion,
                    authority.ConsentVersion, evidence.Select(value => new LegalReviewEvidenceSnapshot(
                        value.Code, value.Owner, value.Reference, value.CurrentVersion)).ToArray()));
            }
            var updated = row with
            {
                State = action == "OUTCOME" ? "Reviewed" : "Inspection in progress",
                Version = row.Version + 1,
                Evidence = evidence,
                Revisions = revisions,
            };
            reviews[Key(before.TenantId, reviewId)] = updated;
            operations.Add(operationKey, new(reviewId, action, fingerprint));
            return View(updated);
        }
        finally { gate.Release(); }
    }

    private ReviewRecord Assigned(LegalAdvisorContext context, string reviewId)
    {
        if (!reviews.TryGetValue(Key(context.TenantId, reviewId), out var row) ||
            row.AssignedActorId != context.ActorId)
            throw new ApplicationCaseException("LEGAL_REVIEW_NOT_FOUND", 404);
        return row;
    }

    private static LegalReviewView View(ReviewRecord row) => new(row.ReviewId,
        row.MatterReference, row.MatterLabel, row.State, row.Version, row.AssignedAt,
        row.Evidence.Select(value => new LegalDocumentView(value.Code, value.Label, value.Owner,
            value.Reference, value.AssignedVersion, value.CurrentVersion, value.InspectedAt is not null,
            value.InspectedAt)).ToArray(), row.Revisions, Durability);

    private static CustomerLegalReviewView CustomerView(ReviewRecord row)
    {
        var latest = row.Revisions[^1];
        return new(row.ReviewId, row.MatterReference, row.MatterLabel, latest.Revision,
            latest.Outcome, latest.Findings,
            latest.RecordedAt, latest.RecordedBy, latest.Evidence.Count, Durability);
    }

    private static string Key(string tenantId, string reviewId) => tenantId + ":" + reviewId;
    internal static string Fingerprint(string action, string? evidenceCode, RecordLegalReview? request)
    {
        var canonical = request is null
            ? action + "\n" + evidenceCode
            : string.Join('\n', action, request.Outcome, request.Findings);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public void Dispose() => gate.Dispose();

    private sealed record DocumentRecord(string Code, string Label, string Owner, string Reference,
        long AssignedVersion, long CurrentVersion, DateTimeOffset? InspectedAt);
    private sealed record ReviewRecord(string TenantId, string AssignedActorId, string CustomerId,
        string ReviewId, string MatterReference, string MatterLabel, string State, int Version,
        DateTimeOffset AssignedAt, IReadOnlyList<DocumentRecord> Evidence,
        List<LegalReviewRevisionView> Revisions);
    private sealed record OperationRecord(string ReviewId, string Action, string RequestFingerprint);
}
