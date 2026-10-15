using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Monergy.AccessManagement;
using Monergy.AccessManagement.DeliveryReference;
using Npgsql;

if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Development" ||
    Environment.GetEnvironmentVariable("MONERGY_AM_REFERENCE_DELIVERY") != "1")
{
    Console.Error.WriteLine("Delivery reference tools require explicit Development authorization.");
    return 1;
}
if (args.Length == 0) return 1;
// Docker Desktop mounts only this worker's delivery credentials. They never
// become command-line arguments or image layers.
if (Environment.GetEnvironmentVariable("MONERGY_AM_DELIVERY_CONFIGURATION") is { } configPath)
{
    var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(configPath))
        ?? throw new InvalidOperationException("Delivery configuration is required.");
    var allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "MONERGY_AM_T001_DELIVERY", "MONERGY_AM_T002_DELIVERY",
        "MONERGY_AM_REFERENCE_AUTHORIZATION_URL", "MONERGY_AM_REFERENCE_AUTHORIZATION_TOKEN",
        "MONERGY_AM_REFERENCE_SESSIONS_URL", "MONERGY_AM_REFERENCE_SESSIONS_TOKEN",
        "MONERGY_AM_REFERENCE_AUDIT_URL", "MONERGY_AM_REFERENCE_AUDIT_TOKEN",
    };
    if (values.Keys.Any(key => !allowed.Contains(key))) throw new InvalidOperationException("Unexpected delivery configuration key.");
    foreach (var (key, value) in values) Environment.SetEnvironmentVariable(key, value);
}
if (args[0] != "receiver")
{
    try
    {
        if (args.Length < 2) return 1;
        var tenantId = args[1];
        AccessInput.Identifier(tenantId, 64);
        await using var connections = new ReferenceTenantConnectionFactory(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [tenantId] = Required("MONERGY_AM_" + tenantId + "_DELIVERY")
        });
        using var client = ReferenceHttpEventTransport.CreateClient();
        var routes = new Dictionary<string, ReferenceDeliveryRoute>(StringComparer.Ordinal);
        foreach (var destination in (string[])["authorization", "sessions", "audit"])
            routes.Add(destination,
                new ReferenceDeliveryRoute(
                new Uri(Environment.GetEnvironmentVariable("MONERGY_AM_REFERENCE_" + destination.ToUpperInvariant() + "_URL") ?? Required("MONERGY_AM_REFERENCE_RECEIVER_URL")),
                Environment.GetEnvironmentVariable("MONERGY_AM_REFERENCE_" + destination.ToUpperInvariant() + "_TOKEN") ?? Required("MONERGY_AM_REFERENCE_TRANSPORT_TOKEN")));
        var transport = new ReferenceHttpEventTransport(client, routes);
        var dispatcher = new PostgresOutboxDispatcher(connections, transport);
        if (args[0] == "watch")
        {
            using var stopping = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
            using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
                signal => { signal.Cancel = true; stopping.Cancel(); });
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await dispatcher.DispatchAsync(tenantId, 100, stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
                catch (Exception failure) when (failure is NpgsqlException or AccessStoreUnavailableException or HttpRequestException)
                {
                    Console.Error.WriteLine("Delivery dependency unavailable; durable work will be retried.");
                }
                try { await Task.Delay(TimeSpan.FromSeconds(2), stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
            }
            return 0;
        }
        if (args[0] == "dispatch")
            Console.WriteLine(JsonSerializer.Serialize(await dispatcher.DispatchAsync(tenantId,
                args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 100, default), AccessEventProtocol.Json));
        else if (args[0] == "redrive" && args.Length == 7)
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                changed = await dispatcher.RedriveAsync(tenantId,
                new(Guid.Parse(args[5]), Guid.Parse(args[2]), args[3], int.Parse(args[4], CultureInfo.InvariantCulture), args[6]), default)
            }, AccessEventProtocol.Json));
        else if (args[0] != "status") return 1;
        Console.WriteLine(JsonSerializer.Serialize(await dispatcher.StatusAsync(tenantId, default), AccessEventProtocol.Json));
        return 0;
    }
    catch (Exception exception) when (exception is NpgsqlException or AccessStoreUnavailableException or AccessAdministrationException or ArgumentException or InvalidOperationException or FormatException)
    {
        Console.Error.WriteLine("Reference delivery operation failed. Check guarded configuration and operator state.");
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args[1..]);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65_536);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
});
builder.Services.AddSingleton(_ => new ReferenceDeliveryReceiver(Required("MONERGY_AM_REFERENCE_RECEIVER_RUNTIME")));
var expectedToken = Required("MONERGY_AM_REFERENCE_TRANSPORT_TOKEN");
if (expectedToken.Length is < 32 or > 256 || expectedToken.Any(c => !char.IsAsciiLetterOrDigit(c))) return 1;
var loseReceipt = Environment.GetEnvironmentVariable("MONERGY_AM_REFERENCE_LOSE_RECEIPT_ONCE") == "1" ? 1 : 0;
var app = builder.Build();
app.MapGet("/health/ready", async (ReferenceDeliveryReceiver receiver, CancellationToken ct) =>
    await receiver.ReadyAsync(ct).ConfigureAwait(false) ? Results.Ok(new { state = "REFERENCE_READY" }) : Results.StatusCode(503));
app.MapPost("/reference/events/{destination}", async (string destination, AccessEventEnvelope message, HttpContext http, ReferenceDeliveryReceiver receiver) =>
{
    if (!ValidToken(http, expectedToken)) return (IResult)Results.StatusCode(403);
    try
    {
        var receipt = await receiver.ConsumeAsync(http.Request.Headers["X-Monergy-Reference-Tenant"].ToString(), destination, message, http.RequestAborted).ConfigureAwait(false);
        if (Interlocked.Exchange(ref loseReceipt, 0) == 1)
        {
            http.Abort();
            return Results.Empty;
        }
        return Results.Ok(receipt);
    }
    catch (EventDeliveryException exception) { return Results.Json(new { error = exception.Code }, statusCode: 409); }
    catch (AccessAdministrationException exception) { return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode); }
    catch (Exception exception) when (exception is NpgsqlException or AccessStoreUnavailableException) { return Results.StatusCode(503); }
});
app.MapPost("/reference/{tenant}/sessions", async (string tenant, ReferenceSessionSeed seed, HttpContext http, ReferenceDeliveryReceiver receiver) =>
{
    if (!ValidToken(http, expectedToken)) return (IResult)Results.StatusCode(403);
    try
    {
        await receiver.SeedSessionAsync(tenant, seed, http.RequestAborted).ConfigureAwait(false);
        return Results.Ok(new { state = "REFERENCE_SESSION_SEEDED" });
    }
    catch (AccessAdministrationException exception) { return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode); }
    catch (Exception exception) when (exception is NpgsqlException or AccessStoreUnavailableException) { return Results.StatusCode(503); }
});
app.MapGet("/reference/{tenant}/state", async (string tenant, HttpContext http, ReferenceDeliveryReceiver receiver) =>
    ValidToken(http, expectedToken)
        ? Results.Ok(await receiver.StateAsync(tenant, http.RequestAborted).ConfigureAwait(false))
        : (IResult)Results.StatusCode(403));
await app.RunAsync();
return 0;

static string Required(string key) => Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException("Reference delivery configuration missing.");
static bool ValidToken(HttpContext http, string expected)
{
    var actual = http.Request.Headers["X-Monergy-Reference-Transport"].ToString();
    return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
}
