using Dapper;
using Monergy.Platform;
using Monergy.Services.ApplicationCase.Application;
using Npgsql;

namespace Monergy.Services.ApplicationCase.Infrastructure;

public sealed class PostgresLegalReviewApplication : ILegalReviewApplication, IAsyncDisposable
{
    private const string Durability = "Durable test data";
    private readonly TimeProvider clock;
    private readonly Dictionary<string, NpgsqlDataSource> sources = new(StringComparer.Ordinal);

    public PostgresLegalReviewApplication(IConfiguration configuration, TimeProvider clock)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        this.clock = clock;
        foreach (var binding in configuration
            .GetSection("Monergy:Persistence:ApplicationCase:RuntimeConnections").GetChildren())
        {
            if (!TenantAccessClient.Identifier(binding.Key, 64) || string.IsNullOrWhiteSpace(binding.Value))
                throw new InvalidOperationException("Invalid Application & Case tenant binding.");
            var options = new NpgsqlConnectionStringBuilder(binding.Value)
            {
                IncludeErrorDetail = false,
                NoResetOnClose = false,
                Timeout = 5,
                CommandTimeout = 15,
                MaxPoolSize = 20,
            };
            sources.Add(binding.Key, NpgsqlDataSource.Create(options.ConnectionString));
        }
        if (sources.Count == 0)
            throw new InvalidOperationException("Application & Case database bindings are required.");
    }

    public async Task<IReadOnlyList<LegalReviewView>> ListAsync(
        LegalAdvisorContext context,
        Func<string, CancellationToken, Task<LegalReviewContext>> authorize,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(context.TenantId, ct).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<AssignmentRow>(Command("""
            SELECT review_id AS ReviewId,matter_reference AS MatterReference,
                matter_label AS MatterLabel,customer_id AS CustomerId,state AS State,
                version AS Version,assigned_at AS AssignedAt
            FROM application_case.legal_review_assignments
            WHERE tenant_id=@Tenant AND assigned_legal_advisor_actor_id=@Actor
            ORDER BY assigned_at,review_id;
            """, new { Tenant = context.TenantId, Actor = context.ActorId }, null, ct))
            .ConfigureAwait(false)).ToArray();
        var result = new List<LegalReviewView>(rows.Length);
        foreach (var row in rows)
        {
            var authority = await authorize(row.CustomerId, ct).ConfigureAwait(false);
            LegalReviewPolicy.RequireCurrent(context, authority);
            result.Add(await ViewAsync(connection, null, context.TenantId, row, ct).ConfigureAwait(false));
        }
        return result;
    }

    public async Task<IReadOnlyList<CustomerLegalReviewView>> ListCustomerResultsAsync(
        CaseActorContext customer, CancellationToken ct)
    {
        if (customer.CustomerId is null)
            throw new ApplicationCaseException("CUSTOMER_CASE_ACCESS_REQUIRED", 403);
        await using var connection = await OpenAsync(customer.TenantId, ct).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<AssignmentRow>(Command("""
            SELECT review_id AS ReviewId,matter_reference AS MatterReference,
                matter_label AS MatterLabel,customer_id AS CustomerId,state AS State,
                version AS Version,assigned_at AS AssignedAt
            FROM application_case.legal_review_assignments
            WHERE tenant_id=@Tenant AND customer_id=@Customer AND state='Reviewed'
            ORDER BY assigned_at,review_id;
            """, new { Tenant = customer.TenantId, Customer = customer.CustomerId }, null, ct))
            .ConfigureAwait(false)).ToArray();
        var result = new List<CustomerLegalReviewView>(rows.Length);
        foreach (var row in rows)
        {
            var view = await ViewAsync(connection, null, customer.TenantId, row, ct).ConfigureAwait(false);
            if (view.Revisions.Count > 0) result.Add(CustomerView(view));
        }
        return result;
    }

    public Task<LegalReviewView> InspectEvidenceAsync(
        string reviewId, string evidenceCode, LegalAdvisorContext before,
        InspectLegalDocument request,
        Func<string, CancellationToken, Task<LegalReviewContext>> confirm, CancellationToken ct) =>
        MutateAsync(reviewId, before, request.OperationId, request.ExpectedVersion,
            "EVIDENCE_INSPECTION", LegalReviewPolicy.Identifier(evidenceCode,
                "LEGAL_DOCUMENT_CODE_INVALID", 64), null, confirm, ct);

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
        return MutateAsync(reviewId, before, normalized.OperationId, normalized.ExpectedVersion,
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
        var fingerprint = LegalReviewApplication.Fingerprint(action, evidenceCode, request);
        await using var connection = await OpenAsync(before.TenantId, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(Command("SELECT pg_advisory_xact_lock(hashtextextended(@Value,0));",
            new { Value = "legal-review-operation:" + before.TenantId + ":" + before.ActorId + ":" + operationId },
            transaction, ct)).ConfigureAwait(false);
        var replay = await connection.QuerySingleOrDefaultAsync<OperationRow>(Command("""
            SELECT review_id AS ReviewId,action AS Action,request_fingerprint AS RequestFingerprint
            FROM application_case.legal_review_operations
            WHERE tenant_id=@Tenant AND legal_advisor_actor_id=@Actor AND operation_id=@Operation;
            """, new { Tenant = before.TenantId, Actor = before.ActorId, Operation = operationId }, transaction, ct))
            .ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.ReviewId != reviewId || replay.Action != action ||
                replay.RequestFingerprint != fingerprint)
                throw new ApplicationCaseException("OPERATION_REUSE_CONFLICT", 409);
            var replayed = await AssignedAsync(connection, transaction, before, reviewId, false, ct)
                .ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return await ViewAsync(connection, null, before.TenantId, replayed, ct).ConfigureAwait(false);
        }

        var row = await AssignedAsync(connection, transaction, before, reviewId, true, ct)
            .ConfigureAwait(false);
        if (row.Version != expectedVersion)
            throw new ApplicationCaseException("LEGAL_REVIEW_VERSION_CHANGED", 409);
        var authority = await confirm(row.CustomerId, ct).ConfigureAwait(false);
        LegalReviewPolicy.RequireCurrent(before, authority);
        var now = clock.GetUtcNow();
        int? reviewRevision = null;
        if (action == "EVIDENCE_INSPECTION")
        {
            var changed = await connection.ExecuteAsync(Command("""
                UPDATE application_case.legal_review_evidence
                SET inspected_at=@Now,inspected_by=@Actor
                WHERE tenant_id=@Tenant AND review_id=@Review AND evidence_code=@Evidence
                  AND assigned_version=current_version AND inspected_at IS NULL;
                """, new { Now = now, Actor = before.ActorId, Tenant = before.TenantId,
                    Review = reviewId, Evidence = evidenceCode }, transaction, ct)).ConfigureAwait(false);
            if (changed != 1)
            {
                var exists = await connection.ExecuteScalarAsync<bool>(Command("""
                    SELECT EXISTS(SELECT 1 FROM application_case.legal_review_evidence
                        WHERE tenant_id=@Tenant AND review_id=@Review AND evidence_code=@Evidence);
                    """, new { Tenant = before.TenantId, Review = reviewId, Evidence = evidenceCode },
                    transaction, ct)).ConfigureAwait(false);
                throw new ApplicationCaseException(exists ? "LEGAL_DOCUMENT_VERSION_CHANGED" :
                    "LEGAL_DOCUMENT_NOT_FOUND", exists ? 409 : 404);
            }
        }
        else
        {
            var incomplete = await connection.ExecuteScalarAsync<bool>(Command("""
                SELECT EXISTS(SELECT 1 FROM application_case.legal_review_evidence
                    WHERE tenant_id=@Tenant AND review_id=@Review
                      AND (inspected_at IS NULL OR assigned_version<>current_version));
                """, new { Tenant = before.TenantId, Review = reviewId }, transaction, ct))
                .ConfigureAwait(false);
            if (incomplete) throw new ApplicationCaseException("LEGAL_DOCUMENT_INSPECTION_INCOMPLETE", 409);
            reviewRevision = await connection.ExecuteScalarAsync<int>(Command("""
                SELECT COALESCE(max(revision),0)+1
                FROM application_case.legal_review_revisions
                WHERE tenant_id=@Tenant AND review_id=@Review;
                """, new { Tenant = before.TenantId, Review = reviewId }, transaction, ct))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(Command("""
                INSERT INTO application_case.legal_review_revisions(
                    tenant_id,review_id,revision,outcome,findings,
                    recorded_at,recorded_by,policy_version,subject_version,customer_version,consent_version)
                VALUES(@Tenant,@Review,@Revision,@Outcome,@Findings,
                    @Now,@Actor,@PolicyVersion,@SubjectVersion,@CustomerVersion,@ConsentVersion);
                INSERT INTO application_case.legal_review_revision_evidence(
                    tenant_id,review_id,revision,evidence_code,owner_service,resource_reference,
                    source_version,recorded_by,recorded_at)
                SELECT tenant_id,review_id,@Revision,evidence_code,owner_service,resource_reference,
                    current_version,@Actor,@Now
                FROM application_case.legal_review_evidence
                WHERE tenant_id=@Tenant AND review_id=@Review ORDER BY sequence;
                """, new { Tenant = before.TenantId, Review = reviewId, Revision = reviewRevision,
                    request!.Outcome, request.Findings,
                    Now = now, Actor = before.ActorId, authority.PolicyVersion, authority.SubjectVersion,
                    authority.CustomerVersion, authority.ConsentVersion }, transaction, ct)).ConfigureAwait(false);
        }
        var nextVersion = expectedVersion + 1;
        var state = action == "OUTCOME" ? "Reviewed" : "Inspection in progress";
        var updated = await connection.ExecuteAsync(Command("""
            UPDATE application_case.legal_review_assignments
            SET state=@State,version=@Version
            WHERE tenant_id=@Tenant AND review_id=@Review AND assigned_legal_advisor_actor_id=@Actor
              AND version=@ExpectedVersion;
            """, new { State = state, Version = nextVersion, Tenant = before.TenantId,
                Review = reviewId, Actor = before.ActorId, ExpectedVersion = expectedVersion },
                transaction, ct)).ConfigureAwait(false);
        if (updated != 1) throw new ApplicationCaseException("LEGAL_REVIEW_VERSION_CHANGED", 409);
        await connection.ExecuteAsync(Command("""
            INSERT INTO application_case.legal_review_events(
                event_id,tenant_id,review_id,legal_advisor_actor_id,action,evidence_code,review_revision,
                version,policy_version,subject_version,customer_version,consent_version,occurred_at)
            VALUES(@Event,@Tenant,@Review,@Actor,@Action,@Evidence,@Revision,@Version,
                @PolicyVersion,@SubjectVersion,@CustomerVersion,@ConsentVersion,@Now);
            INSERT INTO application_case.legal_review_operations(
                tenant_id,legal_advisor_actor_id,operation_id,review_id,action,request_fingerprint)
            VALUES(@Tenant,@Actor,@Operation,@Review,@Action,@Fingerprint);
            """, new { Event = Guid.NewGuid(), Tenant = before.TenantId, Review = reviewId,
                Actor = before.ActorId, Action = action, Evidence = evidenceCode, Revision = reviewRevision,
                Version = nextVersion, authority.PolicyVersion, authority.SubjectVersion, Fingerprint = fingerprint,
                authority.CustomerVersion, authority.ConsentVersion, Now = now, Operation = operationId },
                transaction, ct)).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        var current = await AssignedAsync(connection, null, before, reviewId, false, ct).ConfigureAwait(false);
        return await ViewAsync(connection, null, before.TenantId, current, ct).ConfigureAwait(false);
    }

    private static async Task<AssignmentRow> AssignedAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, LegalAdvisorContext context, string reviewId,
        bool locked, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<AssignmentRow>(Command($"""
            SELECT review_id AS ReviewId,matter_reference AS MatterReference,
                matter_label AS MatterLabel,customer_id AS CustomerId,state AS State,
                version AS Version,assigned_at AS AssignedAt
            FROM application_case.legal_review_assignments
            WHERE tenant_id=@Tenant AND review_id=@Review AND assigned_legal_advisor_actor_id=@Actor
            {(locked ? "FOR UPDATE" : "")};
            """, new { Tenant = context.TenantId, Review = reviewId, Actor = context.ActorId },
                transaction, ct)).ConfigureAwait(false);
        return row ?? throw new ApplicationCaseException("LEGAL_REVIEW_NOT_FOUND", 404);
    }

    private static async Task<LegalReviewView> ViewAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, string tenant, AssignmentRow row, CancellationToken ct)
    {
        var evidence = (await connection.QueryAsync<EvidenceRow>(Command("""
            SELECT evidence_code AS Code,label AS Label,owner_service AS Owner,
                resource_reference AS Reference,assigned_version AS AssignedVersion,
                current_version AS CurrentVersion,inspected_at AS InspectedAt
            FROM application_case.legal_review_evidence
            WHERE tenant_id=@Tenant AND review_id=@Review ORDER BY sequence;
            """, new { Tenant = tenant, Review = row.ReviewId }, transaction, ct))
            .ConfigureAwait(false)).ToArray();
        var revisions = (await connection.QueryAsync<RevisionRow>(Command("""
            SELECT revision AS Revision,outcome AS Outcome,findings AS Findings,
                recorded_at AS RecordedAt,recorded_by AS RecordedBy,policy_version AS PolicyVersion,
                subject_version AS SubjectVersion,customer_version AS CustomerVersion,
                consent_version AS ConsentVersion
            FROM application_case.legal_review_revisions
            WHERE tenant_id=@Tenant AND review_id=@Review ORDER BY revision;
            """, new { Tenant = tenant, Review = row.ReviewId }, transaction, ct))
            .ConfigureAwait(false)).ToArray();
        var snapshots = (await connection.QueryAsync<SnapshotRow>(Command("""
            SELECT revision AS Revision,evidence_code AS Code,owner_service AS Owner,
                resource_reference AS Reference,source_version AS Version
            FROM application_case.legal_review_revision_evidence
            WHERE tenant_id=@Tenant AND review_id=@Review ORDER BY revision,evidence_code;
            """, new { Tenant = tenant, Review = row.ReviewId }, transaction, ct))
            .ConfigureAwait(false)).ToArray();
        return new(row.ReviewId, row.MatterReference, row.MatterLabel, row.State, row.Version,
            row.AssignedAt, evidence.Select(value => new LegalDocumentView(value.Code, value.Label,
                value.Owner, value.Reference, value.AssignedVersion, value.CurrentVersion,
                value.InspectedAt is not null, value.InspectedAt)).ToArray(),
            revisions.Select(value => new LegalReviewRevisionView(value.Revision, value.Outcome,
                value.Findings, value.RecordedAt,
                value.RecordedBy, value.PolicyVersion, value.SubjectVersion, value.CustomerVersion,
                value.ConsentVersion, snapshots.Where(item => item.Revision == value.Revision)
                    .Select(item => new LegalReviewEvidenceSnapshot(item.Code, item.Owner,
                        item.Reference, item.Version)).ToArray())).ToArray(), Durability);
    }

    private async Task<NpgsqlConnection> OpenAsync(string tenant, CancellationToken ct)
    {
        if (!sources.TryGetValue(tenant, out var source))
            throw new ApplicationCaseException("CASE_TENANT_UNAVAILABLE", 503);
        NpgsqlConnection connection;
        try { connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false); }
        catch (NpgsqlException) { throw new ApplicationCaseException("CASE_TENANT_UNAVAILABLE", 503); }
        try
        {
            var valid = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM application_case.tenant_identity
                    WHERE singleton AND tenant_id=@Tenant AND schema_version=6)
                  AND has_table_privilege(current_user,'application_case.legal_review_assignments','SELECT')
                  AND has_column_privilege(current_user,'application_case.legal_review_assignments','state','UPDATE')
                  AND has_column_privilege(current_user,'application_case.legal_review_assignments','version','UPDATE')
                  AND has_table_privilege(current_user,'application_case.legal_review_evidence','SELECT')
                  AND has_column_privilege(current_user,'application_case.legal_review_evidence','inspected_at','UPDATE')
                  AND has_column_privilege(current_user,'application_case.legal_review_evidence','inspected_by','UPDATE')
                  AND has_table_privilege(current_user,'application_case.legal_review_revisions','SELECT,INSERT')
                  AND has_table_privilege(current_user,'application_case.legal_review_revision_evidence','SELECT,INSERT')
                  AND has_table_privilege(current_user,'application_case.legal_review_events','SELECT,INSERT')
                  AND has_table_privilege(current_user,'application_case.legal_review_operations','SELECT,INSERT')
                  AND NOT has_table_privilege(current_user,'application_case.legal_review_assignments','INSERT,DELETE,TRUNCATE')
                  AND NOT has_column_privilege(current_user,'application_case.legal_review_assignments','customer_id','UPDATE')
                  AND NOT has_column_privilege(current_user,'application_case.legal_review_assignments','assigned_legal_advisor_actor_id','UPDATE')
                  AND NOT has_table_privilege(current_user,'application_case.legal_review_evidence','INSERT,DELETE,TRUNCATE')
                  AND NOT has_column_privilege(current_user,'application_case.legal_review_evidence','current_version','UPDATE')
                  AND NOT has_table_privilege(current_user,'application_case.legal_review_revisions','UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'application_case.legal_review_revision_evidence','UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'application_case.legal_review_events','UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'application_case.legal_review_operations','UPDATE,DELETE,TRUNCATE');
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

    private static CustomerLegalReviewView CustomerView(LegalReviewView row)
    {
        var latest = row.Revisions[^1];
        return new(row.ReviewId, row.MatterReference, row.MatterLabel, latest.Revision,
            latest.Outcome, latest.Findings,
            latest.RecordedAt, latest.RecordedBy, latest.Evidence.Count, row.DataDurability);
    }

    private static CommandDefinition Command(string sql, object? values,
        NpgsqlTransaction? transaction, CancellationToken ct) =>
        new(sql, values, transaction, cancellationToken: ct);

    public async ValueTask DisposeAsync()
    {
        foreach (var source in sources.Values) await source.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record AssignmentRow(string ReviewId, string MatterReference,
        string MatterLabel, string CustomerId, string State, int Version, DateTime AssignedAt);
    private sealed record EvidenceRow(string Code, string Label, string Owner, string Reference,
        long AssignedVersion, long CurrentVersion, DateTime? InspectedAt);
    private sealed record RevisionRow(int Revision, string Outcome, string Findings, DateTime RecordedAt,
        string RecordedBy, long PolicyVersion, long SubjectVersion, long CustomerVersion,
        long ConsentVersion);
    private sealed record SnapshotRow(int Revision, string Code, string Owner, string Reference, long Version);
    private sealed record OperationRow(string ReviewId, string Action, string RequestFingerprint);
}
