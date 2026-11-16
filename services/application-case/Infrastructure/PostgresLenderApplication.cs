using Dapper;
using Monergy.Platform;
using Monergy.Services.ApplicationCase.Application;
using Npgsql;

namespace Monergy.Services.ApplicationCase.Infrastructure;

public sealed class PostgresLenderApplication : ILenderApplication, IAsyncDisposable
{
    private const string Durability = "Durable test data";
    private readonly TimeProvider clock;
    private readonly Dictionary<string, NpgsqlDataSource> sources = new(StringComparer.Ordinal);

    public PostgresLenderApplication(IConfiguration configuration, TimeProvider clock)
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

    public async Task<IReadOnlyList<LenderApplicationView>> ListAsync(LenderContext context, CancellationToken ct)
    {
        await using var connection = await OpenAsync(context.TenantId, ct).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<ApplicationRow>(Command("""
            SELECT application_id AS ApplicationId,application_reference AS ApplicationReference,
                state AS State,version AS Version,assigned_at AS AssignedAt,
                decision AS Decision,decision_reason AS DecisionReason,decided_at AS DecidedAt
            FROM application_case.lender_applications
            WHERE tenant_id=@Tenant AND assigned_lender_actor_id=@Actor
            ORDER BY assigned_at,application_id;
            """, new { Tenant = context.TenantId, Actor = context.ActorId }, null, ct)).ConfigureAwait(false)).ToArray();
        var result = new List<LenderApplicationView>(rows.Length);
        foreach (var row in rows)
            result.Add(await ViewAsync(connection, null, context.TenantId, row, ct).ConfigureAwait(false));
        return result;
    }

    public Task<LenderApplicationView> ReviewDocumentAsync(
        string applicationId, string documentCode, LenderContext before, ReviewLenderDocument request,
        Func<CancellationToken, Task<LenderContext>> confirm, CancellationToken ct) =>
        MutateAsync(applicationId, before, request.OperationId, request.ExpectedVersion,
            "DOCUMENT_REVIEW", LenderApplicationPolicy.Identifier(documentCode, "LENDER_DOCUMENT_CODE_INVALID", 64),
            null, null, confirm, ct);

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
        await using var connection = await OpenAsync(before.TenantId, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(Command(
            "SELECT pg_advisory_xact_lock(hashtextextended(@Value,0));",
            new { Value = "lender-operation:" + before.TenantId + ":" + before.ActorId + ":" + operationId },
            transaction, ct)).ConfigureAwait(false);
        var replay = await connection.QuerySingleOrDefaultAsync<OperationRow>(Command("""
            SELECT application_id AS ApplicationId,action AS Action
            FROM application_case.lender_application_operations
            WHERE tenant_id=@Tenant AND lender_actor_id=@Actor AND operation_id=@Operation;
            """, new { Tenant = before.TenantId, Actor = before.ActorId, Operation = operationId }, transaction, ct))
            .ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.ApplicationId != applicationId || replay.Action != action)
                throw new ApplicationCaseException("OPERATION_REUSE_CONFLICT", 409);
            var replayed = await AssignedAsync(connection, transaction, before, applicationId, false, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return await ViewAsync(connection, null, before.TenantId, replayed, ct).ConfigureAwait(false);
        }

        var row = await AssignedAsync(connection, transaction, before, applicationId, true, ct).ConfigureAwait(false);
        if (row.Version != expectedVersion)
            throw new ApplicationCaseException("LENDER_APPLICATION_VERSION_CHANGED", 409);
        if (row.Decision is not null)
            throw new ApplicationCaseException("LENDER_APPLICATION_ALREADY_DECIDED", 409);
        LenderApplicationPolicy.RequireCurrent(before, await confirm(ct).ConfigureAwait(false));
        var now = clock.GetUtcNow();
        if (action == "DOCUMENT_REVIEW")
        {
            var changed = await connection.ExecuteAsync(Command("""
                UPDATE application_case.lender_application_documents
                SET reviewed_at=@Now,reviewed_by=@Actor
                WHERE tenant_id=@Tenant AND application_id=@Application AND document_code=@Document
                  AND present AND reviewed_at IS NULL;
                """, new { Now = now, Actor = before.ActorId, Tenant = before.TenantId,
                    Application = applicationId, Document = documentCode }, transaction, ct)).ConfigureAwait(false);
            if (changed != 1) throw new ApplicationCaseException("LENDER_DOCUMENT_UNAVAILABLE", 409);
        }
        else
        {
            var incomplete = await connection.ExecuteScalarAsync<bool>(Command("""
                SELECT EXISTS(
                    SELECT 1 FROM application_case.lender_application_documents
                    WHERE tenant_id=@Tenant AND application_id=@Application
                      AND (NOT present OR reviewed_at IS NULL));
                """, new { Tenant = before.TenantId, Application = applicationId }, transaction, ct)).ConfigureAwait(false);
            if (incomplete) throw new ApplicationCaseException("LENDER_DOCUMENT_REVIEW_INCOMPLETE", 409);
        }
        var nextVersion = expectedVersion + 1;
        var state = action == "DECISION" ? "Decided" : "Under review";
        var updated = await connection.ExecuteAsync(Command("""
            UPDATE application_case.lender_applications
            SET state=@State,version=@Version,
                decision=CASE WHEN @Action='DECISION' THEN @Decision ELSE decision END,
                decision_reason=CASE WHEN @Action='DECISION' THEN @Reason ELSE decision_reason END,
                decided_at=CASE WHEN @Action='DECISION' THEN @Now ELSE decided_at END,
                decided_by=CASE WHEN @Action='DECISION' THEN @Actor ELSE decided_by END
            WHERE tenant_id=@Tenant AND application_id=@Application
              AND assigned_lender_actor_id=@Actor AND version=@ExpectedVersion;
            """, new { State = state, Version = nextVersion, Action = action, Decision = decision,
                Reason = reason, Now = now, Actor = before.ActorId, Tenant = before.TenantId,
                Application = applicationId, ExpectedVersion = expectedVersion }, transaction, ct)).ConfigureAwait(false);
        if (updated != 1) throw new ApplicationCaseException("LENDER_APPLICATION_VERSION_CHANGED", 409);
        await connection.ExecuteAsync(Command("""
            INSERT INTO application_case.lender_application_events(
                event_id,tenant_id,application_id,lender_actor_id,action,document_code,
                decision,reason,version,occurred_at)
            VALUES(@Event,@Tenant,@Application,@Actor,@Action,@Document,@Decision,@Reason,@Version,@Now);
            INSERT INTO application_case.lender_application_operations(
                tenant_id,lender_actor_id,operation_id,application_id,action)
            VALUES(@Tenant,@Actor,@Operation,@Application,@Action);
            """, new { Event = Guid.NewGuid(), Tenant = before.TenantId, Application = applicationId,
                Actor = before.ActorId, Action = action, Document = documentCode, Decision = decision,
                Reason = reason, Version = nextVersion, Now = now, Operation = operationId }, transaction, ct))
            .ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        var current = await AssignedAsync(connection, null, before, applicationId, false, ct).ConfigureAwait(false);
        return await ViewAsync(connection, null, before.TenantId, current, ct).ConfigureAwait(false);
    }

    private static async Task<ApplicationRow> AssignedAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, LenderContext context, string applicationId, bool locked, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ApplicationRow>(Command($"""
            SELECT application_id AS ApplicationId,application_reference AS ApplicationReference,
                state AS State,version AS Version,assigned_at AS AssignedAt,
                decision AS Decision,decision_reason AS DecisionReason,decided_at AS DecidedAt
            FROM application_case.lender_applications
            WHERE tenant_id=@Tenant AND application_id=@Application AND assigned_lender_actor_id=@Actor
            {(locked ? "FOR UPDATE" : "")};
            """, new { Tenant = context.TenantId, Application = applicationId, Actor = context.ActorId }, transaction, ct))
            .ConfigureAwait(false);
        return row ?? throw new ApplicationCaseException("LENDER_APPLICATION_NOT_FOUND", 404);
    }

    private static async Task<LenderApplicationView> ViewAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, string tenantId, ApplicationRow row, CancellationToken ct)
    {
        var documents = (await connection.QueryAsync<DocumentRow>(Command("""
            SELECT document_code AS Code,label AS Label,present AS Present,reviewed_at AS ReviewedAt
            FROM application_case.lender_application_documents
            WHERE tenant_id=@Tenant AND application_id=@Application ORDER BY sequence;
            """, new { Tenant = tenantId, Application = row.ApplicationId }, transaction, ct)).ConfigureAwait(false)).ToArray();
        var decision = row.Decision is null ? null : new LenderDecisionView(
            row.Decision, row.DecisionReason ?? throw new ApplicationCaseException("LENDER_HISTORY_INVALID", 503),
            row.DecidedAt ?? throw new ApplicationCaseException("LENDER_HISTORY_INVALID", 503));
        return new(row.ApplicationId, row.ApplicationReference, row.State, row.Version, row.AssignedAt,
            documents.Select(value => new LenderDocumentView(value.Code, value.Label, value.Present,
                value.ReviewedAt is not null, value.ReviewedAt)).ToArray(), decision, Durability);
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
                SELECT EXISTS(
                    SELECT 1 FROM application_case.tenant_identity
                    WHERE singleton AND tenant_id=@Tenant AND schema_version=6)
                  AND has_table_privilege(current_user,'application_case.lender_applications','SELECT')
                  AND has_column_privilege(current_user,'application_case.lender_applications','state','UPDATE')
                  AND has_column_privilege(current_user,'application_case.lender_applications','version','UPDATE')
                  AND has_column_privilege(current_user,'application_case.lender_applications','decision','UPDATE')
                  AND has_column_privilege(current_user,'application_case.lender_applications','decision_reason','UPDATE')
                  AND has_column_privilege(current_user,'application_case.lender_applications','decided_at','UPDATE')
                  AND has_column_privilege(current_user,'application_case.lender_applications','decided_by','UPDATE')
                  AND has_table_privilege(current_user,'application_case.lender_application_documents','SELECT')
                  AND has_column_privilege(current_user,'application_case.lender_application_documents','reviewed_at','UPDATE')
                  AND has_column_privilege(current_user,'application_case.lender_application_documents','reviewed_by','UPDATE')
                  AND has_table_privilege(current_user,'application_case.lender_application_events','SELECT,INSERT')
                  AND has_table_privilege(current_user,'application_case.lender_application_operations','SELECT,INSERT')
                  AND NOT has_table_privilege(current_user,'application_case.lender_applications','INSERT,DELETE,TRUNCATE')
                  AND NOT has_column_privilege(current_user,'application_case.lender_applications','assigned_lender_actor_id','UPDATE')
                  AND NOT has_table_privilege(current_user,'application_case.lender_application_documents','INSERT,DELETE,TRUNCATE')
                  AND NOT has_column_privilege(current_user,'application_case.lender_application_documents','present','UPDATE')
                  AND NOT has_column_privilege(current_user,'application_case.lender_application_documents','label','UPDATE')
                  AND NOT has_column_privilege(current_user,'application_case.lender_application_documents','document_code','UPDATE')
                  AND NOT has_table_privilege(current_user,'application_case.lender_application_events','UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'application_case.lender_application_operations','UPDATE,DELETE,TRUNCATE');
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

    private static CommandDefinition Command(string sql, object? values,
        NpgsqlTransaction? transaction, CancellationToken ct) =>
        new(sql, values, transaction, cancellationToken: ct);

    public async ValueTask DisposeAsync()
    {
        foreach (var source in sources.Values) await source.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record ApplicationRow(string ApplicationId, string ApplicationReference, string State,
        int Version, DateTime AssignedAt, string? Decision, string? DecisionReason, DateTime? DecidedAt);
    private sealed record DocumentRow(string Code, string Label, bool Present, DateTime? ReviewedAt);
    private sealed record OperationRow(string ApplicationId, string Action);
}
