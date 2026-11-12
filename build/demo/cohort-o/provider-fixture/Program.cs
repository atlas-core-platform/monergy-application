using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length != 4)
    throw new InvalidOperationException("Expected port, certificate, protected authorization and receipt paths.");

var port = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
var certificate = Path.GetFullPath(args[1]);
var authorization = JsonSerializer.Deserialize<ProviderAuthorization>(await File.ReadAllTextAsync(args[2]))
    ?? throw new InvalidOperationException("Protected provider authorization is invalid.");
var receipts = Path.GetFullPath(args[3]);
Directory.CreateDirectory(Path.GetDirectoryName(receipts)!);
var gate = new SemaphoreSlim(1, 1);
var calls = File.Exists(receipts) ? File.ReadLines(receipts).Count(line => !string.IsNullOrWhiteSpace(line)) : 0;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.ListenAnyIP(port, listen => listen.UseHttps(certificate)));
var app = builder.Build();

app.MapGet("/health", () => Results.Json(new { status = "ready", mode = "PROTECTED_LOCAL_CONTRACT" }));
app.MapPost("/v1/chat/completions", async (HttpContext http) =>
{
    http.Response.Headers.CacheControl = "no-store";
    var supplied = http.Request.Headers.Authorization.ToString();
    var expected = "Bearer " + authorization.ApiKey;
    if (!Fixed(supplied, expected)) return Results.Json(new { error = "provider.authorization.denied" }, statusCode: 401);
    if (http.Request.ContentLength is > 65_536) return Results.Json(new { error = "provider.request.too-large" }, statusCode: 413);

    using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
    var root = body.RootElement;
    Exact(root, "model", "store", "max_completion_tokens", "response_format", "messages");
    var messages = root.GetProperty("messages");
    if (messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() != 2)
        throw new InvalidOperationException("Provider fixture requires exact system/user messages.");
    var system = Message(messages[0], "system");
    var user = Message(messages[1], "user");
    using var input = JsonDocument.Parse(user);
    Exact(input.RootElement, "question", "basis", "claims");
    var question = input.RootElement.GetProperty("question").GetString() ?? "";
    var basis = input.RootElement.GetProperty("basis").GetString() ?? "";
    var claims = input.RootElement.GetProperty("claims");
    if (claims.ValueKind != JsonValueKind.Array || claims.GetArrayLength() > 20)
        throw new InvalidOperationException("Provider fixture received an invalid bounded claim list.");
    var claimIds = new List<string>();
    foreach (var claim in claims.EnumerateArray())
    {
        Exact(claim, "id", "text");
        var id = claim.GetProperty("id").GetString() ?? throw new InvalidOperationException("Claim ID is required.");
        var text = claim.GetProperty("text").GetString() ?? throw new InvalidOperationException("Claim text is required.");
        if (id.Length is < 1 or > 256 || text.Length > 2000) throw new InvalidOperationException("Claim is outside the provider contract bounds.");
        claimIds.Add(id);
    }

    string content;
    string responseKind;
    if (system.Contains("exactly mode and query", StringComparison.Ordinal))
    {
        var unsupported = question.Contains("advice", StringComparison.OrdinalIgnoreCase)
            || question.Contains("forecast", StringComparison.OrdinalIgnoreCase)
            || question.Contains("predict", StringComparison.OrdinalIgnoreCase)
            || question.Contains("credit score", StringComparison.OrdinalIgnoreCase);
        var mode = unsupported ? "unsupported" : basis == "net-worth" ? "calculation" : "lookup";
        content = JsonSerializer.Serialize(new { mode, query = unsupported ? "unsupported" : "investment" });
        responseKind = "PLAN_" + mode.ToUpperInvariant();
    }
    else
    {
        content = JsonSerializer.Serialize(new { state = "SOURCES", claimIds });
        responseKind = basis == "summary" ? "SUMMARY_SIX_CLAIMS" : "SOURCE_SELECTION";
    }

    await gate.WaitAsync(http.RequestAborted);
    try
    {
        calls++;
        var receipt = JsonSerializer.Serialize(new
        {
            sequence = calls,
            at = DateTimeOffset.UtcNow,
            requestSchema = "chat-completions-v1",
            authorization = "VALIDATED_NOT_RETAINED",
            model = root.GetProperty("model").GetString(),
            basis,
            questionSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(question))),
            claimCount = claimIds.Count,
            responseKind,
            privacy = new
            {
                outerKeys = ProviderSchemas.Outer,
                userKeys = ProviderSchemas.User,
                claimKeys = ProviderSchemas.Claim,
                tenantId = false,
                customerId = false,
                sessionToken = false,
                documentInternalId = false,
                provenanceId = false,
                evidenceInternalId = false,
                apiCredential = false
            }
        });
        await File.AppendAllTextAsync(receipts, receipt + Environment.NewLine, Encoding.UTF8, http.RequestAborted);
    }
    finally { gate.Release(); }

    return Results.Json(new
    {
        model = "cohort-o-local-contract-v1",
        choices = new[] { new { finish_reason = "stop", message = new { content } } }
    });
});

await app.RunAsync();

static string Message(JsonElement value, string expectedRole)
{
    Exact(value, "role", "content");
    if (value.GetProperty("role").GetString() != expectedRole) throw new InvalidOperationException("Provider message role is invalid.");
    return value.GetProperty("content").GetString() ?? throw new InvalidOperationException("Provider message content is required.");
}

static void Exact(JsonElement value, params string[] expected)
{
    if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Provider object is invalid.");
    var actual = value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
    var wanted = expected.Order(StringComparer.Ordinal).ToArray();
    if (!actual.SequenceEqual(wanted, StringComparer.Ordinal))
        throw new InvalidOperationException("Provider request contains an unexpected field.");
}

static bool Fixed(string left, string right)
{
    var a = Encoding.UTF8.GetBytes(left); var b = Encoding.UTF8.GetBytes(right);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}

sealed record ProviderAuthorization(string ApiKey);

static class ProviderSchemas
{
    public static readonly string[] Outer = ["model", "store", "max_completion_tokens", "response_format", "messages"];
    public static readonly string[] User = ["question", "basis", "claims"];
    public static readonly string[] Claim = ["id", "text"];
}
