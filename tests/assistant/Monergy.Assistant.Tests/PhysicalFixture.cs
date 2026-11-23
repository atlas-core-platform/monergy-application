using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DbUp;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monergy.Platform;
using Monergy.AccessManagement;
using Monergy.Platform.ControlPlane;
using Monergy.Services.AiIntelligence.AssistantFeedback;
using Monergy.Services.Audit.PlatformAudit;
using Monergy.UatGateway;
using Npgsql;
using Xunit;
using Am = Monergy.AccessManagement;

namespace Monergy.Assistant.Tests;

[CollectionDefinition("Assistant physical", DisableParallelization = true)]
public sealed class PhysicalTestGroup : ICollectionFixture<PhysicalFixture>;

public sealed class PhysicalFixture : IAsyncLifetime, IDisposable
{
    private readonly List<WebApplication> hosts = [];
    private readonly Dictionary<string, string> owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> runtimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> tokens = new(StringComparer.Ordinal);
    private readonly string prefix = "assistant_app_" + Guid.NewGuid().ToString("N")[..10];
    private string cluster = "";
    private string folder = "";
    private string session = "";
    private FilePlatformCredentialStore? credentials;
    internal PlatformAdministratorSessions Sessions { get; private set; } = null!;
    internal IConfiguration Configuration { get; private set; } = null!;
    public HttpClient Client { get; } = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
    public bool LoseNextOwnerResponse { get; set; }
    public (int Status, string Body)? NextOwnerFailure { get; set; }
    public string LoseNextOwnerResponsePath { get; set; } = "/internal/assistant-feedback/v1/topics";
    public bool AuditTransportUnavailable { get; set; }
    public string Token(string purpose) => tokens[purpose];
    public string Connection(string service, bool owner = false) => owner ? owners[service] : runtimes[service];
    internal string FixtureFolder => folder;
    internal HttpRequestMessage BrowserRequest(HttpMethod method, string path, string host = "platform.localhost", bool authenticated = true)
    {
        var request = new HttpRequestMessage(method, "http://127.0.0.1:4173" + path); request.Headers.Host = host;
        if (authenticated) request.Headers.Add("X-Monergy-Platform-Session", session);
        return request;
    }
    public static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "services", "ai-intelligence"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
        }
    }
    public static string Expert => Environment.GetEnvironmentVariable("MONERGY_ASSISTANT_EXPERT_ROOT") ?? throw new InvalidOperationException("Exact Expert candidate fixture is required.");
    public static string CatalogueFile => Path.Combine(Expert, "generated", "assistant-runtime", "validation-catalogue.json");
    public AssistantKnowledgeCatalogue Catalogue() => new(Configuration["Monergy:AssistantFeedback:CataloguePath"]!, Configuration["Monergy:AssistantFeedback:CatalogueSha256"]!);

    public async Task InitializeAsync()
    {
        var file = Environment.GetEnvironmentVariable("MONERGY_ASSISTANT_PG_FILE") ?? throw new InvalidOperationException("Disposable PostgreSQL connection file is required; this suite never skips its fixture.");
        cluster = await File.ReadAllTextAsync(file);
        var parsed = new NpgsqlConnectionStringBuilder(cluster);
        if (parsed.Host != "127.0.0.1" || parsed.Port != 15489 || parsed.Database != "postgres") throw new InvalidOperationException("The Assistant suite requires its isolated approved PostgreSQL endpoint.");
        folder = Path.Combine(Path.GetDirectoryName(file)!, prefix); Directory.CreateDirectory(folder);
        foreach (var purpose in new[] { "identity", "evaluation", "operator", "ingest", "review", "ai-audit", "am-audit", "product", "platform", "tester" }) tokens.Add(purpose, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        await File.WriteAllTextAsync(Path.Combine(folder, "operator"), Token("operator"));
        await using (var connection = new NpgsqlConnection(cluster))
        {
            await connection.OpenAsync();
            foreach (var service in new[] { "ai", "audit", "am" })
            {
                var name = prefix + "_" + service;
                var ownerPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                var runtimePassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                await connection.ExecuteAsync($"CREATE ROLE {name}_owner LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{ownerPassword}'; CREATE ROLE {name}_runtime LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{runtimePassword}';");
                await connection.ExecuteAsync($"CREATE DATABASE {name} OWNER {name}_owner;");
                await connection.ExecuteAsync($"REVOKE ALL ON DATABASE {name} FROM PUBLIC; GRANT CONNECT ON DATABASE {name} TO {name}_runtime;");
                owners[service] = new NpgsqlConnectionStringBuilder(cluster) { Database = name, Username = name + "_owner", Password = ownerPassword }.ConnectionString;
                runtimes[service] = new NpgsqlConnectionStringBuilder(cluster) { Database = name, Username = name + "_runtime", Password = runtimePassword }.ConnectionString;
                var path = service switch { "ai" => "services/ai-intelligence/migrations/assistant-feedback", "audit" => "services/audit/migrations/platform", _ => "build/uat/dependencies/source/tools/Monergy.AccessManagement.Migrations/platform-migrations" };
                var result = DeployChanges.To.PostgresqlDatabase(owners[service]).JournalToPostgresqlTable("public", "assistant_test_migrations")
                    .WithScriptsFromFileSystem(Path.Combine(Root, path), new DbUp.ScriptProviders.FileSystemScriptOptions { IncludeSubDirectories = false }).WithTransaction().Build().PerformUpgrade();
                if (!result.Successful) throw new InvalidOperationException("Disposable owner migration failed.", result.Error);
                await using var owner = new NpgsqlConnection(owners[service]); await owner.OpenAsync();
                var schema = service switch { "ai" => "ai_feedback", "audit" => "audit_platform", _ => "am_platform" };
                var grants = $"REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT USAGE ON SCHEMA {schema} TO {name}_runtime; GRANT SELECT ON ALL TABLES IN SCHEMA {schema} TO {name}_runtime;";
                grants += service switch
                {
                    "ai" => $"GRANT INSERT,UPDATE,DELETE ON ai_feedback.topics,ai_feedback.operations,ai_feedback.counters TO {name}_runtime; GRANT INSERT,DELETE ON ai_feedback.audit_recovery_operations TO {name}_runtime; GRANT INSERT ON ai_feedback.audit_outbox TO {name}_runtime; GRANT UPDATE(attempt_count,recovery_count,next_attempt_at,receipt,delivered_at) ON ai_feedback.audit_outbox TO {name}_runtime; GRANT EXECUTE ON FUNCTION ai_feedback.purge_delivered_audit() TO {name}_runtime;",
                    "audit" => $"GRANT INSERT ON ALL TABLES IN SCHEMA audit_platform TO {name}_runtime;",
                    _ => $"GRANT INSERT,UPDATE ON am_platform.policy,am_platform.principal_references,am_platform.reviewer_grants,am_platform.audit_delivery TO {name}_runtime; GRANT INSERT ON am_platform.command_results,am_platform.audit_outbox TO {name}_runtime;",
                };
                await owner.ExecuteAsync(grants);
            }
        }
        var config = Settings(); Configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        credentials = new FilePlatformCredentialStore(Path.Combine(folder, "platform-credentials.json"), "reviewer@example.test", Token("tester"));
        var activation = await credentials.IssueActivationAsync("reviewer@example.test", Token("tester"), default);
        var password = "Synthetic-only-" + Guid.NewGuid().ToString("N") + "aA1!";
        await credentials.ActivateAsync(activation.Ticket, password, default);
        Sessions = new PlatformAdministratorSessions(credentials);
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(await Sessions.EstablishAsync("reviewer@example.test", password, default), AssistantContract.Json)))
            session = document.RootElement.GetProperty("session").GetString()!;

        var gateway = Builder(4173, config);
        gateway.Services.AddSingleton(Sessions);
        gateway.Services.AddSingleton<ITenantRegistry>(new FileTenantRegistry(Path.Combine(folder, "registry.json")));
        gateway.Services.AddAssistant();
        var gatewayApp = gateway.Build(); gatewayApp.UseAssistantPlatformIdentity(); gatewayApp.UseTrustedEntryRouting(); gatewayApp.MapAssistant();
        await StartAsync(gatewayApp);
        var am = Builder(5088, config); am.Services.AddSingleton(TimeProvider.System); am.Services.AddPlatformReviewerAuthorization();
        am.Services.RemoveAll<IHostedService>();
        var amApp = am.Build(); amApp.MapPlatformReviewerAuthorization(); await StartAsync(amApp);
        var audit = Builder(5102, config); audit.Services.AddPlatformAudit();
        var auditApp = audit.Build();
        auditApp.Use(async (http, next) => { if (AuditTransportUnavailable) http.Abort(); else await next(http); });
        auditApp.MapPlatformAudit(); await StartAsync(auditApp);
        var ai = Builder(5119, config); ai.AddAssistantFeedback(); ai.Services.RemoveAll<IHostedService>();
        var aiApp = ai.Build();
        aiApp.Use(async (http, next) =>
        {
            var failure = NextOwnerFailure;
            if ((!LoseNextOwnerResponse && failure is null) || http.Request.Path != LoseNextOwnerResponsePath) { await next(http); return; }
            NextOwnerFailure = null;
            LoseNextOwnerResponse = false; LoseNextOwnerResponsePath = "/internal/assistant-feedback/v1/topics";
            var actual = http.Response.Body; await using var held = new MemoryStream(); http.Response.Body = held;
            await next(http); http.Response.Body = actual;
            if (failure is { } replacement)
            {
                http.Response.Clear(); http.Response.StatusCode = replacement.Status; http.Response.ContentType = "application/json";
                await http.Response.WriteAsync(replacement.Body);
            }
            else http.Abort();
        });
        aiApp.MapAssistantFeedback(); await StartAsync(aiApp);
    }
    private Dictionary<string, string?> Settings() => new(StringComparer.Ordinal)
    {
        ["Monergy:ExecutionZone"] = "LOCAL",
        ["Monergy:Assistant:Enabled"] = "true", ["Monergy:Assistant:IdentityValidationToken"] = Token("identity"),
        ["Monergy:Assistant:CataloguePath"] = CatalogueFile, ["Monergy:Assistant:CatalogueSha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(CatalogueFile))),
        ["Monergy:Assistant:ProductToken"] = Token("product"), ["Monergy:Assistant:PlatformToken"] = Token("platform"),
        ["Monergy:Assistant:FeedbackIngestToken"] = Token("ingest"), ["Monergy:Assistant:FeedbackReviewToken"] = Token("review"),
        ["Monergy:AssistantFeedback:Enabled"] = "true", ["Monergy:AssistantFeedback:ConnectionString"] = Connection("ai"),
        ["Monergy:AssistantFeedback:IngestToken"] = Token("ingest"), ["Monergy:AssistantFeedback:ReviewToken"] = Token("review"),
        ["Monergy:AssistantFeedback:PlatformEvaluationToken"] = Token("evaluation"), ["Monergy:AssistantFeedback:AuditToken"] = Token("ai-audit"),
        ["Monergy:AssistantFeedback:CataloguePath"] = CatalogueFile, ["Monergy:AssistantFeedback:CatalogueSha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(CatalogueFile))),
        ["Monergy:PlatformAudit:Enabled"] = "true", ["Monergy:PlatformAudit:ConnectionString"] = Connection("audit"),
        ["Monergy:PlatformAudit:AmToken"] = Token("am-audit"), ["Monergy:PlatformAudit:FeedbackToken"] = Token("ai-audit"),
        ["Monergy:AccessManagement:PlatformAuthorization:Enabled"] = "true", ["Monergy:AccessManagement:PlatformAuthorization:ConnectionString"] = Connection("am"),
        ["Monergy:AccessManagement:PlatformAuthorization:EvaluationToken"] = Token("evaluation"),
        ["Monergy:AccessManagement:PlatformAuthorization:OperatorCredentialFile"] = Path.Combine(folder, "operator"),
        ["Monergy:AccessManagement:PlatformAuthorization:IdentityUrl"] = "http://127.0.0.1:4173/", ["Monergy:AccessManagement:PlatformAuthorization:IdentityToken"] = Token("identity"),
        ["Monergy:AccessManagement:PlatformAuthorization:AuditUrl"] = "http://127.0.0.1:5102/", ["Monergy:AccessManagement:PlatformAuthorization:AuditToken"] = Token("am-audit"),
    };
    private static WebApplicationBuilder Builder(int port, IDictionary<string, string?> config)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", Args = [] });
        builder.Configuration.AddInMemoryCollection(config); builder.WebHost.UseUrls("http://127.0.0.1:" + port);
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(options => { options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow; options.SerializerOptions.RespectRequiredConstructorParameters = true; });
        return builder;
    }
    private async Task StartAsync(WebApplication app) { await app.StartAsync(); hosts.Add(app); }
    public FeedbackStore Store(IConfiguration? config = null) => new(config ?? Configuration);
    public FeedbackAuthority Authority() => new(Configuration);
    public IConfiguration Modified(params (string Key, string? Value)[] overrides)
    {
        var settings = Settings(); foreach (var (key, value) in overrides) settings[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
    public async Task<string> ContextAsync(string operation, string? resource = null)
    {
        var http = new DefaultHttpContext(); http.Request.Headers["X-Monergy-Platform-Session"] = session;
        return (await Sessions.RequireContextAsync(http, operation, resource)).AuthenticationContext;
    }
    public async Task GrantAsync(bool granted = true)
    {
        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:5088/platform/v1/assistant-feedback/entitlements");
        statusRequest.Headers.Add("X-Monergy-AM-Platform-Operator", Token("operator"));
        using var statusResponse = await Client.SendAsync(statusRequest); statusResponse.EnsureSuccessStatusCode();
        var status = await statusResponse.Content.ReadFromJsonAsync<Am.PlatformEntitlementStatus>(Am.PlatformReviewerContract.Json) ?? throw new InvalidOperationException();
        using var request = new HttpRequestMessage(HttpMethod.Post, statusRequest.RequestUri);
        request.Headers.Add("X-Monergy-AM-Platform-Operator", Token("operator"));
        request.Content = JsonContent.Create(new Am.PlatformEntitlementCommand("1.0.0", Guid.NewGuid().ToString(), "A950", AssistantContract.Capability,
            granted ? "Grant" : "Revoke", status.PolicyRevision, granted ? "REVIEW_ASSIGNMENT" : "REVIEW_REMOVAL"), options: Am.PlatformReviewerContract.Json);
        using var response = await Client.SendAsync(request); response.EnsureSuccessStatusCode();
    }
    public HelpTopicSubmission Submission(string audience = "product", string? topic = null, string text = "How can a person request general product help?")
    {
        using var catalogue = Catalogue();
        return new("1.0.0", text, topic, audience, catalogue.KnowledgeRevision, catalogue.RetrievalRevision, AssistantContract.Hash(Guid.NewGuid().ToString()));
    }
    public async Task<NpgsqlConnection> OpenAsync(string service, bool owner = true) { var connection = new NpgsqlConnection(Connection(service, owner)); await connection.OpenAsync(); return connection; }
    public async Task<HttpResponseMessage> PostAsync(string path, object body, string purpose = "ingest")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:5119/internal/assistant-feedback/v1/" + path);
        request.Headers.Add("X-Monergy-Assistant-Feedback", Token(purpose)); request.Content = JsonContent.Create(body, options: AssistantContract.Json);
        return await Client.SendAsync(request);
    }
    public async Task DisposeAsync()
    {
        for (var index = hosts.Count - 1; index >= 0; index--) { await hosts[index].StopAsync(); await hosts[index].DisposeAsync(); }
        Dispose();
        // Databases and protected credentials are disposable fixtures, retained until this testing task closes.
        // No named volume or previously retained profile is mounted or changed.
    }
    public void Dispose() { Client.Dispose(); credentials?.Dispose(); }
}
