using System.Reflection;
using Dapper;
using DbUp;
using Npgsql;

// Independent owner database/journal. Never runs tenant bootstrap or Default Access Pack adoption.
internal static class PlatformMigration
{
    public static async Task<int> RunAsync()
    {
        if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Development" ||
            Environment.GetEnvironmentVariable("MONERGY_AM_PLATFORM_PROVISIONING") != "1")
        { Console.Error.WriteLine("Platform provisioning requires explicit Development authorization."); return 1; }
        var connectionString = Environment.GetEnvironmentVariable("MONERGY_AM_PLATFORM_MIGRATION_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        { Console.Error.WriteLine("MONERGY_AM_PLATFORM_MIGRATION_CONNECTION is required."); return 1; }
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await connection.ExecuteAsync("SELECT pg_advisory_lock(1549537391,11);").ConfigureAwait(false);
            try
            {
                var tenantSchema = await connection.ExecuteScalarAsync<bool>("SELECT to_regnamespace('am') IS NOT NULL;").ConfigureAwait(false);
                if (tenantSchema) throw new InvalidOperationException("Platform and tenant stores must be separate.");
                var upgrade = DeployChanges.To.PostgresqlDatabase(connectionString)
                    .JournalToPostgresqlTable("public", "am_platform_schema_history")
                    .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly(), name => name.Contains(".platform-migrations.", StringComparison.Ordinal))
                    .WithTransaction().Build().PerformUpgrade();
                if (!upgrade.Successful)
                { Console.Error.WriteLine("Platform owner schema migration failed."); return 1; }
                Console.WriteLine("AM platform schema initialized; no reviewer authority was granted.");
                return 0;
            }
            finally { await connection.ExecuteAsync("SELECT pg_advisory_unlock(1549537391,11);").ConfigureAwait(false); }
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or ArgumentException)
        { Console.Error.WriteLine("Platform owner schema migration unavailable."); return 1; }
    }
}
