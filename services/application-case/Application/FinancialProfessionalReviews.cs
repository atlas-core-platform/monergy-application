namespace Monergy.Services.ApplicationCase.Application;

public sealed record FinancialProfessionalContext(
    string TenantId,
    string ActorId,
    long PolicyVersion,
    long SubjectVersion);

public sealed record FinancialProfessionalReviewContext(
    string TenantId,
    string ActorId,
    string CustomerId,
    long PolicyVersion,
    long SubjectVersion,
    long CustomerVersion,
    long ConsentVersion);

public sealed record FinancialReviewSourceView(
    string Code,
    string Label,
    string Owner,
    string Reference,
    long AssignedVersion,
    long CurrentVersion,
    bool Reviewed,
    DateTimeOffset? ReviewedAt);

public sealed record FinancialProfessionalNoteView(
    string Note,
    IReadOnlyList<string> SourceCodes,
    DateTimeOffset RecordedAt,
    string RecordedBy);

public sealed record FinancialCertificationSourceView(
    string Code,
    string Owner,
    string Reference,
    long Version);

public sealed record FinancialCertificationView(
    string Outcome,
    string Rationale,
    DateTimeOffset CertifiedAt,
    string CertifiedBy,
    long PolicyVersion,
    long SubjectVersion,
    long CustomerVersion,
    long ConsentVersion,
    IReadOnlyList<FinancialCertificationSourceView> Sources);

public sealed record FinancialProfessionalReviewView(
    string ReviewId,
    string ReviewReference,
    string State,
    int Version,
    DateTimeOffset AssignedAt,
    IReadOnlyList<FinancialReviewSourceView> Sources,
    IReadOnlyList<FinancialProfessionalNoteView> Notes,
    FinancialCertificationView? Certification,
    string DataDurability);

public sealed record ReviewFinancialSource(string OperationId, int ExpectedVersion);
public sealed record AddFinancialProfessionalNote(
    string OperationId,
    int ExpectedVersion,
    string Note,
    string[] SourceCodes);
public sealed record RecordFinancialCertification(
    string OperationId,
    int ExpectedVersion,
    string Outcome,
    string Rationale);

public interface IFinancialProfessionalReview
{
    Task<IReadOnlyList<FinancialProfessionalReviewView>> ListAsync(
        FinancialProfessionalContext context,
        Func<string, CancellationToken, Task<FinancialProfessionalReviewContext>> authorize,
        CancellationToken ct);
    Task<FinancialProfessionalReviewView> ReviewSourceAsync(
        string reviewId,
        string sourceCode,
        FinancialProfessionalContext before,
        ReviewFinancialSource request,
        Func<string, CancellationToken, Task<FinancialProfessionalReviewContext>> confirm,
        CancellationToken ct);
    Task<FinancialProfessionalReviewView> AddNoteAsync(
        string reviewId,
        FinancialProfessionalContext before,
        AddFinancialProfessionalNote request,
        Func<string, CancellationToken, Task<FinancialProfessionalReviewContext>> confirm,
        CancellationToken ct);
    Task<FinancialProfessionalReviewView> CertifyAsync(
        string reviewId,
        FinancialProfessionalContext before,
        RecordFinancialCertification request,
        Func<string, CancellationToken, Task<FinancialProfessionalReviewContext>> confirm,
        CancellationToken ct);
}

public static class FinancialProfessionalReviewPolicy
{
    public const string Capability = "application-case.financial-review.certify";
    public static readonly string[] Outcomes =
        ["Certified with no exceptions", "Certified with exceptions", "Unable to certify"];

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
        if (value < 1) throw new ApplicationCaseException("FINANCIAL_REVIEW_VERSION_INVALID", 400);
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
        if (!Outcomes.Contains(value, StringComparer.Ordinal))
            throw new ApplicationCaseException("FINANCIAL_CERTIFICATION_OUTCOME_INVALID", 400);
        return value;
    }

    public static string[] Sources(string[] values)
    {
        if (values is not { Length: > 0 and <= 20 })
            throw new ApplicationCaseException("FINANCIAL_NOTE_SOURCES_INVALID", 400);
        var result = values.Select(value => Identifier(value, "FINANCIAL_SOURCE_CODE_INVALID", 64))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (result.Length != values.Length)
            throw new ApplicationCaseException("FINANCIAL_NOTE_SOURCES_INVALID", 400);
        return result;
    }

    public static void RequireCurrent(FinancialProfessionalContext before, FinancialProfessionalReviewContext current)
    {
        if (before.TenantId != current.TenantId || before.ActorId != current.ActorId ||
            before.PolicyVersion != current.PolicyVersion || before.SubjectVersion != current.SubjectVersion ||
            current.CustomerVersion < 1 || current.ConsentVersion < 1)
            throw new ApplicationCaseException("FINANCIAL_PROFESSIONAL_AUTHORITY_CHANGED", 409);
    }
}

