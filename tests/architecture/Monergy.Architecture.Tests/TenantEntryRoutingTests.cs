using System.Text;
using Microsoft.AspNetCore.Http;
using Monergy.UatGateway;
using Xunit;

namespace Monergy.Architecture.Tests;

public sealed class TenantEntryRoutingTests
{
    private static readonly TrustedEntryContext Tenant =
        new(EntryHostKind.Tenant, "t002.localhost", "T002", "org-t002", "Tenant 2", "t002");

    [Fact]
    public void ReservedHostsAndSecurityAreasFailClosed()
    {
        Assert.Equal(EntryHostKind.Platform, TenantEntryRouting.ClassifyReservedHost("platform.localhost", "/"));
        Assert.Equal(EntryHostKind.Health, TenantEntryRouting.ClassifyReservedHost("127.0.0.1", "/health/ready"));
        Assert.Null(TenantEntryRouting.ClassifyReservedHost("unknown.localhost", "/"));
        Assert.True(TenantEntryRouting.AllowsPath(EntryHostKind.Platform, "/platform-api/local/v1/context"));
        Assert.False(TenantEntryRouting.AllowsPath(EntryHostKind.Platform, "/identity-api/local/v1/persona-credentials/sign-in"));
        Assert.False(TenantEntryRouting.AllowsPath(EntryHostKind.Platform, "/access/users"));
        Assert.True(TenantEntryRouting.AllowsPath(EntryHostKind.Tenant, "/advisor"));
        Assert.False(TenantEntryRouting.AllowsPath(EntryHostKind.Tenant, "/platform-api/local/v1/context"));
        Assert.False(TenantEntryRouting.AllowsPath(EntryHostKind.Tenant, "/platform/settings"));
        Assert.False(TenantEntryRouting.AllowsPath(EntryHostKind.Health, "/"));
    }

    [Fact]
    public void SpoofedTenantHeadersAndQueryAreReplacedByHostnameContext()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Tenant-Id"] = "T001";
        http.Request.Headers["X-Monergy-Tenant"] = "T001";
        http.Request.QueryString = new QueryString("?TenantId=T001&email=user%40example.test");
        TenantEntryRouting.ApplyTrustedTenant(http, Tenant);
        Assert.False(http.Request.Headers.ContainsKey("X-Tenant-Id"));
        Assert.Equal("T002", http.Request.Headers["X-Monergy-Tenant"].ToString());
        Assert.Equal("T002", http.Request.Query["tenantId"].ToString());
        Assert.Equal("user@example.test", http.Request.Query["email"].ToString());
    }

    [Fact]
    public void BrowserTenantBodyIsOverwrittenWithoutAddingFieldsToUnrelatedContracts()
    {
        var http = new DefaultHttpContext();
        http.Items[TrustedEntryContext.ItemKey] = Tenant;
        var spoofed = TenantEntryRouting.ReplaceTopLevelTenantId(http,
            Encoding.UTF8.GetBytes("{\"TenantId\":\"T001\",\"email\":\"user@example.test\"}"));
        Assert.Contains("\"tenantId\":\"T002\"", Encoding.UTF8.GetString(spoofed), StringComparison.Ordinal);
        Assert.DoesNotContain("\"TenantId\"", Encoding.UTF8.GetString(spoofed), StringComparison.Ordinal);
        var unrelated = TenantEntryRouting.ReplaceTopLevelTenantId(http, Encoding.UTF8.GetBytes("{\"value\":1}"));
        Assert.Equal("{\"value\":1}", Encoding.UTF8.GetString(unrelated));
    }
}
