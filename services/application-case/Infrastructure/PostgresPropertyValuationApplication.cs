using Dapper;
using Monergy.Platform;
using Monergy.Services.ApplicationCase.Application;
using Npgsql;

namespace Monergy.Services.ApplicationCase.Infrastructure;

public sealed class PostgresPropertyValuationApplication : IPropertyValuationApplication, IAsyncDisposable
{
    private const string Durability = "Durable test data";
    private readonly TimeProvider clock;
    private readonly Dictionary<string, NpgsqlDataSource> sources = new(StringComparer.Ordinal);

    public PostgresPropertyValuationApplication(IConfiguration configuration, TimeProvider clock)
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

    public async Task<IReadOnlyList<PropertyValuationView>> ListAsync(
        PropertyValuerContext context,
        Func<string, CancellationToken, Task<PropertyValuationContext>> authorize,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(context.TenantId, ct).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<AssignmentRow>(Command("""
            SELECT valuation_id AS ValuationId,property_reference AS PropertyReference,
                property_label AS PropertyLabel,customer_id AS CustomerId,state AS State,
                version AS Version,assigned_at AS AssignedAt
            FROM application_case.property_valuation_assignments
            WHERE tenant_id=@Tenant AND assigned_valuer_actor_id=@Actor
            ORDER BY assigned_at,valuation_id;
            """, new { Tenant = context.TenantId, Actor = context.ActorId }, null, ct))
            .ConfigureAwait(false)).ToArray();
        var result = new List<PropertyValuationView>(rows.Length);
        foreach (var row in rows)
        {
            var authority = await authorize(row.CustomerId, ct).ConfigureAwait(false);
            PropertyValuationPolicy.RequireCurrent(context, authority);
            result.Add(await ViewAsync(connection, null, context.TenantId, row, ct).ConfigureAwait(false));
        }
        return result;
    }

    public async Task<IReadOnlyList<CustomerPropertyValuationView>> ListCustomerResultsAsync(
        CaseActorContext customer, CancellationToken ct)
    {
        if (customer.CustomerId is null)
            throw new ApplicationCaseException("CUSTOMER_CASE_ACCESS_REQUIRED", 403);
        await using var connection = await OpenAsync(customer.TenantId, ct).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<AssignmentRow>(Command("""
            SELECT valuation_id AS ValuationId,property_reference AS PropertyReference,
                property_label AS PropertyLabel,customer_id AS CustomerId,state AS State,
                version AS Version,assigned_at AS AssignedAt
            FROM application_case.property_valuation_assignments
            WHERE tenant_id=@Tenant AND customer_id=@Customer AND state='Valued'
            ORDER BY assigned_at,valuation_id;
            """, new { Tenant = customer.TenantId, Customer = customer.CustomerId }, null, ct))
            .ConfigureAwait(false)).ToArray();
        var result = new List<CustomerPropertyValuationView>(rows.Length);
        foreach (var row in rows)
        {
            var view = await ViewAsync(connection, null, customer.TenantId, row, ct).ConfigureAwait(false);
            if (view.Revisions.Count > 0) result.Add(CustomerView(view));
        }
        return result;
    }

    public Task<PropertyValuationView> InspectEvidenceAsync(
        string valuationId, string evidenceCode, PropertyValuerContext before,
        InspectPropertyEvidence request,
        Func<string, CancellationToken, Task<PropertyValuationContext>> confirm, CancellationToken ct) =>
        MutateAsync(valuationId, before, request.OperationId, request.ExpectedVersion,
            "EVIDENCE_INSPECTION", PropertyValuationPolicy.Identifier(evidenceCode,
                "PROPERTY_EVIDENCE_CODE_INVALID", 64), null, confirm, ct);

    public Task<PropertyValuationView> RecordValuationAsync(
        string valuationId, PropertyValuerContext before, RecordPropertyValuation request,
        Func<string, CancellationToken, Task<PropertyValuationContext>> confirm, CancellationToken ct)
    {
        PropertyValuationPolicy.Request(request.ExpectedVersion, request.MarketValue,
            request.EffectiveDate, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
        var normalized = request with
        {
            Currency = PropertyValuationPolicy.Currency(request.Currency),
            Basis = PropertyValuationPolicy.Text(request.Basis, "PROPERTY_VALUATION_BASIS_INVALID", 3, 500),
            Findings = PropertyValuationPolicy.Text(request.Findings, "PROPERTY_VALUATION_FINDINGS_INVALID", 3, 2000),
        };
        return MutateAsync(valuationId, before, normalized.OperationId, normalized.ExpectedVersion,
            "VALUATION", null, normalized, confirm, ct);
    }

    private async Task<PropertyValuationView> MutateAsync(
        string valuationId, PropertyValuerContext before, string operationId, int expectedVersion,
        string action, string? evidenceCode, RecordPropertyValuation? request,
        Func<string, CancellationToken, Task<PropertyValuationContext>> confirm, CancellationToken ct)
    {
        PropertyValuationPolicy.Identifier(valuationId, "PROPERTY_VALUATION_ID_INVALID");
        PropertyValuationPolicy.Operation(operationId);
        PropertyValuationPolicy.ExpectedVersion(expectedVersion);
        var fingerprint = PropertyValuationApplication.Fingerprint(action, evidenceCode, request);
        await using var connection = await OpenAsync(before.TenantId, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(Command("SELECT pg_advisory_xact_lock(hashtextextended(@Value,0));",
            new { Value = "property-valuation-operation:" + before.TenantId + ":" + before.ActorId + ":" + operationId },
            transaction, ct)).ConfigureAwait(false);
        var replay = await connection.QuerySingleOrDefaultAsync<OperationRow>(Command("""
            SELECT valuation_id AS ValuationId,action AS Action,request_fingerprint AS RequestFingerprint
            FROM application_case.property_valuation_operations
            WHERE tenant_id=@Tenant AND valuer_actor_id=@Actor AND operation_id=@Operation;
            """, new { Tenant = before.TenantId, Actor = before.ActorId, Operation = operationId }, transaction, ct))
            .ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.ValuationId != valuationId || replay.Action != action ||
                replay.RequestFingerprint != fingerprint)
                throw new ApplicationCaseException("OPERATION_REUSE_CONFLICT", 409);
            var replayed = await AssignedAsync(connection, transaction, before, valuationId, false, ct)
                .ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return await ViewAsync(connection, null, before.TenantId, replayed, ct).ConfigureAwait(false);
        }

        var row = await AssignedAsync(connection, transaction, before, valuationId, true, ct)
            .ConfigureAwait(false);
        if (row.Version != expectedVersion)
            throw new ApplicationCaseException("PROPERTY_VALUATION_VERSION_CHANGED", 409);
        var authority = await confirm(row.CustomerId, ct).ConfigureAwait(false);
        PropertyValuationPolicy.RequireCurrent(before, authority);
        var now = clock.GetUtcNow();
        int? valuationRevision = null;
        if (action == "EVIDENCE_INSPECTION")
        {
            var changed = await connection.ExecuteAsync(Command("""
                UPDATE application_case.property_valuation_evidence
                SET inspected_at=@Now,inspected_by=@Actor
                WHERE tenant_id=@Tenant AND valuation_id=@Valuation AND evidence_code=@Evidence
                  AND assigned_version=current_version AND inspected_at IS NULL;
                """, new { Now = now, Actor = before.ActorId, Tenant = before.TenantId,
                    Valuation = valuationId, Evidence = evidenceCode }, transaction, ct)).ConfigureAwait(false);
            if (changed != 1)
            {
                var exists = await connection.ExecuteScalarAsync<bool>(Command("""
                    SELECT EXISTS(SELECT 1 FROM application_case.property_valuation_evidence
                        WHERE tenant_id=@Tenant AND valuation_id=@Valuation AND evidence_code=@Evidence);
                    """, new { Tenant = before.TenantId, Valuation = valuationId, Evidence = evidenceCode },
                    transaction, ct)).ConfigureAwait(false);
                throw new ApplicationCaseException(exists ? "PROPERTY_EVIDENCE_VERSION_CHANGED" :
                    "PROPERTY_EVIDENCE_NOT_FOUND", exists ? 409 : 404);
            }
        }
        else
        {
            var incomplete = await connection.ExecuteScalarAsync<bool>(Command("""
                SELECT EXISTS(SELECT 1 FROM application_case.property_valuation_evidence
                    WHERE tenant_id=@Tenant AND valuation_id=@Valuation
                      AND (inspected_at IS NULL OR assigned_version<>current_version));
                """, new { Tenant = before.TenantId, Valuation = valuationId }, transaction, ct))
                .ConfigureAwait(false);
            if (incomplete) throw new ApplicationCaseException("PROPERTY_INSPECTION_INCOMPLETE", 409);
            valuationRevision = await connection.ExecuteScalarAsync<int>(Command("""
                SELECT COALESCE(max(revision),0)+1
                FROM application_case.property_valuation_revisions
                WHERE tenant_id=@Tenant AND valuation_id=@Valuation;
                """, new { Tenant = before.TenantId, Valuation = valuationId }, transaction, ct))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(Command("""
                INSERT INTO application_case.property_valuation_revisions(
                    tenant_id,valuation_id,revision,market_value,currency,effective_date,basis,findings,
                    recorded_at,recorded_by,policy_version,subject_version,customer_version,consent_version)
                VALUES(@Tenant,@Valuation,@Revision,@MarketValue,@Currency,@EffectiveDate,@Basis,@Findings,
                    @Now,@Actor,@PolicyVersion,@SubjectVersion,@CustomerVersion,@ConsentVersion);
                INSERT INTO application_case.property_valuation_revision_evidence(
                    tenant_id,valuation_id,revision,evidence_code,owner_service,resource_reference,
                    source_version,recorded_by,recorded_at)
                SELECT tenant_id,valuation_id,@Revision,evidence_code,owner_service,resource_reference,
                    current_version,@Actor,@Now
                FROM application_case.property_valuation_evidence
                WHERE tenant_id=@Tenant AND valuation_id=@Valuation ORDER BY sequence;
                """, new { Tenant = before.TenantId, Valuation = valuationId, Revision = valuationRevision,
                    request!.MarketValue, request.Currency, request.EffectiveDate, request.Basis, request.Findings,
                    Now = now, Actor = before.ActorId, authority.PolicyVersion, authority.SubjectVersion,
                    authority.CustomerVersion, authority.ConsentVersion }, transaction, ct)).ConfigureAwait(false);
        }
        var nextVersion = expectedVersion + 1;
        var state = action == "VALUATION" ? "Valued" : "Inspection in progress";
        var updated = await connection.ExecuteAsync(Command("""
            UPDATE application_case.property_valuation_assignments
            SET state=@State,version=@Version
            WHERE tenant_id=@Tenant AND valuation_id=@Valuation AND assigned_valuer_actor_id=@Actor
              AND version=@ExpectedVersion;
            """, new { State = state, Version = nextVersion, Tenant = before.TenantId,
                Valuation = valuationId, Actor = before.ActorId, ExpectedVersion = expectedVersion },
                transaction, ct)).ConfigureAwait(false);
        if (updated != 1) throw new ApplicationCaseException("PROPERTY_VALUATION_VERSION_CHANGED", 409);
        await connection.ExecuteAsync(Command("""
            INSERT INTO application_case.property_valuation_events(
                event_id,tenant_id,valuation_id,valuer_actor_id,action,evidence_code,valuation_revision,
                version,policy_version,subject_version,customer_version,consent_version,occurred_at)
            VALUES(@Event,@Tenant,@Valuation,@Actor,@Action,@Evidence,@Revision,@Version,
                @PolicyVersion,@SubjectVersion,@CustomerVersion,@ConsentVersion,@Now);
            INSERT INTO application_case.property_valuation_operations(
                tenant_id,valuer_actor_id,operation_id,valuation_id,action,request_fingerprint)
            VALUES(@Tenant,@Actor,@Operation,@Valuation,@Action,@Fingerprint);
            """, new { Event = Guid.NewGuid(), Tenant = before.TenantId, Valuation = valuationId,
                Actor = before.ActorId, Action = action, Evidence = evidenceCode, Revision = valuationRevision,
                Version = nextVersion, authority.PolicyVersion, authority.SubjectVersion, Fingerprint = fingerprint,
                authority.CustomerVersion, authority.ConsentVersion, Now = now, Operation = operationId },
                transaction, ct)).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        var current = await AssignedAsync(connection, null, before, valuationId, false, ct).ConfigureAwait(false);
        return await ViewAsync(connection, null, before.TenantId, current, ct).ConfigureAwait(false);
    }

    private static async Task<AssignmentRow> AssignedAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, PropertyValuerContext context, string valuationId,
        bool locked, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<AssignmentRow>(Command($"""
            SELECT valuation_id AS ValuationId,property_reference AS PropertyReference,
                property_label AS PropertyLabel,customer_id AS CustomerId,state AS State,
                version AS Version,assigned_at AS AssignedAt
            FROM application_case.property_valuation_assignments
            WHERE tenant_id=@Tenant AND valuation_id=@Valuation AND assigned_valuer_actor_id=@Actor
            {(locked ? "FOR UPDATE" : "")};
            """, new { Tenant = context.TenantId, Valuation = valuationId, Actor = context.ActorId },
                transaction, ct)).ConfigureAwait(false);
        return row ?? throw new ApplicationCaseException("PROPERTY_VALUATION_NOT_FOUND", 404);
    }

    private static async Task<PropertyValuationView> ViewAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, string tenant, AssignmentRow row, CancellationToken ct)
    {
        var evidence = (await connection.QueryAsync<EvidenceRow>(Command("""
            SELECT evidence_code AS Code,label AS Label,owner_service AS Owner,
                resource_reference AS Reference,assigned_version AS AssignedVersion,
                current_version AS CurrentVersion,inspected_at AS InspectedAt
            FROM application_case.property_valuation_evidence
            WHERE tenant_id=@Tenant AND valuation_id=@Valuation ORDER BY sequence;
            """, new { Tenant = tenant, Valuation = row.ValuationId }, transaction, ct))
            .ConfigureAwait(false)).ToArray();
        var revisions = (await connection.QueryAsync<RevisionRow>(Command("""
            SELECT revision AS Revision,market_value AS MarketValue,currency AS Currency,
                effective_date AS EffectiveDate,basis AS Basis,findings AS Findings,
                recorded_at AS RecordedAt,recorded_by AS RecordedBy,policy_version AS PolicyVersion,
                subject_version AS SubjectVersion,customer_version AS CustomerVersion,
                consent_version AS ConsentVersion
            FROM application_case.property_valuation_revisions
            WHERE tenant_id=@Tenant AND valuation_id=@Valuation ORDER BY revision;
            """, new { Tenant = tenant, Valuation = row.ValuationId }, transaction, ct))
            .ConfigureAwait(false)).ToArray();
        var snapshots = (await connection.QueryAsync<SnapshotRow>(Command("""
            SELECT revision AS Revision,evidence_code AS Code,owner_service AS Owner,
                resource_reference AS Reference,source_version AS Version
            FROM application_case.property_valuation_revision_evidence
            WHERE tenant_id=@Tenant AND valuation_id=@Valuation ORDER BY revision,evidence_code;
            """, new { Tenant = tenant, Valuation = row.ValuationId }, transaction, ct))
            .ConfigureAwait(false)).ToArray();
        return new(row.ValuationId, row.PropertyReference, row.PropertyLabel, row.State, row.Version,
            row.AssignedAt, evidence.Select(value => new PropertyEvidenceView(value.Code, value.Label,
                value.Owner, value.Reference, value.AssignedVersion, value.CurrentVersion,
                value.InspectedAt is not null, value.InspectedAt)).ToArray(),
            revisions.Select(value => new PropertyValuationRevisionView(value.Revision, value.MarketValue,
                value.Currency, value.EffectiveDate, value.Basis, value.Findings, value.RecordedAt,
                value.RecordedBy, value.PolicyVersion, value.SubjectVersion, value.CustomerVersion,
                value.ConsentVersion, snapshots.Where(item => item.Revision == value.Revision)
                    .Select(item => new PropertyValuationEvidenceSnapshot(item.Code, item.Owner,
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
                  AND has_table_privilege(current_user,'application_case.property_valuation_assignments','SELECT')
                  AND has_column_privilege(current_user,'application_case.property_valuation_assignments','state','UPDATE')
                  AND has_column_privilege(current_user,'application_case.property_valuation_assignments','version','UPDATE')
                  AND has_table_privilege(current_user,'application_case.property_valuation_evidence','SELECT')
                  AND has_column_privilege(current_user,'application_case.property_valuation_evidence','inspected_at','UPDATE')
                  AND has_column_privilege(current_user,'application_case.property_valuation_evidence','inspected_by','UPDATE')
                  AND has_table_privilege(current_user,'application_case.property_valuation_revisions','SELECT,INSERT')
                  AND has_table_privilege(current_user,'application_case.property_valuation_revision_evidence','SELECT,INSERT')
                  AND has_table_privilege(current_user,'application_case.property_valuation_events','SELECT,INSERT')
                  AND has_table_privilege(current_user,'application_case.property_valuation_operations','SELECT,INSERT')
                  AND NOT has_table_privilege(current_user,'application_case.property_valuation_assignments','INSERT,DELETE,TRUNCATE')
                  AND NOT has_column_privilege(current_user,'application_case.property_valuation_assignments','customer_id','UPDATE')
                  AND NOT has_column_privilege(current_user,'application_case.property_valuation_assignments','assigned_valuer_actor_id','UPDATE')
                  AND NOT has_table_privilege(current_user,'application_case.property_valuation_evidence','INSERT,DELETE,TRUNCATE')
                  AND NOT has_column_privilege(current_user,'application_case.property_valuation_evidence','current_version','UPDATE')
                  AND NOT has_table_privilege(current_user,'application_case.property_valuation_revisions','UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'application_case.property_valuation_revision_evidence','UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'application_case.property_valuation_events','UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'application_case.property_valuation_operations','UPDATE,DELETE,TRUNCATE');
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

    private static CustomerPropertyValuationView CustomerView(PropertyValuationView row)
    {
        var latest = row.Revisions[^1];
        return new(row.ValuationId, row.PropertyReference, row.PropertyLabel, latest.Revision,
            latest.MarketValue, latest.Currency, latest.EffectiveDate, latest.Basis, latest.Findings,
            latest.RecordedAt, latest.RecordedBy, latest.Evidence.Count, row.DataDurability);
    }

    private static CommandDefinition Command(string sql, object? values,
        NpgsqlTransaction? transaction, CancellationToken ct) =>
        new(sql, values, transaction, cancellationToken: ct);

    public async ValueTask DisposeAsync()
    {
        foreach (var source in sources.Values) await source.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record AssignmentRow(string ValuationId, string PropertyReference,
        string PropertyLabel, string CustomerId, string State, int Version, DateTimeOffset AssignedAt);
    private sealed record EvidenceRow(string Code, string Label, string Owner, string Reference,
        long AssignedVersion, long CurrentVersion, DateTimeOffset? InspectedAt);
    private sealed record RevisionRow(int Revision, decimal MarketValue, string Currency,
        DateOnly EffectiveDate, string Basis, string Findings, DateTimeOffset RecordedAt,
        string RecordedBy, long PolicyVersion, long SubjectVersion, long CustomerVersion,
        long ConsentVersion);
    private sealed record SnapshotRow(int Revision, string Code, string Owner, string Reference, long Version);
    private sealed record OperationRow(string ValuationId, string Action, string RequestFingerprint);
}
