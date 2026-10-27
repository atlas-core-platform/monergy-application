using Monergy.UatGateway;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class SpaRouteAllowlistTests
{
    [Theory]
    [InlineData("/advisor")]
    [InlineData("/welcome")]
    [InlineData("/")]
    [InlineData("/access")]
    [InlineData("/access/users")]
    [InlineData("/evidence")]
    [InlineData("/settings/sessions")]
    public void OnlyExplicitApplicationDocumentPathsReceiveSpaShell(string path)
    {
        Assert.True(SpaRouteAllowlist.Contains(path));
    }

    [Theory]
    [InlineData("/advisor/")]
    [InlineData("/advisor/private")]
    [InlineData("/Advisor")]
    [InlineData("/welcome/anything")]
    [InlineData("/access-api/v1/advisor/context")]
    [InlineData("/product-api/v1/context")]
    [InlineData("/uat-api/v1/setup")]
    [InlineData("/does-not-exist")]
    [InlineData("/../advisor")]
    public void UnknownApiAndUnlistedRoutesAreNotSpaDocuments(string path)
    {
        Assert.False(SpaRouteAllowlist.Contains(path));
    }

    [Fact]
    public void NullPathCannotReceiveSpaShell() =>
        Assert.False(SpaRouteAllowlist.Contains(null));
}
