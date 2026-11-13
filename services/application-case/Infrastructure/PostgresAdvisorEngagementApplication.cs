using System.Security.Cryptography;
using Dapper;
using Monergy.Platform;
using Monergy.Services.ApplicationCase.Application;
using Npgsql;

namespace Monergy.Services.ApplicationCase.Infrastructure;

public sealed class PostgresAdvisorEngagementApplication : IAdvisorEngagementApplication, IAsyncDisposable
{
    private const string Durability = "Durable test data";
    private readonly TimeProvider clock;
    private readonly Dictionary<string, NpgsqlDataSource> sources = new(StringComparer.Ordinal);

    public PostgresAdvisorEngagementApplication(IConfiguration configuration, TimeProvider clock)
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

    public async Task<AdvisorEngagementView> ReadAsync(AdvisorEngagementContext context, CancellationToken ct)
    {
        await using var connection = await OpenAsync(context.TenantId, ct).ConfigureAwait(false);
        return await ReadAsync(connection, null, context, false, ct).ConfigureAwait(false);
    }

    public Task<AdvisorEngagementView> AddNoteAsync(
        AdvisorEngagementContext before,
        AddAdvisorNote request,
        Func<CancellationToken, Task<AdvisorEngagementContext>> confirm,
        CancellationToken ct) =>
        MutateAsync(before, request.OperationId, request.ExpectedVersion, "NOTE",
            AdvisorEngagementPolicy.Text(request.Note, "ADVISOR_NOTE_INVALID"),
            AdvisorEngagementPolicy.Sources(request.SourceFactIds), null, confirm, ct);

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
        IReadOnlyList<string> sourceFactIds,
        string? requestedStage,
        Func<CancellationToken, Task<AdvisorEngagementContext>> confirm,
        CancellationToken ct)
    {
        AdvisorEngagementPolicy.Operation(operationId);
        AdvisorEngagementPolicy.ExpectedVersion(expectedVersion);
        await using var connection = await OpenAsync(before.TenantId, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(Command(
            "SELECT pg_advisory_xact_lock(hashtextextended(@Value,0));",
            new { Value = "advisor-operation:" + before.TenantId + ":" + before.ActorId + ":" + operationId },
            transaction, ct)).ConfigureAwait(false);

        var replay = await connection.QuerySingleOrDefaultAsync<OperationRow>(Command("""
            SELECT customer_id AS CustomerId,action AS Action
            FROM application_case.advisor_engagement_operations
            WHERE tenant_id=@Tenant AND advisor_actor_id=@Actor AND operation_id=@Operation;
            """, new { Tenant = before.TenantId, Actor = before.ActorId, Operation = operationId }, transaction, ct))
            .ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.CustomerId != before.CustomerId || replay.Action != action)
                throw new ApplicationCaseException("OPERATION_REUSE_CONFLICT", 409);
            var replayed = await ReadAsync(connection, transaction, before, false, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return replayed;
        }

        var current = await connection.QuerySingleOrDefaultAsync<EngagementRow>(Command("""
            SELECT tenant_id AS TenantId,advisor_actor_id AS AdvisorActorId,customer_id AS CustomerId,
                version AS Version,application_stage AS ApplicationStage,updated_at AS UpdatedAt
            FROM application_case.advisor_engagements
            WHERE tenant_id=@Tenant AND advisor_actor_id=@Actor AND customer_id=@Customer
            FOR UPDATE;
            """, new { Tenant = before.TenantId, Actor = before.ActorId, Customer = before.CustomerId }, transaction, ct))
            .ConfigureAwait(false);
        if ((current?.Version ?? 0) != expectedVersion)
            throw new ApplicationCaseException("ADVISOR_ENGAGEMENT_VERSION_CHANGED", 409);
        if (expectedVersion >= 200)
            throw new ApplicationCaseException("ADVISOR_ENGAGEMENT_LIMIT_REACHED", 409);
        AdvisorEngagementPolicy.RequireCurrent(before, await confirm(ct).ConfigureAwait(false));

        var now = clock.GetUtcNow();
        var version = expectedVersion + 1;
        var stage = current?.ApplicationStage ?? AdvisorEngagementPolicy.InitialStage;
        if (action == "APPLICATION_STAGE")
            stage = AdvisorEngagementPolicy.NextStage(stage, requestedStage ?? "");
        if (current is null)
        {
            await connection.ExecuteAsync(Command("""
                INSERT INTO application_case.advisor_engagements(
                    tenant_id,advisor_actor_id,customer_id,version,application_stage,created_at,updated_at)
                VALUES(@Tenant,@Actor,@Customer,@Version,@Stage,@Now,@Now);
                """, new { Tenant = before.TenantId, Actor = before.ActorId, Customer = before.CustomerId,
                    Version = version, Stage = stage, Now = now }, transaction, ct)).ConfigureAwait(false);
        }
        else
        {
            var changed = await connection.ExecuteAsync(Command("""
                UPDATE application_case.advisor_engagements
                SET version=@Version,application_stage=@Stage,updated_at=@Now
                WHERE tenant_id=@Tenant AND advisor_actor_id=@Actor AND customer_id=@Customer
                  AND version=@ExpectedVersion;
                """, new { Tenant = before.TenantId, Actor = before.ActorId, Customer = before.CustomerId,
                    Version = version, Stage = stage, Now = now, ExpectedVersion = expectedVersion }, transaction, ct))
                .ConfigureAwait(false);
            if (changed != 1)
                throw new ApplicationCaseException("ADVISOR_ENGAGEMENT_VERSION_CHANGED", 409);
        }

        await connection.ExecuteAsync(Command("""
            INSERT INTO application_case.advisor_engagement_activities(
                activity_id,tenant_id,advisor_actor_id,customer_id,kind,body,source_fact_ids,
                application_stage,version,occurred_at)
            VALUES(@ActivityId,@Tenant,@Actor,@Customer,@Kind,@Body,@Sources,@ApplicationStage,@Version,@Now);
            """, new
        {
            ActivityId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            Tenant = before.TenantId,
            Actor = before.ActorId,
            Customer = before.CustomerId,
            Kind = action,
            Body = body,
            Sources = sourceFactIds.ToArray(),
            ApplicationStage = action == "APPLICATION_STAGE" ? stage : null,
            Version = version,
            Now = now,
        }, transaction, ct)).ConfigureAwait(false);
        await connection.ExecuteAsync(Command("""
            INSERT INTO application_case.advisor_engagement_operations(
                tenant_id,advisor_actor_id,operation_id,customer_id,action)
            VALUES(@Tenant,@Actor,@Operation,@Customer,@Action);
            """, new { Tenant = before.TenantId, Actor = before.ActorId, Operation = operationId,
                Customer = before.CustomerId, Action = action }, transaction, ct)).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return await ReadAsync(before, ct).ConfigureAwait(false);
    }

    private static async Task<AdvisorEngagementView> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        AdvisorEngagementContext context,
        bool locked,
        CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<EngagementRow>(Command($"""
            SELECT tenant_id AS TenantId,advisor_actor_id AS AdvisorActorId,customer_id AS CustomerId,
                version AS Version,application_stage AS ApplicationStage,updated_at AS UpdatedAt
            FROM application_case.advisor_engagements
            WHERE tenant_id=@Tenant AND advisor_actor_id=@Actor AND customer_id=@Customer
            {(locked ? "FOR UPDATE" : "")};
            """, new { Tenant = context.TenantId, Actor = context.ActorId, Customer = context.CustomerId }, transaction, ct))
            .ConfigureAwait(false);
        if (row is null)
            return new(context.CustomerId, 0, AdvisorEngagementPolicy.InitialStage, null, [], Durability);
        var activities = (await connection.QueryAsync<ActivityRow>(Command("""
            SELECT activity_id AS ActivityId,kind AS Kind,body AS Body,source_fact_ids AS SourceFactIds,
                application_stage AS ApplicationStage,version AS Version,occurred_at AS OccurredAt
            FROM application_case.advisor_engagement_activities
            WHERE tenant_id=@Tenant AND advisor_actor_id=@Actor AND customer_id=@Customer
            ORDER BY version;
            """, new { Tenant = context.TenantId, Actor = context.ActorId, Customer = context.CustomerId }, transaction, ct))
            .ConfigureAwait(false)).ToArray();
        if (activities.Length != row.Version || activities.Where((value, index) => value.Version != index + 1).Any())
            throw new ApplicationCaseException("ADVISOR_ENGAGEMENT_HISTORY_INVALID", 503);
        return new(context.CustomerId, row.Version, row.ApplicationStage, row.UpdatedAt,
            activities.Select(value => new AdvisorActivityView(value.ActivityId, value.Kind, value.Body,
                value.SourceFactIds, value.ApplicationStage, value.Version, value.OccurredAt)).ToArray(), Durability);
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
                    WHERE singleton AND tenant_id=@Tenant AND schema_version=2)
                  AND has_table_privilege(current_user,'application_case.advisor_engagements','SELECT,INSERT')
                  AND has_column_privilege(current_user,'application_case.advisor_engagements','version','UPDATE')
                  AND has_column_privilege(current_user,'application_case.advisor_engagements','application_stage','UPDATE')
                  AND has_column_privilege(current_user,'application_case.advisor_engagements','updated_at','UPDATE')
                  AND has_table_privilege(current_user,'application_case.advisor_engagement_activities','SELECT,INSERT')
                  AND has_table_privilege(current_user,'application_case.advisor_engagement_operations','SELECT,INSERT')
                  AND NOT has_table_privilege(current_user,'application_case.advisor_engagement_activities','UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'application_case.advisor_engagement_operations','UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'application_case.advisor_engagements','DELETE,TRUNCATE')
                  AND NOT has_column_privilege(current_user,'application_case.advisor_engagements','customer_id','UPDATE')
                  AND NOT has_column_privilege(current_user,'application_case.advisor_engagements','advisor_actor_id','UPDATE');
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

    private sealed record EngagementRow(string TenantId, string AdvisorActorId, string CustomerId,
        int Version, string ApplicationStage, DateTimeOffset UpdatedAt);
    private sealed record ActivityRow(string ActivityId, string Kind, string? Body, string[] SourceFactIds,
        string? ApplicationStage, int Version, DateTimeOffset OccurredAt);
    private sealed record OperationRow(string CustomerId, string Action);
}
