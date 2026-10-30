using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monergy.Platform;

namespace Monergy.Services.IntegrationGateway;

public sealed record DriveFailureDecision(string Code, int Status, bool Retryable, int? RetryAfterSeconds);

public static class DriveFailurePolicy
{
    public const int RateLimitRetryAfterSeconds = 60;

    public static DriveFailureDecision Classify(TenantBoundaryException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var code = failure.Code.Length is > 0 and <= 100 && failure.Code.All(character =>
            character == '_' || char.IsAsciiLetterUpper(character)) ? failure.Code : "DRIVE_UNAVAILABLE";
        var status = failure.Status is >= 400 and <= 599 ? failure.Status : 503;
        var rateLimited = code == "DRIVE_RATE_LIMITED" && status == StatusCodes.Status429TooManyRequests;
        var retryable = rateLimited || code == "DRIVE_UNAVAILABLE" && status == StatusCodes.Status503ServiceUnavailable;
        return new(code, status, retryable, rateLimited ? RateLimitRetryAfterSeconds : null);
    }

    public static IResult Result(HttpContext http, TenantBoundaryException failure, string provider)
    {
        var decision = Classify(failure);
        if (decision.RetryAfterSeconds is int seconds)
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        Log(http, provider, decision);
        return Results.Json(new { error = decision.Code }, statusCode: decision.Status);
    }

    public static IResult Unavailable(HttpContext http, string provider)
    {
        var decision = new DriveFailureDecision("DRIVE_UNAVAILABLE", StatusCodes.Status503ServiceUnavailable, true, null);
        Log(http, provider, decision);
        return Results.Json(new { error = decision.Code }, statusCode: decision.Status);
    }

    private static void Log(HttpContext http, string provider, DriveFailureDecision decision) =>
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Monergy.ProviderFailure").LogWarning(
            "Provider operation did not complete. Provider {Provider}; failure {FailureCode}; status {Status}; retryable {Retryable}.",
            provider, decision.Code, decision.Status, decision.Retryable);
}
