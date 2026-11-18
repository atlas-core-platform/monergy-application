using Dapper;
using Monergy.Platform;
using Monergy.Services.ApplicationCase.Application;
using Npgsql;

namespace Monergy.Services.ApplicationCase.Infrastructure;

public sealed class PostgresFinancialProfessionalReview : IFinancialProfessionalReview, IAsyncDisposable
{
    private const string Durability = "Durable test data";
    private readonly TimeProvider clock;
    private readonly ProtectedTenantDatabaseConnections connections;

    public PostgresFinancialProfessionalReview(IConfiguration configuration, TimeProvider clock)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        this.clock = clock;
        connections = new(configuration, "application-case",
            "Monergy:Persistence:ApplicationCase:RuntimeConnections");
    }

    public async Task<IReadOnlyList<FinancialProfessionalReviewView>> ListAsync(
        FinancialProfessionalContext context,
        Func<string, CancellationToken, Task<FinancialProfessionalReviewContext>> authorize,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(context.TenantId, ct).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<ReviewRow>(Command("""
            SELECT review_id AS ReviewId,review_reference AS ReviewReference,customer_id AS CustomerId,
                state AS State,version AS Version,assigned_at AS AssignedAt,
                certification_outcome AS CertificationOutcome,certification_rationale AS CertificationRationale,
                certified_at AS CertifiedAt,certified_by AS CertifiedBy,
                certified_policy_version AS CertifiedPolicyVersion,
                certified_subject_version AS CertifiedSubjectVersion,
                certified_customer_version AS CertifiedCustomerVersion,
                certified_consent_version AS CertifiedConsentVersion
            FROM application_case.financial_professional_reviews
            WHERE tenant_id=@Tenant AND assigned_professional_actor_id=@Actor
            ORDER BY assigned_at,review_id;
            """, new { Tenant = context.TenantId, Actor = context.ActorId }, null, ct)).ConfigureAwait(false)).ToArray();
        var result = new List<FinancialProfessionalReviewView>(rows.Length);
        foreach (var row in rows)
        {
            var authority = await authorize(row.CustomerId, ct).ConfigureAwait(false);
            FinancialProfessionalReviewPolicy.RequireCurrent(context, authority);
            result.Add(await ViewAsync(connection, null, context.TenantId, row, ct).ConfigureAwait(false));
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
        await using var connection = await OpenAsync(before.TenantId, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(Command("SELECT pg_advisory_xact_lock(hashtextextended(@Value,0));",
            new { Value = "financial-review-operation:" + before.TenantId + ":" + before.ActorId + ":" + operationId },
            transaction, ct)).ConfigureAwait(false);
        var replay = await connection.QuerySingleOrDefaultAsync<OperationRow>(Command("""
            SELECT review_id AS ReviewId,action AS Action
            FROM application_case.financial_review_operations
            WHERE tenant_id=@Tenant AND professional_actor_id=@Actor AND operation_id=@Operation;
            """, new { Tenant = before.TenantId, Actor = before.ActorId, Operation = operationId }, transaction, ct))
            .ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.ReviewId != reviewId || replay.Action != action)
                throw new ApplicationCaseException("OPERATION_REUSE_CONFLICT", 409);
            var replayed = await AssignedAsync(connection, transaction, before, reviewId, false, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return await ViewAsync(connection, null, before.TenantId, replayed, ct).ConfigureAwait(false);
        }

        var row = await AssignedAsync(connection, transaction, before, reviewId, true, ct).ConfigureAwait(false);
        if (row.Version != expectedVersion)
            throw new ApplicationCaseException("FINANCIAL_REVIEW_VERSION_CHANGED", 409);
        if (row.CertificationOutcome is not null)
            throw new ApplicationCaseException("FINANCIAL_REVIEW_ALREADY_CERTIFIED", 409);
        var authority = await confirm(row.CustomerId, ct).ConfigureAwait(false);
        FinancialProfessionalReviewPolicy.RequireCurrent(before, authority);
        var now = clock.GetUtcNow();
        if (action == "SOURCE_REVIEW")
        {
            var changed = await connection.ExecuteAsync(Command("""
                UPDATE application_case.financial_review_sources
                SET reviewed_at=@Now,reviewed_by=@Actor
                WHERE tenant_id=@Tenant AND review_id=@Review AND source_code=@Source
                  AND assigned_version=current_version AND reviewed_at IS NULL;
                """, new { Now = now, Actor = before.ActorId, Tenant = before.TenantId,
                    Review = reviewId, Source = sourceCode }, transaction, ct)).ConfigureAwait(false);
            if (changed != 1)
            {
                var exists = await connection.ExecuteScalarAsync<bool>(Command("""
                    SELECT EXISTS(SELECT 1 FROM application_case.financial_review_sources
                        WHERE tenant_id=@Tenant AND review_id=@Review AND source_code=@Source);
                    """, new { Tenant = before.TenantId, Review = reviewId, Source = sourceCode }, transaction, ct)).ConfigureAwait(false);
                throw new ApplicationCaseException(exists ? "FINANCIAL_SOURCE_VERSION_CHANGED" : "FINANCIAL_SOURCE_NOT_FOUND",
                    exists ? 409 : 404);
            }
        }
        else if (action == "NOTE")
        {
            var valid = await connection.ExecuteScalarAsync<bool>(Command("""
                SELECT count(*)=@Count AND bool_and(assigned_version=current_version)
                FROM application_case.financial_review_sources
                WHERE tenant_id=@Tenant AND review_id=@Review AND source_code=ANY(@Sources);
                """, new { Count = sourceCodes!.Length, Tenant = before.TenantId, Review = reviewId, Sources = sourceCodes },
                transaction, ct)).ConfigureAwait(false);
            if (!valid) throw new ApplicationCaseException("FINANCIAL_NOTE_SOURCE_CHANGED", 409);
            await connection.ExecuteAsync(Command("""
                INSERT INTO application_case.financial_review_notes(
                    note_id,tenant_id,review_id,professional_actor_id,note,source_codes,recorded_at)
                VALUES(@NoteId,@Tenant,@Review,@Actor,@Text,@Sources,@Now);
                """, new { NoteId = Guid.NewGuid(), Tenant = before.TenantId, Review = reviewId,
                    Actor = before.ActorId, Text = text, Sources = sourceCodes, Now = now }, transaction, ct)).ConfigureAwait(false);
        }
        else
        {
            var incomplete = await connection.ExecuteScalarAsync<bool>(Command("""
                SELECT EXISTS(SELECT 1 FROM application_case.financial_review_sources
                    WHERE tenant_id=@Tenant AND review_id=@Review
                      AND (reviewed_at IS NULL OR assigned_version<>current_version));
                """, new { Tenant = before.TenantId, Review = reviewId }, transaction, ct)).ConfigureAwait(false);
            if (incomplete) throw new ApplicationCaseException("FINANCIAL_SOURCE_REVIEW_INCOMPLETE", 409);
            await connection.ExecuteAsync(Command("""
                INSERT INTO application_case.financial_review_certification_sources(
                    tenant_id,review_id,source_code,owner_service,resource_reference,source_version,
                    certified_by,certified_at)
                SELECT tenant_id,review_id,source_code,owner_service,resource_reference,current_version,@Actor,@Now
                FROM application_case.financial_review_sources
                WHERE tenant_id=@Tenant AND review_id=@Review ORDER BY sequence;
                """, new { Tenant = before.TenantId, Review = reviewId, Actor = before.ActorId, Now = now },
                transaction, ct)).ConfigureAwait(false);
        }
        var nextVersion = expectedVersion + 1;
        var state = action == "CERTIFICATION" ? "Certified" : "Under review";
        var updated = await connection.ExecuteAsync(Command("""
            UPDATE application_case.financial_professional_reviews
            SET state=@State,version=@Version,
                certification_outcome=CASE WHEN @Action='CERTIFICATION' THEN @Outcome ELSE certification_outcome END,
                certification_rationale=CASE WHEN @Action='CERTIFICATION' THEN @Text ELSE certification_rationale END,
                certified_at=CASE WHEN @Action='CERTIFICATION' THEN @Now ELSE certified_at END,
                certified_by=CASE WHEN @Action='CERTIFICATION' THEN @Actor ELSE certified_by END,
                certified_policy_version=CASE WHEN @Action='CERTIFICATION' THEN @PolicyVersion ELSE certified_policy_version END,
                certified_subject_version=CASE WHEN @Action='CERTIFICATION' THEN @SubjectVersion ELSE certified_subject_version END,
                certified_customer_version=CASE WHEN @Action='CERTIFICATION' THEN @CustomerVersion ELSE certified_customer_version END,
                certified_consent_version=CASE WHEN @Action='CERTIFICATION' THEN @ConsentVersion ELSE certified_consent_version END
            WHERE tenant_id=@Tenant AND review_id=@Review AND assigned_professional_actor_id=@Actor
              AND version=@ExpectedVersion;
            """, new { State = state, Version = nextVersion, Action = action, Outcome = outcome, Text = text,
                Now = now, Actor = before.ActorId, authority.PolicyVersion, authority.SubjectVersion,
                authority.CustomerVersion, authority.ConsentVersion, Tenant = before.TenantId, Review = reviewId,
                ExpectedVersion = expectedVersion }, transaction, ct)).ConfigureAwait(false);
        if (updated != 1) throw new ApplicationCaseException("FINANCIAL_REVIEW_VERSION_CHANGED", 409);
        await connection.ExecuteAsync(Command("""
            INSERT INTO application_case.financial_review_events(
                event_id,tenant_id,review_id,professional_actor_id,action,source_code,note_source_codes,
                outcome,version,policy_version,subject_version,customer_version,consent_version,occurred_at)
            VALUES(@Event,@Tenant,@Review,@Actor,@Action,@Source,@NoteSources,@Outcome,@Version,
                @PolicyVersion,@SubjectVersion,@CustomerVersion,@ConsentVersion,@Now);
            INSERT INTO application_case.financial_review_operations(
                tenant_id,professional_actor_id,operation_id,review_id,action)
            VALUES(@Tenant,@Actor,@Operation,@Review,@Action);
            """, new { Event = Guid.NewGuid(), Tenant = before.TenantId, Review = reviewId, Actor = before.ActorId,
                Action = action, Source = sourceCode, NoteSources = sourceCodes, Outcome = outcome, Version = nextVersion,
                authority.PolicyVersion, authority.SubjectVersion, authority.CustomerVersion, authority.ConsentVersion,
                Now = now, Operation = operationId }, transaction, ct)).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        var current = await AssignedAsync(connection, null, before, reviewId, false, ct).ConfigureAwait(false);
        return await ViewAsync(connection, null, before.TenantId, current, ct).ConfigureAwait(false);
    }

    private static async Task<ReviewRow> AssignedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        FinancialProfessionalContext context, string reviewId, bool locked, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ReviewRow>(Command($"""
            SELECT review_id AS ReviewId,review_reference AS ReviewReference,customer_id AS CustomerId,
                state AS State,version AS Version,assigned_at AS AssignedAt,
                certification_outcome AS CertificationOutcome,certification_rationale AS CertificationRationale,
                certified_at AS CertifiedAt,certified_by AS CertifiedBy,
                certified_policy_version AS CertifiedPolicyVersion,
                certified_subject_version AS CertifiedSubjectVersion,
                certified_customer_version AS CertifiedCustomerVersion,
                certified_consent_version AS CertifiedConsentVersion
            FROM application_case.financial_professional_reviews
            WHERE tenant_id=@Tenant AND review_id=@Review AND assigned_professional_actor_id=@Actor
            {(locked ? "FOR UPDATE" : "")};
            """, new { Tenant = context.TenantId, Review = reviewId, Actor = context.ActorId }, transaction, ct))
            .ConfigureAwait(false);
        return row ?? throw new ApplicationCaseException("FINANCIAL_REVIEW_NOT_FOUND", 404);
    }

    private static async Task<FinancialProfessionalReviewView> ViewAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, string tenant, ReviewRow row, CancellationToken ct)
    {
        var sources = (await connection.QueryAsync<SourceRow>(Command("""
            SELECT source_code AS Code,label AS Label,owner_service AS Owner,
                resource_reference AS Reference,assigned_version AS AssignedVersion,
                current_version AS CurrentVersion,reviewed_at AS ReviewedAt
            FROM application_case.financial_review_sources
            WHERE tenant_id=@Tenant AND review_id=@Review ORDER BY sequence;
            """, new { Tenant = tenant, Review = row.ReviewId }, transaction, ct)).ConfigureAwait(false)).ToArray();
        var notes = (await connection.QueryAsync<NoteRow>(Command("""
            SELECT note AS Note,source_codes AS SourceCodes,recorded_at AS RecordedAt,
                professional_actor_id AS RecordedBy
            FROM application_case.financial_review_notes
            WHERE tenant_id=@Tenant AND review_id=@Review ORDER BY recorded_at,note_id;
            """, new { Tenant = tenant, Review = row.ReviewId }, transaction, ct)).ConfigureAwait(false)).ToArray();
        FinancialCertificationView? certification = null;
        if (row.CertificationOutcome is not null)
        {
            var snapshots = (await connection.QueryAsync<CertificationSourceRow>(Command("""
                SELECT source_code AS Code,owner_service AS Owner,resource_reference AS Reference,
                    source_version AS Version
                FROM application_case.financial_review_certification_sources
                WHERE tenant_id=@Tenant AND review_id=@Review ORDER BY source_code;
                """, new { Tenant = tenant, Review = row.ReviewId }, transaction, ct)).ConfigureAwait(false)).ToArray();
            certification = new(row.CertificationOutcome,
                row.CertificationRationale ?? throw InvalidHistory(),
                row.CertifiedAt ?? throw InvalidHistory(), row.CertifiedBy ?? throw InvalidHistory(),
                row.CertifiedPolicyVersion ?? throw InvalidHistory(),
                row.CertifiedSubjectVersion ?? throw InvalidHistory(),
                row.CertifiedCustomerVersion ?? throw InvalidHistory(),
                row.CertifiedConsentVersion ?? throw InvalidHistory(),
                snapshots.Select(value => new FinancialCertificationSourceView(value.Code, value.Owner,
                    value.Reference, value.Version)).ToArray());
        }
        return new(row.ReviewId, row.ReviewReference, row.State, row.Version, row.AssignedAt,
            sources.Select(value => new FinancialReviewSourceView(value.Code, value.Label, value.Owner,
                value.Reference, value.AssignedVersion, value.CurrentVersion, value.ReviewedAt is not null,
                value.ReviewedAt)).ToArray(), notes.Select(value => new FinancialProfessionalNoteView(
                    value.Note, value.SourceCodes, value.RecordedAt, value.RecordedBy)).ToArray(), certification, Durability);
    }

    private async Task<NpgsqlConnection> OpenAsync(string tenant, CancellationToken ct)
    {
        NpgsqlConnection connection;
        try { connection = await connections.OpenAsync(tenant, ct).ConfigureAwait(false); }
        catch (Exception failure) when (failure is NpgsqlException or TenantBoundaryException)
        { throw new ApplicationCaseException("CASE_TENANT_UNAVAILABLE", 503); }
        try
        {
            var valid = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM application_case.tenant_identity
                    WHERE singleton AND tenant_id=@Tenant AND schema_version=6)
                  AND has_table_privilege(current_user,'application_case.financial_professional_reviews','SELECT')
                  AND has_column_privilege(current_user,'application_case.financial_professional_reviews','state','UPDATE')
                  AND has_column_privilege(current_user,'application_case.financial_professional_reviews','version','UPDATE')
                  AND has_column_privilege(current_user,'application_case.financial_professional_reviews','certification_outcome','UPDATE')
                  AND has_table_privilege(current_user,'application_case.financial_review_sources','SELECT')
                  AND has_column_privilege(current_user,'application_case.financial_review_sources','reviewed_at','UPDATE')
                  AND has_column_privilege(current_user,'application_case.financial_review_sources','reviewed_by','UPDATE')
                  AND has_table_privilege(current_user,'application_case.financial_review_notes','SELECT,INSERT')
                  AND has_table_privilege(current_user,'application_case.financial_review_events','SELECT,INSERT')
                  AND has_table_privilege(current_user,'application_case.financial_review_operations','SELECT,INSERT')
                  AND has_table_privilege(current_user,'application_case.financial_review_certification_sources','SELECT,INSERT')
                  AND NOT has_table_privilege(current_user,'application_case.financial_professional_reviews','INSERT,DELETE,TRUNCATE')
                  AND NOT has_column_privilege(current_user,'application_case.financial_professional_reviews','customer_id','UPDATE')
                  AND NOT has_column_privilege(current_user,'application_case.financial_professional_reviews','assigned_professional_actor_id','UPDATE')
                  AND NOT has_table_privilege(current_user,'application_case.financial_review_sources','INSERT,DELETE,TRUNCATE')
                  AND NOT has_column_privilege(current_user,'application_case.financial_review_sources','current_version','UPDATE')
                  AND NOT has_table_privilege(current_user,'application_case.financial_review_notes','UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'application_case.financial_review_events','UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'application_case.financial_review_operations','UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'application_case.financial_review_certification_sources','UPDATE,DELETE,TRUNCATE');
                """, new { Tenant = tenant }, cancellationToken: ct)).ConfigureAwait(false);
            if (!valid) throw new ApplicationCaseException("CASE_BINDING_INVALID", 503);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static ApplicationCaseException InvalidHistory() =>
        new("FINANCIAL_REVIEW_HISTORY_INVALID", 503);
    private static CommandDefinition Command(string sql, object? values,
        NpgsqlTransaction? transaction, CancellationToken ct) =>
        new(sql, values, transaction, cancellationToken: ct);

    public async ValueTask DisposeAsync()
    {
        await connections.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record ReviewRow(string ReviewId, string ReviewReference, string CustomerId,
        string State, int Version, DateTime AssignedAt, string? CertificationOutcome,
        string? CertificationRationale, DateTime? CertifiedAt, string? CertifiedBy,
        long? CertifiedPolicyVersion, long? CertifiedSubjectVersion, long? CertifiedCustomerVersion,
        long? CertifiedConsentVersion);
    private sealed record SourceRow(string Code, string Label, string Owner, string Reference,
        long AssignedVersion, long CurrentVersion, DateTime? ReviewedAt);
    // Npgsql exposes PostgreSQL arrays through DbDataReader as System.Array. Dapper's
    // positional-record constructor matching therefore cannot bind a text[] column to
    // a string[] constructor parameter, even though the runtime value is a string[].
    // Property materialization keeps the precise public type while remaining compatible
    // with the physical provider metadata.
    private sealed class NoteRow
    {
        public string Note { get; init; } = "";
        public string[] SourceCodes { get; init; } = [];
        public DateTime RecordedAt { get; init; }
        public string RecordedBy { get; init; } = "";
    }
    private sealed record CertificationSourceRow(string Code, string Owner, string Reference, long Version);
    private sealed record OperationRow(string ReviewId, string Action);
}
