using System.Reflection;
using System.Text.Json;
using Dapper;
using DbUp;
using Monergy.AccessManagement;
using Npgsql;

if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Development" ||
    Environment.GetEnvironmentVariable("MONERGY_AM_REFERENCE_PROVISIONING") != "1" || args.Length != 2)
{
    Console.Error.WriteLine("Reference provisioning requires Development, MONERGY_AM_REFERENCE_PROVISIONING=1, and tenant-id initial-admin-actor arguments.");
    return 1;
}

var tenantId = args[0];
var actorId = args[1];
AccessInput.Identifier(tenantId, 64);
AccessInput.Identifier(actorId);
var catalog = new CapabilityCatalog();
DefaultAccessPack.Validate(catalog);
var identity = await new ReferenceTenantIdentityDirectory().FindAsync(tenantId, actorId, default);
if (identity is null)
{
    Console.Error.WriteLine("A verified reference identity is required.");
    return 1;
}
var connectionString = Environment.GetEnvironmentVariable("MONERGY_AM_MIGRATION_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("MONERGY_AM_MIGRATION_CONNECTION is required.");
    return 1;
}

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();
await connection.ExecuteAsync("SELECT pg_advisory_lock(1549537391,2);");
try
{
    var hasIdentityTable = await connection.ExecuteScalarAsync<bool>(
        "SELECT to_regclass('am.tenant_identity') IS NOT NULL;");
    if (hasIdentityTable)
    {
        var existingTenant = await connection.QuerySingleOrDefaultAsync<string>("SELECT tenant_id FROM am.tenant_identity WHERE singleton;");
        if (existingTenant is not null && existingTenant != tenantId)
            throw new InvalidOperationException("TENANT_DATABASE_BINDING_MISMATCH");
    }

    var upgrade = DeployChanges.To.PostgresqlDatabase(connectionString)
        .JournalToPostgresqlTable("public", "am_schema_history")
        .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
        .WithTransaction()
        .LogToConsole()
        .Build()
        .PerformUpgrade();
    if (!upgrade.Successful)
    {
        Console.Error.WriteLine("Service-owned schema migration failed.");
        return 1;
    }

    await using var transaction = await connection.BeginTransactionAsync();
    var revision = await connection.ExecuteScalarAsync<long>(
        "SELECT policy_version FROM am.policy_state WHERE singleton FOR UPDATE;", transaction: transaction);
    var anchor = await connection.QuerySingleOrDefaultAsync<TenantAnchor>(
        """
        SELECT initial_admin_actor_id AS InitialAdminActorId,
            default_access_pack_version AS DefaultAccessPackVersion
        FROM am.tenant_identity WHERE singleton;
        """, transaction: transaction);
    if (anchor?.InitialAdminActorId is not null)
    {
        if (anchor.InitialAdminActorId != actorId) throw new InvalidOperationException("BOOTSTRAP_ALREADY_COMPLETED");
        await transaction.CommitAsync();
        Console.WriteLine(anchor.DefaultAccessPackVersion == DefaultAccessPack.Version
            ? "Schema current; initial administrator and default access pack already bootstrapped."
            : "Schema current; existing tenant bootstrap preserved without automatic access-pack mutation.");
        return 0;
    }
    if (revision != 0 || await connection.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM am.access_subjects;", transaction: transaction) != 0)
        throw new InvalidOperationException("BOOTSTRAP_REQUIRES_EMPTY_ACCESS_STATE");

    await connection.ExecuteAsync("""
        INSERT INTO am.tenant_identity (
            singleton,tenant_id,initial_admin_actor_id,initialized_at,
            default_access_pack_version,default_access_pack_installed_at)
        VALUES (true,@TenantId,@ActorId,clock_timestamp(),@PackVersion,clock_timestamp());
        INSERT INTO am.access_subjects (actor_id,normalized_email,status,administrative_authority)
        VALUES (@ActorId,@NormalizedEmail,'Active','TenantAdmin');
        """, new
    {
        identity.TenantId,
        identity.ActorId,
        identity.NormalizedEmail,
        PackVersion = DefaultAccessPack.Version
    }, transaction);

    foreach (var permission in DefaultAccessPack.Permissions)
    {
        await connection.ExecuteAsync("""
            INSERT INTO am.permissions(permission_id,code,label) VALUES (@PermissionId,@Code,@Label);
            """, permission, transaction);
        foreach (var grant in permission.Grants)
            await connection.ExecuteAsync("""
                INSERT INTO am.permission_capabilities(permission_id,capability_id,scope)
                VALUES (@PermissionId,@CapabilityId,@Scope);
                """, new { permission.PermissionId, grant.CapabilityId, Scope = grant.Scope.ToString() }, transaction);
    }

    foreach (var role in DefaultAccessPack.Roles)
    {
        await connection.ExecuteAsync("""
            INSERT INTO am.roles(role_id,code,label) VALUES (@RoleId,@Code,@Label);
            """, role, transaction);
        foreach (var permissionId in role.PermissionIds)
            await connection.ExecuteAsync("""
                INSERT INTO am.role_permissions(role_id,permission_id) VALUES (@RoleId,@PermissionId);
                """, new { role.RoleId, PermissionId = permissionId }, transaction);
    }

    await connection.ExecuteAsync("UPDATE am.policy_state SET policy_version=1 WHERE singleton;", transaction: transaction);
    await connection.ExecuteAsync("""
        INSERT INTO am.default_access_pack_history (
            pack_version,pack_identity,installed_at,operation_id,plan_hash,plan_document,
            from_policy_version,to_policy_version,installed_by)
        VALUES (@PackVersion,@PackIdentity,clock_timestamp(),'reference-bootstrap-v2',NULL,NULL,0,1,'reference-control-plane');
        """, new
    {
        PackVersion = DefaultAccessPack.Version,
        PackIdentity = DefaultAccessPack.Identity
    }, transaction);
    var correlationId = Guid.NewGuid().ToString("N");
    var payload = JsonSerializer.Serialize(new
    {
        operation = "tenant.bootstrap.completed",
        actorId,
        defaultAccessPack = DefaultAccessPack.Identity
    });
    foreach (var eventType in new[] { "AuthorizationPolicyChanged", "AccessManagementAuditEvidence" })
        await connection.ExecuteAsync("""
            INSERT INTO am.outbox (event_id,event_type,tenant_id,aggregate_id,initiator_actor_id,
                correlation_id,policy_version,subject_version,payload)
            VALUES (@EventId,@EventType,@TenantId,@AggregateId,'reference-control-plane',
                @CorrelationId,1,@SubjectVersion,CAST(@Payload AS jsonb));
            """, new
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            identity.TenantId,
            AggregateId = eventType == "AuthorizationPolicyChanged" ? identity.TenantId : identity.ActorId,
            CorrelationId = correlationId,
            SubjectVersion = eventType == "AuthorizationPolicyChanged" ? (long?)null : 1,
            Payload = payload
        }, transaction);

    await transaction.CommitAsync();
    Console.WriteLine($"Tenant schema, exactly one reference Tenant Admin, and {DefaultAccessPack.Identity} initialized atomically.");
    return 0;
}
catch (Exception exception) when (exception is InvalidOperationException or NpgsqlException)
{
    Console.Error.WriteLine("Reference provisioning failed: " +
        (exception is NpgsqlException ? "DATABASE_OPERATION_FAILED" : exception.Message));
    return 1;
}
finally
{
    await connection.ExecuteAsync("SELECT pg_advisory_unlock(1549537391,2);");
}


file sealed class TenantAnchor
{
    public string? InitialAdminActorId { get; init; }
    public string? DefaultAccessPackVersion { get; init; }
}
