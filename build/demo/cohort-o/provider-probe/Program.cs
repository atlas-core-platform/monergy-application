using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
{ Timeout = TimeSpan.FromSeconds(10) };
try
{
    var profile = JsonDocument.Parse(await File.ReadAllTextAsync("/run/profile.json"));
    var identity = profile.RootElement.GetProperty("identities").EnumerateArray().Single(x =>
        x.GetProperty("tenantId").GetString() == "T001" && x.GetProperty("actorId").GetString() == "A100");
    using var signIn = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:5101/local/v1/tenant-sessions")
    { Content = JsonContent.Create(new { tenantId="T001" }) };
    signIn.Headers.Add("X-Monergy-Reference-Authentication", identity.GetProperty("token").GetString());
    using var signedIn = await client.SendAsync(signIn);
    var session = JsonDocument.Parse(await signedIn.Content.ReadAsStringAsync()).RootElement;
    var config = JsonDocument.Parse(await File.ReadAllTextAsync("/run/integration.json"));
    var modelRoot = config.RootElement.GetProperty("Monergy").GetProperty("AiModel");
    var internalToken = modelRoot.GetProperty("InternalToken").GetString();
    var tenant = modelRoot.GetProperty("Tenants").GetProperty("T001");
    var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
        "T001", tenant.GetProperty("Endpoint").GetString(), tenant.GetProperty("Model").GetString(),
        tenant.GetProperty("ApiKey").GetString(), tenant.GetProperty("ProviderLabel").GetString(),
        tenant.GetProperty("Notice").GetString(), "monergy-basic-qa/4"))));
    using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:5117/internal/local/v1/ai-model/generate")
    { Content = JsonContent.Create(new { phase="plan", question="What investment source is recorded?",
        configurationRevision=revision,
        allowModelProcessing=true, claims=Array.Empty<object>(), basis="financial" }) };
    request.Headers.Add("X-Monergy-Ai-Token", internalToken);
    request.Headers.Add("X-Monergy-Tenant", "T001");
    request.Headers.Add("X-Monergy-Session", session.GetProperty("authenticationContextId").GetString());
    using var response = await client.SendAsync(request);
    Console.WriteLine($"STATUS={(int)response.StatusCode}");
    Console.WriteLine(await response.Content.ReadAsStringAsync());
}
catch (Exception failure)
{
    Console.WriteLine("FAILURE=" + failure.GetType().Name);
    if (failure.InnerException is not null) Console.WriteLine("INNER=" + failure.InnerException.GetType().Name);
    Environment.ExitCode = 1;
}
