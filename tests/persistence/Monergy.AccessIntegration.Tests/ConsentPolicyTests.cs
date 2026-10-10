using Monergy.Platform;
using Monergy.Services.Consent;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class ConsentPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    [Fact]
    public void ConsentIsCapabilitySpecificAndCanonicalForReplay()
    {
        var result = CustomerConsentPolicy.Validate(Grant(), Now);
        Assert.Equal(["financial-profile.profile.read", "search.query.execute"], result.CapabilityIds);
        Assert.Equal(Grant().ExpiresAt, result.ExpiresAt);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("too-long")]
    [InlineData("purpose")]
    [InlineData("unknown-capability")]
    [InlineData("duplicates")]
    [InlineData("empty")]
    [InlineData("customer")]
    [InlineData("revision")]
    public void InvalidOrOverbroadConsentIsRejected(string field)
    {
        var request = field switch
        {
            "expired" => Grant() with { ExpiresAt = Now },
            "too-long" => Grant() with { ExpiresAt = Now.AddDays(91) },
            "purpose" => Grant() with { Purpose = "marketing" },
            "unknown-capability" => Grant() with { CapabilityIds = ["audit.evidence.read"] },
            "duplicates" => Grant() with { CapabilityIds = ["search.query.execute", "search.query.execute"] },
            "empty" => Grant() with { CapabilityIds = [] },
            "customer" => Grant() with { CustomerId = "../T002" },
            _ => Grant() with { ExpectedVersion = 0 },
        };
        Assert.Throws<TenantBoundaryException>(() => CustomerConsentPolicy.Validate(request, Now));
    }

    [Theory]
    [InlineData("T001", "A100", "C001", true)]
    [InlineData("T002", "A100", "C001", false)]
    [InlineData("T001", "A900", "C001", false)]
    [InlineData("T001", "A100", "C002", false)]
    public void OnlyVerifiedCustomerOwnerCanGrantOrRevoke(string tenant, string actor, string customer, bool allowed)
    {
        var owner = new CustomerOwnerContext("T001", "C001", "A100", 1, true);
        if (allowed) PostgresCustomerConsent.RequireOwner(tenant, owner, actor, customer);
        else Assert.Equal(403, Assert.Throws<TenantBoundaryException>(() => PostgresCustomerConsent.RequireOwner(tenant, owner, actor, customer)).Status);
    }

    private static ConsentGrantWrite Grant() => new("request-1", 1, "C001", "A300", CustomerConsentPolicy.Purpose,
        ["search.query.execute", "financial-profile.profile.read"], Now.AddHours(1));
}
