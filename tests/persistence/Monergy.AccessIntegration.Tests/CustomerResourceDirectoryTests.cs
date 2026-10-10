using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Services.CustomerIdentity;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class CustomerResourceDirectoryTests
{
    [Fact]
    public void TenantSessionCompositionResolvesLabelsFromMatchingOwnerBindings()
    {
        var services = new ServiceCollection();
        var configuration = Configuration();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddTenantSessionAuthority(configuration);
        using var provider = services.BuildServiceProvider();
        var directory = provider.GetRequiredService<ITenantCustomerResourceDirectory>();
        var customer = Assert.Single(directory.List("T001"));
        Assert.Equal("reference-customer", customer.ResourceId);
        Assert.Equal("Reference Customer", customer.DisplayName);
        Assert.Empty(directory.List("T002"));
        Assert.Empty(directory.List("unknown"));
    }

    [Theory]
    [InlineData("Monergy:ExecutionZone", "PRODUCTION")]
    [InlineData("Monergy:AccessIntegration:Enabled", "false")]
    [InlineData("Monergy:ReferenceAdapters", "true")]
    [InlineData("Monergy:AccessIntegration:CustomerResources:0:TenantId", "T002")]
    [InlineData("Monergy:AccessIntegration:CustomerResources:0:ResourceId", "unbound")]
    [InlineData("Monergy:AccessIntegration:CustomerResources:0:DisplayName", " ")]
    public void InvalidModeOrOwnerBindingFailsClosed(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => new ReferenceTenantCustomerResourceDirectory(Configuration(key, value)));
    }

    [Fact]
    public void MissingDirectoryDoesNotInventResources()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monergy:ExecutionZone"] = "LOCAL",
            ["Monergy:AccessIntegration:Enabled"] = "true",
        }).Build();
        Assert.Empty(new ReferenceTenantCustomerResourceDirectory(configuration).List("T001"));
    }

    private static IConfiguration Configuration(string? key = null, string? value = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
            ["Monergy:AccessIntegration:Enabled"] = "true",
            ["Monergy:TenantBoundary:CustomerTenants:reference-customer"] = "T001",
            ["Monergy:AccessIntegration:CustomerResources:0:TenantId"] = "T001",
            ["Monergy:AccessIntegration:CustomerResources:0:ResourceId"] = "reference-customer",
            ["Monergy:AccessIntegration:CustomerResources:0:DisplayName"] = "Reference Customer",
        };
        if (key is not null) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
