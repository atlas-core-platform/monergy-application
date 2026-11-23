using System.Net.Http.Json;
using System.Text.Json;

namespace Monergy.AccessManagement;

// Run only inside the existing AM internal container/network. No published host port or secret argument.
public static class PlatformReviewerOperator
{
    public static async Task<int> RunAsync(IConfiguration configuration, IHostEnvironment environment, string mode = "--platform-reviewer-command")
    {
        var section = configuration.GetSection("Monergy:AccessManagement:PlatformAuthorization");
        if (!environment.IsDevelopment() || configuration["Monergy:ExecutionZone"] != "LOCAL" ||
            !string.Equals(section["Enabled"], "true", StringComparison.OrdinalIgnoreCase))
        { Console.Error.WriteLine("PLATFORM_OPERATOR_DISABLED"); return 1; }
        try
        {
            var credentialPath = section["OperatorCredentialFile"] ?? "";
            if (!Path.IsPathFullyQualified(credentialPath)) throw new InvalidOperationException();
            var token = (await File.ReadAllTextAsync(credentialPath).ConfigureAwait(false)).Trim();
            var route = new ReferenceDeliveryRoute(new Uri(section["CommandUrl"] ?? "http://127.0.0.1:5088/"), token);
            ReferenceOwnerBoundary.Validate(route);
            string? json = null;
            if (mode != "--platform-reviewer-status")
            {
                var buffer = new char[4097];
                var length = await Console.In.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                PlatformReviewerContract.Require(length is > 0 and <= 4096, "PLATFORM_OPERATOR_INPUT_INVALID");
                json = new string(buffer, 0, length);
            }
            var method = HttpMethod.Get; var path = "platform/v1/assistant-feedback/"; object? body = null;
            switch (mode)
            {
                case "--platform-reviewer-command":
                    method = HttpMethod.Post; path += "entitlements";
                    body = JsonSerializer.Deserialize<PlatformEntitlementCommand>(json!, PlatformReviewerContract.Json) ?? throw new JsonException();
                    break;
                case "--platform-reviewer-status": path += "entitlements"; break;
                case "--platform-reviewer-discover":
                    var discovery = JsonSerializer.Deserialize<DiscoveryInput>(json!, PlatformReviewerContract.Json) ?? throw new JsonException();
                    path += "delivery?limit=" + discovery.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (discovery.After is not null) path += "&after=" + Uri.EscapeDataString(discovery.After);
                    if (discovery.State is not null) path += "&state=" + Uri.EscapeDataString(discovery.State);
                    break;
                case "--platform-reviewer-operation":
                    var lookup = JsonSerializer.Deserialize<OperationInput>(json!, PlatformReviewerContract.Json) ?? throw new JsonException();
                    PlatformReviewerContract.Require(Guid.TryParseExact(lookup.OperationId, "D", out _), "PLATFORM_OPERATOR_INPUT_INVALID");
                    path += "operations/" + lookup.OperationId; break;
                case "--platform-reviewer-resume":
                    var recovery = JsonSerializer.Deserialize<RecoveryInput>(json!, PlatformReviewerContract.Json) ?? throw new JsonException();
                    PlatformReviewerContract.Require(Guid.TryParseExact(recovery.EventId, "D", out _), "PLATFORM_OPERATOR_INPUT_INVALID");
                    method = HttpMethod.Post; path += "delivery/" + recovery.EventId + "/resume";
                    body = new PlatformAuditRecoveryRequest(recovery.OperationId, recovery.ExpectedRecoveryCount); break;
                default: throw new PlatformAuthorizationException("PLATFORM_OPERATOR_INPUT_INVALID", 400);
            }
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
            // A discovery page contains at most 100 bounded metadata rows; allow the complete advertised page.
            { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 65_536 };
            using var request = new HttpRequestMessage(method, new Uri(route.Endpoint, path));
            request.Headers.Add("X-Monergy-AM-Platform-Operator", token);
            if (body is not null) request.Content = JsonContent.Create(body, options: PlatformReviewerContract.Json);
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            { Console.Error.WriteLine("PLATFORM_OPERATOR_COMMAND_REJECTED HTTP " + (int)response.StatusCode); return 1; }
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            object result = mode switch
            {
                "--platform-reviewer-command" => JsonSerializer.Deserialize<PlatformEntitlementResult>(content, PlatformReviewerContract.Json)!,
                "--platform-reviewer-status" => JsonSerializer.Deserialize<PlatformEntitlementStatus>(content, PlatformReviewerContract.Json)!,
                "--platform-reviewer-discover" => JsonSerializer.Deserialize<PlatformAuditPendingPage>(content, PlatformReviewerContract.Json)!,
                "--platform-reviewer-operation" => JsonSerializer.Deserialize<PlatformAuditDeliveryStatus[]>(content, PlatformReviewerContract.Json)!,
                "--platform-reviewer-resume" => JsonSerializer.Deserialize<PlatformAuditRecoveryResult>(content, PlatformReviewerContract.Json)!,
                _ => throw new InvalidOperationException()
            };
            if (result is null) throw new InvalidOperationException();
            Console.WriteLine(JsonSerializer.Serialize(result, PlatformReviewerContract.Json));
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException or IOException or
            UnauthorizedAccessException or JsonException or HttpRequestException or PlatformAuthorizationException or OperationCanceledException)
        { Console.Error.WriteLine("PLATFORM_OPERATOR_COMMAND_UNAVAILABLE"); return 1; }
    }
    private sealed record DiscoveryInput(string? After, int Limit, string? State);
    private sealed record OperationInput(string OperationId);
    private sealed record RecoveryInput(string EventId, string OperationId, int ExpectedRecoveryCount);
}
