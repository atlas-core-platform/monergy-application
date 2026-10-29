using System.Diagnostics;
using System.Text.Json;
using DbUp;
using Npgsql;

namespace Monergy.DatabaseMigrator;

internal sealed record UatDatabase(string Service, string Tenant, string Name, string OwnerPassword, string RuntimePassword, string? DeliveryPassword);
internal sealed record UatProfile(string InstanceId, string PostgresPassword, UatDatabase[] Databases, bool Onboarding = false);

internal static partial class LocalUatBootstrap
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task<int> RunAsync(string file)
    {
        Console.WriteLine("Local UAT bootstrap: starting.");
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Development" ||
            Environment.GetEnvironmentVariable("MONERGY_LOCAL_UAT_BOOTSTRAP") != "1")
            throw new InvalidOperationException("Explicit Development local UAT bootstrap is required.");
        UatProfile? profile = null;
        var stage = "profile-validation";
        try
        {
            profile = JsonSerializer.Deserialize<UatProfile>(await File.ReadAllTextAsync(file).ConfigureAwait(false), Json)
                ?? throw new InvalidOperationException("Missing profile.");
            if (!Guid.TryParseExact(profile.InstanceId, "N", out _) || profile.Databases.Length != (profile.Onboarding ? 8 : 6))
                throw new InvalidOperationException("Invalid profile.");
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var database in profile.Databases)
            {
                if (!(database.Service is "am" or "customer-identity" or "audit" || profile.Onboarding && database.Service == "consent") || database.Tenant is not ("T001" or "T002") ||
                    database.Name != "uat_" + database.Service.Replace('-', '_') + "_" + database.Tenant.ToLowerInvariant() || !expected.Add(database.Name))
                    throw new InvalidOperationException("Invalid owner binding.");
                ValidateSecret(database.OwnerPassword); ValidateSecret(database.RuntimePassword);
                if (database.Service == "am") ValidateSecret(database.DeliveryPassword ?? "");
            }
            ValidateSecret(profile.PostgresPassword);
            if (profile.Onboarding)
            {
                stage = "onboarding-storage";
                await ProbeOnboardingStorageAsync(OnboardingFolder).ConfigureAwait(false);
            }
            stage = "cluster-connection";
            await using var cluster = new NpgsqlConnection(Connection("postgres", "postgres", profile.PostgresPassword));
            await cluster.OpenAsync().ConfigureAwait(false);
            await ExecuteAsync(cluster, "SELECT pg_advisory_lock(705901);").ConfigureAwait(false);
            var marked = (bool)(await ScalarAsync(cluster, "SELECT to_regclass('public.monergy_uat_instance') IS NOT NULL;").ConfigureAwait(false))!;
            if (!marked)
            {
                var existing = (long)(await ScalarAsync(cluster, "SELECT count(*) FROM pg_database WHERE NOT datistemplate AND datname<>'postgres';").ConfigureAwait(false))!;
                if (existing != 0) throw new InvalidOperationException("Bootstrap requires an empty local UAT cluster.");
                await ExecuteAsync(cluster, "CREATE TABLE public.monergy_uat_instance(singleton boolean PRIMARY KEY CHECK(singleton), instance_id text NOT NULL); REVOKE ALL ON public.monergy_uat_instance FROM PUBLIC;").ConfigureAwait(false);
                await ExecuteAsync(cluster, "INSERT INTO public.monergy_uat_instance VALUES(true,@value);", profile.InstanceId).ConfigureAwait(false);
            }
            if ((string?)await ScalarAsync(cluster, "SELECT instance_id FROM public.monergy_uat_instance WHERE singleton;").ConfigureAwait(false) != profile.InstanceId)
                throw new InvalidOperationException("Retained database belongs to another credential profile.");
            await ExecuteAsync(cluster, "DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='monergy_audit_runtime') THEN CREATE ROLE monergy_audit_runtime NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE; END IF; END $$;").ConfigureAwait(false);
            stage = "tenant-registry";
            if (profile.Onboarding) await PrepareOnboardingAsync(profile).ConfigureAwait(false);
            // Establish the C&I initial principal before AM bootstraps authority.
            foreach (var database in profile.Databases.OrderBy(database => database.Service == "am" ? 1 : 0))
            {
                stage = $"owner-bootstrap:{database.Service}/{database.Tenant}";
                Console.WriteLine($"Local UAT bootstrap: {stage}.");
                foreach (var role in new[] { (Suffix: "owner", Password: database.OwnerPassword), (Suffix: "runtime", Password: database.RuntimePassword), (Suffix: "delivery", Password: database.DeliveryPassword) })
                {
                    if (role.Password is null) continue;
                    var name = database.Name + "_" + role.Suffix;
                    var present = await ScalarAsync(cluster, "SELECT rolname FROM pg_roles WHERE rolname=@value;", name).ConfigureAwait(false);
                    if (present is null) await ExecuteAsync(cluster, $"CREATE ROLE {Quote(name)} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '{role.Password}';").ConfigureAwait(false);
                    // Never rotate credentials on replay; a mismatched profile must fail authentication.
                }
                var owner = database.Name + "_owner";
                var actual = await ScalarAsync(cluster, "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname=@value;", database.Name).ConfigureAwait(false);
                if (actual is null) await ExecuteAsync(cluster, $"CREATE DATABASE {Quote(database.Name)} OWNER {Quote(owner)};").ConfigureAwait(false);
                else if ((string)actual != owner) throw new InvalidOperationException("Database owner mismatch.");
                await ExecuteAsync(cluster, $"REVOKE ALL ON DATABASE {Quote(database.Name)} FROM PUBLIC; GRANT CONNECT ON DATABASE {Quote(database.Name)} TO {Quote(database.Name + "_runtime")};" +
                    (database.Service == "am" ? $"GRANT CONNECT ON DATABASE {Quote(database.Name)} TO {Quote(database.Name + "_delivery")};" : "")).ConfigureAwait(false);
                var connection = Connection(database.Name, owner, database.OwnerPassword);
                if (database.Service == "am")
                {
                    var process = new ProcessStartInfo("dotnet") { UseShellExecute = false };
                    process.ArgumentList.Add("/app/am-migrate/Monergy.AccessManagement.Migrations.dll");
                    process.ArgumentList.Add(database.Tenant); process.ArgumentList.Add("A900");
                    process.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
                    process.Environment["MONERGY_AM_REFERENCE_PROVISIONING"] = "1";
                    process.Environment["MONERGY_AM_MIGRATION_CONNECTION"] = connection;
                    using var migration = Process.Start(process) ?? throw new InvalidOperationException("Migration did not start.");
                    await migration.WaitForExitAsync().ConfigureAwait(false);
                    if (migration.ExitCode != 0) throw new InvalidOperationException("AM migration failed.");
                }
                else
                {
                    var result = DeployChanges.To.PostgresqlDatabase(connection).JournalToPostgresqlTable("public", "monergy_migration_history")
                        .WithScriptsFromFileSystem("/source/services/" + database.Service + "/migrations").LogToConsole().Build().PerformUpgrade();
                    if (!result.Successful) throw new InvalidOperationException("Owner migration failed.");
                }
                await using var scoped = new NpgsqlConnection(connection);
                await scoped.OpenAsync().ConfigureAwait(false);
                await ExecuteAsync(scoped, Grants(database)).ConfigureAwait(false);
                if (database.Service == "am")
                    await VerifyAccessManagementRuntimePrivilegesAsync(database).ConfigureAwait(false);
                if (database.Service != "am")
                {
                    var table = database.Service switch { "audit" => "audit.access_tenant_identity", "consent" => "consent.tenant_identity", _ => "customer_identity.tenant_identity" };
                    await ExecuteAsync(scoped, $"INSERT INTO {table} VALUES(true,@value,1) ON CONFLICT(singleton) DO NOTHING;", database.Tenant).ConfigureAwait(false);
                    if ((string?)await ScalarAsync(scoped, $"SELECT tenant_id FROM {table} WHERE singleton;").ConfigureAwait(false) != database.Tenant)
                        throw new InvalidOperationException("Tenant database mismatch.");
                }
                // Customer administration is part of every LOCAL tenant profile.
                // Seed its owner-backed directory additively even when the older
                // six-database profile does not enable the wider onboarding stack.
                if (database.Service == "customer-identity")
                    await SeedCustomerOwnershipAsync(scoped, database.Tenant, profile.InstanceId).ConfigureAwait(false);
                Console.WriteLine($"Ready: {database.Service}/{database.Tenant}; scoped runtime grants applied.");
            }
            stage = "owner-readback-receipts";
            if (profile.Onboarding) await RecordOnboardingAsync(profile, cluster).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or JsonException or IOException or UnauthorizedAccessException)
        {
            if (profile is { Onboarding: true }) await RecordOnboardingFailureAsync(profile).ConfigureAwait(false);
            // Only controlled stage, exception type and SQLSTATE; an exception
            // message/connection string can contain profile credentials.
            var sqlState = error is PostgresException databaseError ? $"; SQLSTATE={databaseError.SqlState}" : "";
            Console.Error.WriteLine($"Local UAT bootstrap failed at {stage}: {error.GetType().Name}{sqlState}. Credentials are not printed.");
            return 1;
        }
    }
    private static string Grants(UatDatabase database)
    {
        var runtime = Quote(database.Name + "_runtime");
        var delivery = Quote(database.Name + "_delivery");
        var schema = database.Service == "customer-identity" ? "customer_identity" : database.Service;
        var common = $"REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT USAGE ON SCHEMA {schema} TO {runtime}; GRANT SELECT ON ALL TABLES IN SCHEMA {schema} TO {runtime};";
        var profile = database.Service == "customer-identity"
            ? $"GRANT INSERT ON customer_identity.kyc_events TO {runtime}; GRANT INSERT ON customer_identity.customers TO {runtime}; GRANT INSERT,UPDATE ON customer_identity.registration_challenges,customer_identity.registered_credentials,customer_identity.persona_credentials TO {runtime}; GRANT INSERT ON customer_identity.registration_events TO {runtime}; GRANT INSERT ON customer_identity.persona_invitation_events TO {runtime}; GRANT INSERT,UPDATE ON customer_identity.customer_availability TO {runtime}; GRANT INSERT ON customer_identity.customer_status_changes TO {runtime}; GRANT INSERT,UPDATE ON customer_identity.customer_pan TO {runtime}; GRANT INSERT ON customer_identity.customer_pan_receipts,customer_identity.customer_pan_audit TO {runtime}; GRANT INSERT,UPDATE ON customer_identity.customer_profiles TO {runtime}; GRANT INSERT ON customer_identity.customer_profile_receipts TO {runtime}; GRANT INSERT ON customer_identity.self_session_receipts TO {runtime}; GRANT INSERT ON customer_identity.account_agreement_publications,customer_identity.account_agreement_acceptances TO {runtime};" : "";
        return common + profile + (database.Service switch
        {
            "am" => $"GRANT INSERT,UPDATE,DELETE ON am.permissions,am.roles,am.permission_capabilities,am.role_permissions,am.access_subjects,am.resource_grants,am.customer_relationships,am.groups,am.group_members,am.import_batches,am.import_rows,am.import_email_reservations TO {runtime}; GRANT INSERT,UPDATE ON am.access_requests TO {runtime}; GRANT INSERT,DELETE ON am.subject_permissions TO {runtime}; GRANT INSERT ON am.access_request_events,am.access_request_permission_grants TO {runtime}; GRANT UPDATE(policy_version) ON am.policy_state TO {runtime}; GRANT INSERT ON am.outbox TO {runtime}; GRANT USAGE ON SCHEMA am TO {delivery}; GRANT SELECT ON am.tenant_identity,am.policy_state,am.outbox,am.outbox_delivery,am.outbox_redrives TO {delivery}; GRANT INSERT,UPDATE ON am.outbox_delivery TO {delivery}; GRANT INSERT ON am.outbox_redrives TO {delivery};",
            "customer-identity" => $"GRANT INSERT(session_hash,actor_id,subject_version),UPDATE(revoked) ON customer_identity.trusted_sessions TO {runtime}; GRANT INSERT,UPDATE(version) ON customer_identity.access_subject_versions TO {runtime}; GRANT UPDATE(version) ON customer_identity.access_policy_version TO {runtime}; GRANT INSERT ON customer_identity.access_event_inbox TO {runtime}; GRANT INSERT(actor_id,normalized_email) ON customer_identity.tenant_principals TO {runtime}; GRANT INSERT ON customer_identity.provisioning_receipts TO {runtime}; GRANT INSERT,UPDATE ON customer_identity.email_access_challenges TO {runtime}; GRANT INSERT ON customer_identity.email_access_events TO {runtime};",
            "audit" => $"GRANT INSERT ON audit.evidence,audit.inbox,audit.access_event_inbox,audit.product_activity TO {runtime};",
            "consent" => $"GRANT INSERT ON consent.grants,consent.receipts,consent.audit,consent.outbox,consent.provider_grants,consent.provider_evidence TO {runtime}; GRANT UPDATE(version,folder_id,revoked_at) ON consent.provider_grants TO {runtime}; GRANT UPDATE(revoked_at) ON consent.grants TO {runtime}; GRANT UPDATE(version) ON consent.state TO {runtime};",
            _ => throw new InvalidOperationException("Unknown service."),
        });
    }
    private static async Task VerifyAccessManagementRuntimePrivilegesAsync(UatDatabase database)
    {
        await using var runtime = new NpgsqlConnection(Connection(
            database.Name, database.Name + "_runtime", database.RuntimePassword));
        await runtime.OpenAsync().ConfigureAwait(false);
        var privileges = await ScalarAsync(runtime, """
            SELECT has_table_privilege(current_user,'am.access_request_permission_grants','SELECT')
               AND has_table_privilege(current_user,'am.access_request_permission_grants','INSERT')
               AND NOT has_table_privilege(current_user,'am.access_request_permission_grants','UPDATE')
               AND NOT has_table_privilege(current_user,'am.access_request_permission_grants','DELETE')
               AND NOT has_table_privilege(current_user,'am.access_request_permission_grants','TRUNCATE');
            """).ConfigureAwait(false);
        if (privileges is not true)
            throw new InvalidOperationException("LOCAL_AM_RUNTIME_PROVENANCE_PRIVILEGE_MISMATCH");

        // Exercise the deployed runtime fulfillment write set without retaining probe history.
        await using var transaction = await runtime.BeginTransactionAsync().ConfigureAwait(false);
        var requestId = Guid.NewGuid().ToString("N");
        var actorId = "local-runtime-probe-" + requestId;
        await using var command = new NpgsqlCommand("""
            INSERT INTO am.access_subjects (actor_id,normalized_email,status,business_role_id)
            VALUES (@actor,@email,'Active','default-role-financial-advisor');
            INSERT INTO am.access_requests (
                request_id,tenant_id,requester_actor_id,target_code,target_definition_hash,
                requester_role_id,requester_subject_version,submit_operation_id,state,expires_at)
            VALUES (@request,@tenant,@actor,'financial-advisor.assigned-customer-financial-review',
                @definition,'default-role-financial-advisor',1,@submit,'Submitted',clock_timestamp()+interval '1 hour');
            INSERT INTO am.subject_permissions (
                actor_id,permission_id,granted_at,granted_by,source_request_id)
            VALUES (@actor,'default-permission-profile-read',clock_timestamp(),'A900',@request);
            INSERT INTO am.access_request_permission_grants (
                request_id,actor_id,permission_id,permission_code,capability_id,scope)
            VALUES (@request,@actor,'default-permission-profile-read','financial-profile-read',
                'financial-profile.profile.read','Resource');
            UPDATE am.policy_state SET policy_version=policy_version+1 WHERE singleton;
            UPDATE am.access_requests SET state='Fulfilled',decision_at=clock_timestamp(),
                deciding_actor_id='A900',decision_action='Approve',decision_operation_id=@decision,
                decision_reason=NULL,fulfilled_at=clock_timestamp()
            WHERE request_id=@request;
            INSERT INTO am.access_request_events (
                event_id,request_id,event_type,actor_id,policy_version)
            VALUES (@approved,@request,'Approved','A900',(SELECT policy_version FROM am.policy_state WHERE singleton)),
                   (@fulfilled,@request,'Fulfilled','A900',(SELECT policy_version FROM am.policy_state WHERE singleton));
            INSERT INTO am.outbox (
                event_id,event_type,tenant_id,aggregate_id,initiator_actor_id,
                correlation_id,policy_version,subject_version,payload)
            VALUES (@outbox,'AccessManagementAuditEvidence',@tenant,@request,'A900',@correlation,
                (SELECT policy_version FROM am.policy_state WHERE singleton),1,'{"probe":true}'::jsonb);
            SELECT EXISTS(
                SELECT 1 FROM am.access_request_permission_grants
                WHERE request_id=@request AND actor_id=@actor
                  AND permission_id='default-permission-profile-read'
                  AND capability_id='financial-profile.profile.read' AND scope='Resource');
            """, runtime, transaction);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("email", requestId + "@runtime-probe.example.test");
        command.Parameters.AddWithValue("request", requestId);
        command.Parameters.AddWithValue("tenant", database.Tenant);
        command.Parameters.AddWithValue("definition", new string('0', 64));
        command.Parameters.AddWithValue("submit", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("decision", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("approved", Guid.NewGuid());
        command.Parameters.AddWithValue("fulfilled", Guid.NewGuid());
        command.Parameters.AddWithValue("outbox", Guid.NewGuid());
        command.Parameters.AddWithValue("correlation", Guid.NewGuid().ToString("N"));
        if (await command.ExecuteScalarAsync().ConfigureAwait(false) is not true)
            throw new InvalidOperationException("LOCAL_AM_RUNTIME_PROVENANCE_INSERT_FAILED");
        await transaction.RollbackAsync().ConfigureAwait(false);
    }
    private static void ValidateSecret(string value)
    { if (value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidOperationException("Invalid profile secret."); }
    private static string Quote(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    private static string Connection(string database, string user, string password) => new NpgsqlConnectionStringBuilder
    { Host = "postgres", Database = database, Username = user, Password = password, Timeout = 10, IncludeErrorDetail = false }.ConnectionString;
    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, string? value = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (value is not null) command.Parameters.AddWithValue("value", value);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result is DBNull ? null : result;
    }
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, string? value = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (value is not null) command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
