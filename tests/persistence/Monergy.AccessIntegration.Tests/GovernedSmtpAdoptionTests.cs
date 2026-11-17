using Microsoft.Extensions.Configuration;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Infrastructure;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class GovernedSmtpAdoptionTests
{
    [Fact]
    public async Task SmtpAllowsLegacyOnlyBeforeAdoptionAndEveryAdoptedFailureStaysClosed()
    {
        var legacy = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Monergy:CustomerEmailAccess:Enabled"] = "true",
            ["Monergy:CustomerEmailAccess:SmtpHost"] = "legacy-mail.example.test",
            ["Monergy:CustomerEmailAccess:SmtpPort"] = "2525",
            ["Monergy:CustomerEmailAccess:From"] = "legacy-sender@example.test",
            ["Monergy:CustomerEmailAccess:SmtpUser"] = "legacy-user",
            ["Monergy:CustomerEmailAccess:SmtpPassword"] = "legacy-password",
            ["Monergy:CustomerEmailAccess:AllowedRecipients:0"] = "a100@example.test",
            ["Monergy:AccessIntegration:CustomerIdentityDatabases:T001"] = "Host=legacy-db.example.test",
        }).Build();
        var governed = new MutableGovernedReader();
        var owner = new GovernedSmtpConfigurationOwner(governed, legacy);

        var before = await owner.ResolveAsync("T001", default);
        Assert.Equal("legacy-mail.example.test", before.Host);
        Assert.Equal("legacy-password", before.Password);

        governed.Decision = GovernedProviderConfigurationDecision.Ready(Ready("governed-mail.example.test", "governed-password", 1));
        var adopted = await owner.ResolveAsync("T001", default);
        Assert.Equal("governed-mail.example.test", adopted.Host);
        Assert.Equal("governed-password", adopted.Password);

        foreach (var state in new[] {
            GovernedProviderAdoptionStates.Disabled,
            GovernedProviderAdoptionStates.SecretUnavailable, // cleared or absent
            GovernedProviderAdoptionStates.SecretUnavailable, // present but unresolvable
            GovernedProviderAdoptionStates.ConfigurationIncomplete,
            GovernedProviderAdoptionStates.OrganizationMismatch,
        })
        {
            governed.Decision = GovernedProviderConfigurationDecision.Unavailable(state);
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ResolveAsync(
                state == GovernedProviderAdoptionStates.OrganizationMismatch ? "T002" : "T001", default));
        }

        governed.Decision = GovernedProviderConfigurationDecision.Ready(Ready("repaired-mail.example.test", "repaired-password", 8));
        var repaired = await owner.ResolveAsync("T001", default);
        Assert.Equal("repaired-mail.example.test", repaired.Host);
        Assert.Equal("repaired-password", repaired.Password);
        Assert.NotEqual(before.Host, repaired.Host);
    }

    private static GovernedProviderRuntimeConfiguration Ready(string host, string secret, long revision) =>
        new("email", "Organization", "T001", true, revision, "smtp://" + host + ":2525", "governed-user",
            "governed-sender@example.test", null, new Dictionary<string, string> { ["tenantId"] = "T001" }, secret);

    private sealed class MutableGovernedReader : IGovernedProviderConfigurationReader
    {
        public GovernedProviderConfigurationDecision Decision { get; set; } =
            GovernedProviderConfigurationDecision.NeverAdopted();
        public Task<GovernedProviderConfigurationDecision> ReadAsync(string provider, string tenant, CancellationToken ct) =>
            Task.FromResult(Decision);
    }
}
