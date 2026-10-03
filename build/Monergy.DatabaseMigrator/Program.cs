using DbUp;

var options = Parse(args);
var service = Required(options, "service");
var connection = ResolveConnection(options);
var repositoryRoot = Path.GetFullPath(Required(options, "repository-root"));
var allowed = new HashSet<string>(StringComparer.Ordinal)
{
    "evidence", "financial-profile", "financial-rules", "reporting", "audit", "job-management",
};
if (!allowed.Contains(service))
{
    throw new InvalidOperationException($"Service '{service}' is outside the D09 persistence cohort.");
}

var migrationRoot = Path.Combine(repositoryRoot, "services", service, "migrations");
if (!Directory.Exists(migrationRoot))
{
    throw new DirectoryNotFoundException($"Service-owned migration directory '{migrationRoot}' is unavailable.");
}

var upgrader = DeployChanges.To.PostgresqlDatabase(connection)
    .JournalToPostgresqlTable("public", "monergy_migration_history")
    .WithScriptsFromFileSystem(migrationRoot)
    .LogToConsole()
    .Build();
var result = upgrader.PerformUpgrade();
if (!result.Successful)
{
    Console.Error.WriteLine(result.Error);
    return 1;
}

Console.WriteLine($"Applied service-owned migrations for {service}.");
return 0;

static Dictionary<string, string> Parse(string[] values)
{
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Arguments must be --name value pairs.");
        parsed[values[index][2..]] = values[index + 1];
    }
    return parsed;
}

static string Required(IReadOnlyDictionary<string, string> values, string name) =>
    values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException($"--{name} is required.");

static string ResolveConnection(IReadOnlyDictionary<string, string> values)
{
    if (values.TryGetValue("connection-environment", out var variableName) &&
        !string.IsNullOrWhiteSpace(variableName))
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"The migration connection environment variable '{variableName}' is unavailable.");
    }

    // Historical D09/D10 invocations remain compatible. New D11 orchestration uses
    // --connection-environment so credentials never appear in the process command line.
    return Required(values, "connection");
}