public sealed class FinancialProfessionalReview(TimeProvider clock) : IFinancialProfessionalReview, IDisposable
{
    private const string Durability = "Temporary data";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ReviewRecord> reviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationRecord> operations = new(StringComparer.Ordinal);

    public void AddFixture(string tenantId, string actorId, string customerId, string reviewId,
        string reference, IReadOnlyList<(string Code, string Label, string Owner, string Reference, long Version)> sources,
        DateTimeOffset assignedAt)
    {
        reviews.Add(Key(tenantId, reviewId), new(tenantId, actorId, customerId, reviewId, reference,
            "Assigned", 1, assignedAt, sources.Select(value => new SourceRecord(value.Code, value.Label,
                value.Owner, value.Reference, value.Version, value.Version, null)).ToArray(), [], null));
    }

    public async Task<IReadOnlyList<FinancialProfessionalReviewView>> ListAsync(
        FinancialProfessionalContext context,
        Func<string, CancellationToken, Task<FinancialProfessionalReviewContext>> authorize,
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
        var result = new List<FinancialProfessionalReviewView>(assigned.Length);
        foreach (var row in assigned)
        {
            var authority = await authorize(row.CustomerId, ct).ConfigureAwait(false);
            FinancialProfessionalReviewPolicy.RequireCurrent(context, authority);
            result.Add(View(row));
        }
        return result;
    }

    public Task<FinancialProfessionalReviewView> ReviewSourceAsync(
        string reviewId, string sourceCode, FinancialProfessionalContext before, ReviewFinancialSource request,
        Func<string, CancellationToken, Task<FinancialProfessionalReviewContext>> confirm, CancellationToken ct) =>
        MutateAsync(reviewId, before, request.OperationId, request.ExpectedVersion, "SOURCE_REVIEW",
            FinancialProfessionalReviewPolicy.Identifier(sourceCode, "FINANCIAL_SOURCE_CODE_INVALID", 64),
            null, null, null, confirm, ct);

    public Task<FinancialProfessionalReviewView> AddNoteAsync(
        string reviewId, FinancialProfessionalContext before, AddFinancialProfessionalNote request,
        Func<string, CancellationToken, Task<FinancialProfessionalReviewContext>> confirm, CancellationToken ct) =>
        MutateAsync(reviewId, before, request.OperationId, request.ExpectedVersion, "NOTE", null,
            FinancialProfessionalReviewPolicy.Text(request.Note, "FINANCIAL_NOTE_INVALID", 3, 2000),
            FinancialProfessionalReviewPolicy.Sources(request.SourceCodes), null, confirm, ct);

    public Task<FinancialProfessionalReviewView> CertifyAsync(
        string reviewId, FinancialProfessionalContext before, RecordFinancialCertification request,
        Func<string, CancellationToken, Task<FinancialProfessionalReviewContext>> confirm, CancellationToken ct) =>
        MutateAsync(reviewId, before, request.OperationId, request.ExpectedVersion, "CERTIFICATION", null,
            FinancialProfessionalReviewPolicy.Text(request.Rationale, "FINANCIAL_CERTIFICATION_RATIONALE_INVALID", 3, 2000),
            null, FinancialProfessionalReviewPolicy.Outcome(request.Outcome), confirm, ct);

