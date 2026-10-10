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
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Development" ||
            Environment.GetEnvironmentVariable("MONERGY_LOCAL_UAT_BOOTSTRAP") != "1")
            throw new InvalidOperationException("Explicit Development local UAT bootstrap is required.");
        UatProfile? profile = null;
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
            if (profile.Onboarding) await PrepareOnboardingAsync(profile).ConfigureAwait(false);
            // Establish the C&I initial principal before AM bootstraps authority.
            foreach (var database in profile.Databases.OrderBy(database => database.Service == "am" ? 1 : 0))
            {
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
                if (database.Service != "am")
                {
                    var table = database.Service switch { "audit" => "audit.access_tenant_identity", "consent" => "consent.tenant_identity", _ => "customer_identity.tenant_identity" };
                    await ExecuteAsync(scoped, $"INSERT INTO {table} VALUES(true,@value,1) ON CONFLICT(singleton) DO NOTHING;", database.Tenant).ConfigureAwait(false);
                    if ((string?)await ScalarAsync(scoped, $"SELECT tenant_id FROM {table} WHERE singleton;").ConfigureAwait(false) != database.Tenant)
                        throw new InvalidOperationException("Tenant database mismatch.");
                }
                if (profile.Onboarding && database.Service == "customer-identity")
                    await SeedCustomerOwnershipAsync(scoped, database.Tenant, profile.InstanceId).ConfigureAwait(false);
                Console.WriteLine($"Ready: {database.Service}/{database.Tenant}; scoped runtime grants applied.");
            }
            if (profile.Onboarding) await RecordOnboardingAsync(profile, cluster).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or JsonException or IOException)
        {
            if (profile is { Onboarding: true }) await RecordOnboardingFailureAsync(profile).ConfigureAwait(false);
            Console.Error.WriteLine("Local UAT bootstrap failed. Check profile/volume pairing and owner migration output; credentials are not printed.");
            return 1;
        }
    }
    private static string Grants(UatDatabase database)
    {
        var runtime = Quote(database.Name + "_runtime");
        var delivery = Quote(database.Name + "_delivery");
        var schema = database.Service == "customer-identity" ? "customer_identity" : database.Service;
        var common = $"REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT USAGE ON SCHEMA {schema} TO {runtime}; GRANT SELECT ON ALL TABLES IN SCHEMA {schema} TO {runtime};";
        return common + (database.Service switch
        {
            "am" => $"GRANT INSERT,UPDATE,DELETE ON am.permissions,am.roles,am.permission_capabilities,am.role_permissions,am.access_subjects,am.resource_grants,am.customer_relationships,am.groups,am.group_members,am.import_batches,am.import_rows,am.import_email_reservations TO {runtime}; GRANT UPDATE(policy_version) ON am.policy_state TO {runtime}; GRANT INSERT ON am.outbox TO {runtime}; GRANT USAGE ON SCHEMA am TO {delivery}; GRANT SELECT ON am.tenant_identity,am.policy_state,am.outbox,am.outbox_delivery,am.outbox_redrives TO {delivery}; GRANT INSERT,UPDATE ON am.outbox_delivery TO {delivery}; GRANT INSERT ON am.outbox_redrives TO {delivery};",
            "customer-identity" => $"GRANT INSERT(session_hash,actor_id,subject_version),UPDATE(revoked) ON customer_identity.trusted_sessions TO {runtime}; GRANT INSERT,UPDATE(version) ON customer_identity.access_subject_versions TO {runtime}; GRANT UPDATE(version) ON customer_identity.access_policy_version TO {runtime}; GRANT INSERT ON customer_identity.access_event_inbox TO {runtime}; GRANT INSERT(actor_id,normalized_email) ON customer_identity.tenant_principals TO {runtime}; GRANT INSERT ON customer_identity.provisioning_receipts TO {runtime};",
            "audit" => $"GRANT INSERT ON audit.evidence,audit.inbox,audit.access_event_inbox TO {runtime};",
            "consent" => $"GRANT INSERT ON consent.grants,consent.receipts,consent.audit,consent.outbox TO {runtime}; GRANT UPDATE(revoked_at) ON consent.grants TO {runtime}; GRANT UPDATE(version) ON consent.state TO {runtime};",
            _ => throw new InvalidOperationException("Unknown service."),
        });
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
