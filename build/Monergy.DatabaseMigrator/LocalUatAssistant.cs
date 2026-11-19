using System.Diagnostics;
using System.Text.Json;
using DbUp;
using Npgsql;

namespace Monergy.DatabaseMigrator;

internal sealed record UatAssistantDatabase(string Service, string Name, string OwnerPassword, string RuntimePassword);
internal sealed record UatAssistantProfile(bool Enabled, UatAssistantDatabase[] Databases);

internal static partial class LocalUatBootstrap
{
    // Invoked only by the optional Assistant bootstrap. Business bootstrap never waits for these stores.
    public static async Task<int> RunAssistantAsync(string file)
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Development" ||
            Environment.GetEnvironmentVariable("MONERGY_LOCAL_UAT_BOOTSTRAP") != "1")
            throw new InvalidOperationException("Explicit Development local UAT bootstrap is required.");
        try
        {
            var profile = JsonSerializer.Deserialize<UatProfile>(await File.ReadAllTextAsync(file).ConfigureAwait(false), Json)
                ?? throw new InvalidOperationException("Assistant profile unavailable.");
            if (!Guid.TryParseExact(profile.InstanceId, "N", out _) || !profile.Onboarding || profile.Assistant is not { Enabled: true })
                throw new InvalidOperationException("Assistant opt-in profile is required.");
            ValidateSecret(profile.PostgresPassword);
            await using var cluster = new NpgsqlConnection(Connection("postgres", "postgres", profile.PostgresPassword));
            await cluster.OpenAsync().ConfigureAwait(false);
            await ExecuteAsync(cluster, "SELECT pg_advisory_lock(7059123)").ConfigureAwait(false);
            if (await ScalarAsync(cluster, "SELECT to_regclass('public.monergy_uat_instance') IS NOT NULL").ConfigureAwait(false) is not true ||
                (string?)await ScalarAsync(cluster, "SELECT instance_id FROM public.monergy_uat_instance WHERE singleton").ConfigureAwait(false) != profile.InstanceId)
                throw new InvalidOperationException("Assistant bootstrap requires the matching retained profile marker.");
            await PrepareAssistantStoresAsync(profile, cluster).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Optional Assistant bootstrap unavailable ({error.GetType().Name}); business bootstrap is unchanged.");
            return 1;
        }
    }

    private static async Task PrepareAssistantStoresAsync(UatProfile profile, NpgsqlConnection cluster)
    {
        var modules = profile.Assistant?.Databases ?? [];
        if (!profile.Onboarding || modules.Length != 3 || modules.Select(item => item.Service).Distinct(StringComparer.Ordinal).Count() != 3)
            throw new InvalidOperationException("Assistant requires three separate owner stores in an explicitly opted-in onboarding profile.");
        string? profilePrefix = null;
        foreach (var module in modules)
        {
            if (module.Service is not ("ai-feedback" or "am-platform" or "audit-platform") || module.Name.Length is < 8 or > 50 ||
                !module.Name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_') || !module.Name.StartsWith("uat_", StringComparison.Ordinal) ||
                !module.Name.EndsWith("_" + module.Service.Replace('-', '_'), StringComparison.Ordinal))
                throw new InvalidOperationException("Assistant owner database binding is invalid.");
            var prefix = module.Name[..^(module.Service.Length + 1)];
            if (prefix.Length <= 4 || profilePrefix is not null && prefix != profilePrefix)
                throw new InvalidOperationException("Assistant stores must share one explicit profile namespace.");
            profilePrefix = prefix;
            ValidateSecret(module.OwnerPassword); ValidateSecret(module.RuntimePassword);
            if (module.OwnerPassword == module.RuntimePassword) throw new InvalidOperationException("Assistant owner and runtime credentials must be distinct.");
        }
        foreach (var module in modules)
        {
            var owner = module.Name + "_owner"; var runtime = module.Name + "_runtime";
            foreach (var role in new[] { (Name: owner, Password: module.OwnerPassword), (Name: runtime, Password: module.RuntimePassword) })
            {
                if (await ScalarAsync(cluster, "SELECT rolname FROM pg_roles WHERE rolname=@value", role.Name).ConfigureAwait(false) is null)
                    await ExecuteAsync(cluster, $"CREATE ROLE {Quote(role.Name)} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '{role.Password}'").ConfigureAwait(false);
                if (await ScalarAsync(cluster, """
                    SELECT rolcanlogin AND NOT (rolsuper OR rolinherit OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
                     AND NOT EXISTS(SELECT 1 FROM pg_auth_members WHERE member=r.oid)
                    FROM pg_roles r WHERE rolname=@value
                    """, role.Name).ConfigureAwait(false) is not true)
                    throw new InvalidOperationException("Retained Assistant role has unexpected privileges; restore the approved role configuration without rotating credentials.");
            }
            var actualOwner = await ScalarAsync(cluster, "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname=@value", module.Name).ConfigureAwait(false);
            if (actualOwner is null) await ExecuteAsync(cluster, $"CREATE DATABASE {Quote(module.Name)} OWNER {Quote(owner)}").ConfigureAwait(false);
            else if ((string)actualOwner != owner) throw new InvalidOperationException("Assistant retained owner mismatch.");
            await ExecuteAsync(cluster, $"REVOKE ALL ON DATABASE {Quote(module.Name)} FROM PUBLIC; GRANT CONNECT ON DATABASE {Quote(module.Name)} TO {Quote(runtime)};").ConfigureAwait(false);
            var ownerConnection = Connection(module.Name, owner, module.OwnerPassword);
            await using var connection = new NpgsqlConnection(ownerConnection);
            await connection.OpenAsync().ConfigureAwait(false);
            await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS public.monergy_assistant_store(singleton boolean PRIMARY KEY CHECK(singleton),instance_id text NOT NULL,service text NOT NULL); REVOKE ALL ON public.monergy_assistant_store FROM PUBLIC;").ConfigureAwait(false);
            await using (var command = new NpgsqlCommand("INSERT INTO public.monergy_assistant_store VALUES(true,@instance,@service) ON CONFLICT(singleton) DO NOTHING", connection))
            { command.Parameters.AddWithValue("instance", profile.InstanceId); command.Parameters.AddWithValue("service", module.Service); await command.ExecuteNonQueryAsync().ConfigureAwait(false); }
            if ((string?)await ScalarAsync(connection, "SELECT instance_id||':'||service FROM public.monergy_assistant_store WHERE singleton").ConfigureAwait(false) != profile.InstanceId + ":" + module.Service)
                throw new InvalidOperationException("Assistant retained profile identity mismatch.");
            // Platform stores never flow through tenant identity seeding or Default Access Pack adoption.
            if (module.Service == "am-platform")
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
                start.ArgumentList.Add("/app/am-migrate/Monergy.AccessManagement.Migrations.dll"); start.ArgumentList.Add("--platform");
                start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
                start.Environment["MONERGY_AM_PLATFORM_PROVISIONING"] = "1";
                start.Environment["MONERGY_AM_PLATFORM_MIGRATION_CONNECTION"] = ownerConnection;
                using var process = Process.Start(start) ?? throw new InvalidOperationException("AM platform migration process unavailable.");
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("AM platform migration failed.");
            }
            else
            {
                var path = module.Service == "ai-feedback" ? "/source/services/ai-intelligence/migrations/assistant-feedback" : "/source/services/audit/migrations/platform";
                var journal = module.Service == "ai-feedback" ? "assistant_feedback_schema_history" : "platform_audit_schema_history";
                var result = DeployChanges.To.PostgresqlDatabase(ownerConnection).JournalToPostgresqlTable("public", journal)
                    .WithScriptsFromFileSystem(path, new DbUp.ScriptProviders.FileSystemScriptOptions { IncludeSubDirectories = false })
                    .WithTransaction().Build().PerformUpgrade();
                if (!result.Successful) throw new InvalidOperationException("Assistant owner migration failed.");
            }
            await ExecuteAsync(connection, AssistantGrants(module.Service, runtime)).ConfigureAwait(false);
            // Existing runtime credentials are authenticated, never silently rotated on replay.
            await using var runtimeConnection = new NpgsqlConnection(Connection(module.Name, runtime, module.RuntimePassword));
            await runtimeConnection.OpenAsync().ConfigureAwait(false);
            var schema = module.Service switch { "ai-feedback" => "ai_feedback", "am-platform" => "am_platform", _ => "audit_platform" };
            if (await ScalarAsync(runtimeConnection, "SELECT NOT has_schema_privilege(current_user,@value,'CREATE')", schema).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("Assistant runtime schema privilege mismatch.");
            if (module.Service == "audit-platform" && await ScalarAsync(runtimeConnection, """
                SELECT bool_and(has_table_privilege(current_user,t,'SELECT') AND has_table_privilege(current_user,t,'INSERT')
                 AND NOT has_table_privilege(current_user,t,'UPDATE') AND NOT has_table_privilege(current_user,t,'DELETE')
                 AND NOT has_table_privilege(current_user,t,'TRUNCATE'))
                FROM unnest(ARRAY['audit_platform.inbox','audit_platform.evidence','audit_platform.receipts']) t
                """).ConfigureAwait(false) is not true) throw new InvalidOperationException("Platform audit append-only runtime privilege mismatch.");
            Console.WriteLine($"Ready: {module.Service}; isolated platform store, no reviewer grants provisioned.");
        }
    }

    private static string AssistantGrants(string service, string role)
    {
        var runtime = Quote(role);
        var schema = service switch { "ai-feedback" => "ai_feedback", "am-platform" => "am_platform", "audit-platform" => "audit_platform", _ => throw new InvalidOperationException() };
        var common = $"REVOKE CREATE ON SCHEMA public FROM PUBLIC; REVOKE ALL ON SCHEMA {schema} FROM {runtime}; REVOKE ALL ON ALL TABLES IN SCHEMA {schema} FROM {runtime}; GRANT USAGE ON SCHEMA {schema} TO {runtime}; GRANT SELECT ON ALL TABLES IN SCHEMA {schema} TO {runtime};";
        return common + (service switch
        {
            "ai-feedback" => $"GRANT INSERT,UPDATE,DELETE ON ai_feedback.topics,ai_feedback.operations,ai_feedback.counters TO {runtime}; GRANT INSERT ON ai_feedback.audit_outbox TO {runtime}; GRANT UPDATE(attempt_count,recovery_count,next_attempt_at,receipt,delivered_at) ON ai_feedback.audit_outbox TO {runtime}; GRANT EXECUTE ON FUNCTION ai_feedback.purge_delivered_audit() TO {runtime};",
            "am-platform" => $"GRANT INSERT,UPDATE ON am_platform.policy,am_platform.principal_references,am_platform.reviewer_grants,am_platform.audit_delivery TO {runtime}; GRANT INSERT ON am_platform.command_results,am_platform.audit_outbox TO {runtime};",
            "audit-platform" => $"GRANT INSERT ON audit_platform.inbox,audit_platform.evidence,audit_platform.receipts TO {runtime};",
            _ => throw new InvalidOperationException(),
        });
    }
}
