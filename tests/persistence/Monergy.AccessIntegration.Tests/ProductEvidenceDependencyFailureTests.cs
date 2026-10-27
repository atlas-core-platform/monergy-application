using System.Net;
using Monergy.UatGateway;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class ProductEvidenceDependencyFailureTests
{
    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "EVIDENCE_SCAN_REJECTED", "EVIDENCE_SCAN_REJECTED")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "EVIDENCE_SCAN_UNAVAILABLE", "EVIDENCE_SCAN_UNAVAILABLE")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "EVIDENCE_STORAGE_UNAVAILABLE", "EVIDENCE_STORAGE_UNAVAILABLE")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "untrusted-owner-details", "EVIDENCE_STAGING_UNAVAILABLE")]
    [InlineData(HttpStatusCode.Conflict, "EVIDENCE_STORAGE_UNAVAILABLE", "EVIDENCE_OPERATION_CONFLICT")]
    public void OnlyRecognizedOwnerErrorsAreTranslated(HttpStatusCode status, string ownerCode, string expected)
    {
        Assert.Equal(expected, ProductEvidenceDependencyFailure.StageCode(status, ownerCode));
    }

    [Fact]
    public void AnUpstreamForbiddenIsNeverMistakenForCustomerAuthorization()
    {
        Assert.Equal("EVIDENCE_STAGING_FAILED",
            ProductEvidenceDependencyFailure.StageCode(HttpStatusCode.Forbidden, "EVIDENCE_SCAN_UNAVAILABLE"));
    }
}