    private async Task<FinancialProfessionalReviewView> MutateAsync(
        string reviewId, FinancialProfessionalContext before, string operationId, int expectedVersion,
        string action, string? sourceCode, string? text, string[]? sourceCodes, string? outcome,
        Func<string, CancellationToken, Task<FinancialProfessionalReviewContext>> confirm, CancellationToken ct)
    {
        FinancialProfessionalReviewPolicy.Identifier(reviewId, "FINANCIAL_REVIEW_ID_INVALID");
        FinancialProfessionalReviewPolicy.Operation(operationId);
        FinancialProfessionalReviewPolicy.ExpectedVersion(expectedVersion);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var operationKey = before.TenantId + ":" + before.ActorId + ":" + operationId;
            if (operations.TryGetValue(operationKey, out var replay))
            {
                if (replay.ReviewId != reviewId || replay.Action != action)
                    throw new ApplicationCaseException("OPERATION_REUSE_CONFLICT", 409);
                return View(Assigned(before, reviewId));
            }
            var row = Assigned(before, reviewId);
            if (row.Version != expectedVersion)
                throw new ApplicationCaseException("FINANCIAL_REVIEW_VERSION_CHANGED", 409);
            if (row.Certification is not null)
                throw new ApplicationCaseException("FINANCIAL_REVIEW_ALREADY_CERTIFIED", 409);
            var authority = await confirm(row.CustomerId, ct).ConfigureAwait(false);
            FinancialProfessionalReviewPolicy.RequireCurrent(before, authority);
            var now = clock.GetUtcNow();
            var sources = row.Sources.ToArray();
            var notes = row.Notes.ToList();
            CertificationRecord? certification = null;
            if (action == "SOURCE_REVIEW")
            {
                var index = Array.FindIndex(sources, value => value.Code == sourceCode);
                if (index < 0) throw new ApplicationCaseException("FINANCIAL_SOURCE_NOT_FOUND", 404);
                if (sources[index].AssignedVersion != sources[index].CurrentVersion)
                    throw new ApplicationCaseException("FINANCIAL_SOURCE_VERSION_CHANGED", 409);
                if (sources[index].ReviewedAt is not null)
                    throw new ApplicationCaseException("FINANCIAL_SOURCE_ALREADY_REVIEWED", 409);
                sources[index] = sources[index] with { ReviewedAt = now };
            }
            else if (action == "NOTE")
            {
                if (sourceCodes!.Any(code => !sources.Any(source => source.Code == code &&
                    source.AssignedVersion == source.CurrentVersion)))
                    throw new ApplicationCaseException("FINANCIAL_NOTE_SOURCE_CHANGED", 409);
                notes.Add(new(text!, sourceCodes!, now, before.ActorId));
            }
            else
            {
                if (sources.Any(source => source.ReviewedAt is null))
                    throw new ApplicationCaseException("FINANCIAL_SOURCE_REVIEW_INCOMPLETE", 409);
                if (sources.Any(source => source.AssignedVersion != source.CurrentVersion))
                    throw new ApplicationCaseException("FINANCIAL_SOURCE_VERSION_CHANGED", 409);
                certification = new(outcome!, text!, now, before.ActorId, authority.PolicyVersion,
                    authority.SubjectVersion, authority.CustomerVersion, authority.ConsentVersion,
                    sources.Select(source => new FinancialCertificationSourceView(source.Code, source.Owner,
                        source.Reference, source.CurrentVersion)).ToArray());
            }
            var updated = row with
            {
                State = certification is null ? "Under review" : "Certified",
                Version = row.Version + 1,
                Sources = sources,
                Notes = notes,
                Certification = certification,
            };
            reviews[Key(before.TenantId, reviewId)] = updated;
            operations.Add(operationKey, new(reviewId, action));
            return View(updated);
        }
        finally { gate.Release(); }
    }

    private ReviewRecord Assigned(FinancialProfessionalContext context, string reviewId)
    {
        if (!reviews.TryGetValue(Key(context.TenantId, reviewId), out var row) ||
            row.AssignedActorId != context.ActorId)
            throw new ApplicationCaseException("FINANCIAL_REVIEW_NOT_FOUND", 404);
        return row;
    }

    private static FinancialProfessionalReviewView View(ReviewRecord row) => new(row.ReviewId,
        row.ReviewReference, row.State, row.Version, row.AssignedAt,
        row.Sources.Select(source => new FinancialReviewSourceView(source.Code, source.Label, source.Owner,
            source.Reference, source.AssignedVersion, source.CurrentVersion, source.ReviewedAt is not null,
            source.ReviewedAt)).ToArray(), row.Notes,
        row.Certification is null ? null : new(row.Certification.Outcome, row.Certification.Rationale,
            row.Certification.CertifiedAt, row.Certification.CertifiedBy, row.Certification.PolicyVersion,
            row.Certification.SubjectVersion, row.Certification.CustomerVersion, row.Certification.ConsentVersion,
            row.Certification.Sources), Durability);

    private static string Key(string tenantId, string reviewId) => tenantId + ":" + reviewId;
    public void Dispose() => gate.Dispose();

    private sealed record ReviewRecord(string TenantId, string AssignedActorId, string CustomerId,
        string ReviewId, string ReviewReference, string State, int Version, DateTimeOffset AssignedAt,
        IReadOnlyList<SourceRecord> Sources, IReadOnlyList<FinancialProfessionalNoteView> Notes,
        CertificationRecord? Certification);
    private sealed record SourceRecord(string Code, string Label, string Owner, string Reference,
        long AssignedVersion, long CurrentVersion, DateTimeOffset? ReviewedAt);
    private sealed record CertificationRecord(string Outcome, string Rationale, DateTimeOffset CertifiedAt,
        string CertifiedBy, long PolicyVersion, long SubjectVersion, long CustomerVersion, long ConsentVersion,
        IReadOnlyList<FinancialCertificationSourceView> Sources);
    private sealed record OperationRecord(string ReviewId, string Action);
}
