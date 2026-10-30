using Monergy.Platform;
using Monergy.Services.IntegrationGateway;
using Xunit;

namespace Monergy.IntegrationGateway.Tests;

public sealed class DriveFailurePolicyTests
{
    [Fact]
    public void RateLimitUsesTheGovernedManualRetryDelay()
    {
        var result = DriveFailurePolicy.Classify(new TenantBoundaryException("DRIVE_RATE_LIMITED", 429));
        Assert.Equal(new("DRIVE_RATE_LIMITED", 429, true, 60), result);
    }

    [Theory]
    [InlineData("DRIVE_RECONNECT_REQUIRED")]
    [InlineData("DRIVE_PERMISSION_REQUIRED")]
    [InlineData("DRIVE_RESPONSE_INVALID")]
    public void PermissionAndInvalidResponsesAreNotBlindlyRetried(string code)
    {
        var result = DriveFailurePolicy.Classify(new TenantBoundaryException(code, 503));
        Assert.False(result.Retryable);
        Assert.Null(result.RetryAfterSeconds);
    }

    [Fact]
    public void UnavailableOwnerIsRetryableOnlyAfterStateReview()
    {
        Assert.Equal(new("DRIVE_UNAVAILABLE", 503, true, null),
            DriveFailurePolicy.Classify(new TenantBoundaryException("DRIVE_UNAVAILABLE", 503)));
    }

    [Fact]
    public void PrivateOrMalformedFailureCodesFailClosed()
    {
        var result = DriveFailurePolicy.Classify(new TenantBoundaryException("provider-private-detail", 200));
        Assert.Equal(new("DRIVE_UNAVAILABLE", 503, true, null), result);
    }
}
