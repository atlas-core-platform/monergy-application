using System.Net.Http.Json;
using System.Text.Json;

namespace Monergy.AccessManagement;

// Run only inside the existing AM internal container/network. No published host port or secret argument.
public static class PlatformReviewerOperator
{
    public static async Task<int> RunAsync(IConfiguration configuration, IHostEnvironment environment)
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
            var buffer = new char[4097];
            var length = await Console.In.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            PlatformReviewerContract.Require(length is > 0 and <= 4096, "PLATFORM_OPERATOR_INPUT_INVALID");
            var command = JsonSerializer.Deserialize<PlatformEntitlementCommand>(new string(buffer, 0, length), PlatformReviewerContract.Json);
            PlatformReviewerContract.Require(command is not null, "PLATFORM_OPERATOR_INPUT_INVALID");
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
            { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 16_384 };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(route.Endpoint, "platform/v1/assistant-feedback/entitlements"));
            request.Headers.Add("X-Monergy-AM-Platform-Operator", token);
            request.Content = JsonContent.Create(command, options: PlatformReviewerContract.Json);
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            { Console.Error.WriteLine("PLATFORM_OPERATOR_COMMAND_REJECTED HTTP " + (int)response.StatusCode); return 1; }
            var result = await response.Content.ReadFromJsonAsync<PlatformEntitlementResult>(PlatformReviewerContract.Json).ConfigureAwait(false);
            if (result is null) throw new InvalidOperationException();
            Console.WriteLine(JsonSerializer.Serialize(result, PlatformReviewerContract.Json));
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException or IOException or
            UnauthorizedAccessException or JsonException or HttpRequestException or PlatformAuthorizationException or OperationCanceledException)
        { Console.Error.WriteLine("PLATFORM_OPERATOR_COMMAND_UNAVAILABLE"); return 1; }
    }
}
