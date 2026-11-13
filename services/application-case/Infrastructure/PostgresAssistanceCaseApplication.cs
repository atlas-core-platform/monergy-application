using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Monergy.Platform;
using Monergy.Services.ApplicationCase.Application;
using Npgsql;

namespace Monergy.Services.ApplicationCase.Infrastructure;

public sealed class PostgresAssistanceCaseApplication : IAssistanceCaseApplication,
    IHealthCheck, IAsyncDisposable
{
    private const string RequestType = "Financial assistance";
    private const string Durability = "Durable test data";
    private readonly TimeProvider clock;
    private readonly Dictionary<string, NpgsqlDataSource> sources = new(StringComparer.Ordinal);

    public PostgresAssistanceCaseApplication(IConfiguration configuration, TimeProvider clock)
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

    public async Task<AssistanceCaseView> CreateAsync(
        CaseActorContext before,
        CreateAssistanceCase request,
        Func<CancellationToken, Task<CaseActorContext>> confirm,
        CancellationToken ct)
    {
        RequireCustomer(before);
        Operation(request.OperationId);
        await using var connection = await OpenAsync(before.TenantId, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(connection, transaction, OperationKey(before, request.OperationId), ct).ConfigureAwait(false);
        var replay = await ReplayAsync(connection, transaction, before, request.OperationId, ct).ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.Action != "CREATE") throw new ApplicationCaseException("OPERATION_REUSE_CONFLICT", 409);
            var replayed = await RequireCaseAsync(connection, transaction, before.TenantId, replay.CaseId, false, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return View(replayed, before.ActorId);
        }

        await LockAsync(connection, transaction, "customer:" + before.TenantId + ":" + before.CustomerId, ct)
            .ConfigureAwait(false);
        var current = await confirm(ct).ConfigureAwait(false);
        RequireUnchanged(before, current);
        var existing = await connection.QuerySingleOrDefaultAsync<CaseRow>(Command("""
            SELECT case_id AS CaseId,tenant_id AS TenantId,customer_id AS CustomerId,
                customer_actor_id AS CustomerActorId,request_type AS RequestType,state AS State,
                version AS Version,created_at AS CreatedAt,accepted_at AS AcceptedAt,accepted_by AS AcceptedBy
            FROM application_case.assistance_cases
            WHERE tenant_id=@Tenant AND customer_id=@Customer AND state IN ('Submitted','Accepted')
            ORDER BY created_at DESC LIMIT 1 FOR UPDATE;
            """, new { Tenant = before.TenantId, Customer = before.CustomerId }, transaction, ct)).ConfigureAwait(false);
        if (existing is not null)
        {
            await InsertOperationAsync(connection, transaction, before, request.OperationId, existing.CaseId, "CREATE", ct)
                .ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return View(existing, before.ActorId);
        }

        var now = clock.GetUtcNow();
        var created = new CaseRow("case-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)),
            before.TenantId, before.CustomerId!, before.ActorId, RequestType, "Submitted", 1, now, null, null);
        await connection.ExecuteAsync(Command("""
            INSERT INTO application_case.assistance_cases(
                case_id,tenant_id,customer_id,customer_actor_id,request_type,state,version,created_at)
            VALUES(@CaseId,@TenantId,@CustomerId,@CustomerActorId,@RequestType,@State,@Version,@CreatedAt);
            """, created, transaction, ct)).ConfigureAwait(false);
        await InsertOperationAsync(connection, transaction, before, request.OperationId, created.CaseId, "CREATE", ct)
            .ConfigureAwait(false);
        await InsertEventAsync(connection, transaction, created, "Submitted", now, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return View(created, before.ActorId);
    }

    public async Task<IReadOnlyList<AssistanceCaseView>> ListCustomerAsync(
        CaseActorContext context, CancellationToken ct)
    {
        RequireCustomer(context);
        await using var connection = await OpenAsync(context.TenantId, ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<CaseRow>(Command("""
            SELECT case_id AS CaseId,tenant_id AS TenantId,customer_id AS CustomerId,
                customer_actor_id AS CustomerActorId,request_type AS RequestType,state AS State,
                version AS Version,created_at AS CreatedAt,accepted_at AS AcceptedAt,accepted_by AS AcceptedBy
            FROM application_case.assistance_cases
            WHERE tenant_id=@Tenant AND customer_id=@Customer AND customer_actor_id=@Actor
            ORDER BY created_at DESC LIMIT 100;
            """, new { Tenant = context.TenantId, Customer = context.CustomerId, Actor = context.ActorId }, null, ct))
            .ConfigureAwait(false);
        return rows.Select(value => View(value, context.ActorId)).ToArray();
    }

    public async Task<IReadOnlyList<AssistanceCaseView>> ListDsaAsync(
        CaseActorContext context, CancellationToken ct)
    {
        RequireDsa(context);
        await using var connection = await OpenAsync(context.TenantId, ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<CaseRow>(Command("""
            SELECT case_id AS CaseId,tenant_id AS TenantId,customer_id AS CustomerId,
                customer_actor_id AS CustomerActorId,request_type AS RequestType,state AS State,
                version AS Version,created_at AS CreatedAt,accepted_at AS AcceptedAt,accepted_by AS AcceptedBy
            FROM application_case.assistance_cases
            WHERE tenant_id=@Tenant AND (state='Submitted' OR accepted_by=@Actor)
            ORDER BY CASE WHEN state='Submitted' THEN 0 ELSE 1 END,created_at LIMIT 100;
            """, new { Tenant = context.TenantId, Actor = context.ActorId }, null, ct)).ConfigureAwait(false);
        return rows.Select(value => View(value, context.ActorId)).ToArray();
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
        await using var connection = await OpenAsync(before.TenantId, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(connection, transaction, OperationKey(before, request.OperationId), ct).ConfigureAwait(false);
        var replay = await ReplayAsync(connection, transaction, before, request.OperationId, ct).ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.Action != "ACCEPT" || replay.CaseId != caseId)
                throw new ApplicationCaseException("OPERATION_REUSE_CONFLICT", 409);
            var replayed = await RequireCaseAsync(connection, transaction, before.TenantId, caseId, false, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return View(replayed, before.ActorId);
        }

        var row = await RequireCaseAsync(connection, transaction, before.TenantId, caseId, true, ct).ConfigureAwait(false);
        if (row.State != "Submitted") throw new ApplicationCaseException("CASE_ALREADY_ACCEPTED", 409);
        if (row.Version != request.ExpectedVersion) throw new ApplicationCaseException("CASE_VERSION_CHANGED", 409);
        var current = await confirm(ct).ConfigureAwait(false);
        RequireUnchanged(before, current);
        var now = clock.GetUtcNow();
        var accepted = row with { State = "Accepted", Version = row.Version + 1, AcceptedAt = now, AcceptedBy = before.ActorId };
        var changed = await connection.ExecuteAsync(Command("""
            UPDATE application_case.assistance_cases
            SET state='Accepted',version=@Version,accepted_at=@AcceptedAt,accepted_by=@AcceptedBy
            WHERE case_id=@CaseId AND tenant_id=@TenantId AND state='Submitted' AND version=@ExpectedVersion;
            """, new { accepted.Version, accepted.AcceptedAt, accepted.AcceptedBy, accepted.CaseId,
                accepted.TenantId, ExpectedVersion = row.Version }, transaction, ct)).ConfigureAwait(false);
        if (changed != 1) throw new ApplicationCaseException("CASE_VERSION_CHANGED", 409);
        await InsertOperationAsync(connection, transaction, before, request.OperationId, caseId, "ACCEPT", ct)
            .ConfigureAwait(false);
        await InsertEventAsync(connection, transaction, accepted, "Accepted", now, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return View(accepted, before.ActorId);
    }

    public async Task<AssistanceCaseHistory> HistoryAsync(
        string caseId, CaseActorContext context, CancellationToken ct)
    {
        RequireCustomer(context);
        Identifier(caseId, 64, "CASE_ID_INVALID");
        await using var connection = await OpenAsync(context.TenantId, ct).ConfigureAwait(false);
        var row = await RequireCaseAsync(connection, null, context.TenantId, caseId, false, ct).ConfigureAwait(false);
        if (row.CustomerId != context.CustomerId || row.CustomerActorId != context.ActorId)
            throw new ApplicationCaseException("CASE_NOT_FOUND", 404);
        var events = await connection.QueryAsync<CaseEventRow>(Command("""
            SELECT event_id AS EventId,action AS Action,version AS Version,occurred_at AS OccurredAt
            FROM application_case.assistance_case_events
            WHERE tenant_id=@Tenant AND case_id=@Case ORDER BY version;
            """, new { Tenant = context.TenantId, Case = caseId }, null, ct)).ConfigureAwait(false);
        return new(caseId, events.Select(value =>
            new AssistanceCaseEventView(value.EventId, value.Action, value.Version, value.OccurredAt)).ToArray(), Durability);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var tenant in sources.Keys)
            {
                await using var connection = await OpenAsync(tenant, cancellationToken).ConfigureAwait(false);
            }
            return HealthCheckResult.Healthy("Application & Case tenant stores are ready.");
        }
        catch (NpgsqlException)
        {
            return HealthCheckResult.Unhealthy("Application & Case tenant store is unavailable.");
        }
        catch (ApplicationCaseException)
        {
            return HealthCheckResult.Unhealthy("Application & Case tenant store is unavailable.");
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(string tenant, CancellationToken ct)
    {
        if (!sources.TryGetValue(tenant, out var source))
            throw new ApplicationCaseException("CASE_TENANT_UNAVAILABLE", 503);
        var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var valid = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(
                    SELECT 1 FROM application_case.tenant_identity
                    WHERE singleton AND tenant_id=@Tenant AND schema_version=1)
                  AND has_table_privilege(current_user,'application_case.assistance_cases','SELECT,INSERT')
                  AND has_column_privilege(current_user,'application_case.assistance_cases','state','UPDATE')
                  AND has_column_privilege(current_user,'application_case.assistance_cases','version','UPDATE')
                  AND has_column_privilege(current_user,'application_case.assistance_cases','accepted_at','UPDATE')
                  AND has_column_privilege(current_user,'application_case.assistance_cases','accepted_by','UPDATE')
                  AND has_table_privilege(current_user,'application_case.assistance_case_operations','SELECT,INSERT')
                  AND has_table_privilege(current_user,'application_case.assistance_case_events','SELECT,INSERT')
                  AND NOT has_schema_privilege(current_user,'application_case','CREATE')
                  AND NOT has_schema_privilege(current_user,'public','CREATE')
                  AND NOT has_database_privilege(current_user,current_database(),'CREATE')
                  AND NOT has_column_privilege(current_user,'application_case.assistance_cases','customer_id','UPDATE')
                  AND NOT has_table_privilege(current_user,'application_case.assistance_cases','DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'application_case.assistance_case_operations','UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'application_case.assistance_case_events','UPDATE,DELETE,TRUNCATE')
                  AND NOT EXISTS(
                    SELECT 1 FROM pg_roles WHERE rolname=current_user
                    AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication));
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

    private static async Task<CaseRow> RequireCaseAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, string tenant, string caseId, bool locked, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<CaseRow>(Command($"""
            SELECT case_id AS CaseId,tenant_id AS TenantId,customer_id AS CustomerId,
                customer_actor_id AS CustomerActorId,request_type AS RequestType,state AS State,
                version AS Version,created_at AS CreatedAt,accepted_at AS AcceptedAt,accepted_by AS AcceptedBy
            FROM application_case.assistance_cases
            WHERE tenant_id=@Tenant AND case_id=@Case{(locked ? " FOR UPDATE" : "")};
            """, new { Tenant = tenant, Case = caseId }, transaction, ct)).ConfigureAwait(false);
        return row ?? throw new ApplicationCaseException("CASE_NOT_FOUND", 404);
    }

    private static Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string value, CancellationToken ct) => connection.ExecuteAsync(Command(
            "SELECT pg_advisory_xact_lock(hashtextextended(@Value,0));",
            new { Value = value }, transaction, ct));

    private static Task<OperationRow?> ReplayAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CaseActorContext context, string operation, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<OperationRow>(Command("""
            SELECT case_id AS CaseId,action AS Action
            FROM application_case.assistance_case_operations
            WHERE tenant_id=@Tenant AND actor_id=@Actor AND operation_id=@Operation;
            """, new { Tenant = context.TenantId, Actor = context.ActorId, Operation = operation }, transaction, ct));

    private static Task InsertOperationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CaseActorContext context, string operation, string caseId, string action, CancellationToken ct) =>
        connection.ExecuteAsync(Command("""
            INSERT INTO application_case.assistance_case_operations(
                tenant_id,actor_id,operation_id,case_id,action)
            VALUES(@Tenant,@Actor,@Operation,@Case,@Action);
            """, new { Tenant = context.TenantId, Actor = context.ActorId, Operation = operation,
                Case = caseId, Action = action }, transaction, ct));

    private static Task InsertEventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CaseRow row, string action, DateTimeOffset occurredAt, CancellationToken ct) =>
        connection.ExecuteAsync(Command("""
            INSERT INTO application_case.assistance_case_events(
                event_id,case_id,tenant_id,action,version,occurred_at)
            VALUES(@EventId,@CaseId,@TenantId,@Action,@Version,@OccurredAt);
            """, new { EventId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), row.CaseId,
                row.TenantId, Action = action, row.Version, OccurredAt = occurredAt }, transaction, ct));

    private static AssistanceCaseView View(CaseRow value, string actor) =>
        new(value.CaseId, value.RequestType, value.State, value.Version, value.CreatedAt,
            value.AcceptedAt, value.AcceptedBy == actor, Durability);

    private static void RequireCustomer(CaseActorContext context)
    {
        if (context.CapabilityId != AssistanceCaseApplication.CustomerCapability || context.CustomerId is null)
            throw new ApplicationCaseException("CUSTOMER_CASE_ACCESS_REQUIRED", 403);
        Identifier(context.CustomerId, 128, "CUSTOMER_CONTEXT_INVALID");
    }

    private static void RequireDsa(CaseActorContext context)
    {
        if (context.CapabilityId != AssistanceCaseApplication.DsaCapability || context.CustomerId is not null)
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

    private static string OperationKey(CaseActorContext context, string operation) =>
        "operation:" + context.TenantId + ":" + context.ActorId + ":" + operation;

    private static CommandDefinition Command(string sql, object? values,
        NpgsqlTransaction? transaction, CancellationToken ct) =>
        new(sql, values, transaction, cancellationToken: ct);

    public async ValueTask DisposeAsync()
    {
        foreach (var source in sources.Values) await source.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record CaseRow(string CaseId, string TenantId, string CustomerId,
        string CustomerActorId, string RequestType, string State, int Version,
        DateTimeOffset CreatedAt, DateTimeOffset? AcceptedAt, string? AcceptedBy);
    private sealed record OperationRow(string CaseId, string Action);
    private sealed record CaseEventRow(string EventId, string Action, int Version, DateTimeOffset OccurredAt);
}
