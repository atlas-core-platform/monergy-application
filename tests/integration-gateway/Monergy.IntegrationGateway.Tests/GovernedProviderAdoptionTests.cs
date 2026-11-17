using Microsoft.Extensions.Configuration;
using Monergy.Platform;
using Monergy.Services.IntegrationGateway.AiModel;
using GoogleDriveApplication = Monergy.Services.IntegrationGateway.GoogleDrive.DriveConnectionApplication;
using GoogleDriveConfiguration = Monergy.Services.IntegrationGateway.GoogleDrive.DriveConfiguration;
using OneDriveApplication = Monergy.Services.IntegrationGateway.OneDrive.DriveConnectionApplication;
using OneDriveConfiguration = Monergy.Services.IntegrationGateway.OneDrive.DriveConfiguration;
using Xunit;

namespace Monergy.IntegrationGateway.Tests;

public sealed class GovernedProviderAdoptionTests
{
    [Fact]
    public async Task GoogleDriveAllowsLegacyOnlyBeforeAdoptionAndEveryAdoptedFailureStaysClosed()
    {
        var legacy = GoogleLegacy();
        var governed = new MutableGovernedReader();
        var owner = new GoogleDriveApplication(null!, null!, null!, legacy, governed);
        var legacyRevision = new GoogleDriveConfiguration("legacy-google", "legacy-secret",
            "http://localhost:4173/connections-api/local/v1/google-drive/callback").Revision;
        Assert.Equal(legacyRevision, await owner.ReadinessAsync("T001", default));

        var first = Google("governed-google", "governed-secret", 1);
        governed.Decision = GovernedProviderConfigurationDecision.Ready(first);
        Assert.Equal(new GoogleDriveConfiguration(first.ClientId!, first.Secret, first.CallbackUri!).Revision,
            await owner.ReadinessAsync("T001", default));
        await AssertAdoptedFailures(owner.ReadinessAsync);

        var repaired = Google("repaired-google", "repaired-secret", 8);
        governed.Decision = GovernedProviderConfigurationDecision.Ready(repaired);
        var repairedRevision = await owner.ReadinessAsync("T001", default);
        Assert.Equal(new GoogleDriveConfiguration(repaired.ClientId!, repaired.Secret, repaired.CallbackUri!).Revision,
            repairedRevision);
        Assert.NotEqual(legacyRevision, repairedRevision);

        async Task AssertAdoptedFailures(Func<string, CancellationToken, Task<string>> resolve)
        {
            foreach (var state in AdoptedFailures())
            {
                governed.Decision = GovernedProviderConfigurationDecision.Unavailable(state);
                await Assert.ThrowsAsync<InvalidOperationException>(() => resolve(
                    state == GovernedProviderAdoptionStates.OrganizationMismatch ? "T002" : "T001", default));
            }
        }
    }

    [Fact]
    public async Task OneDriveAllowsLegacyOnlyBeforeAdoptionAndEveryAdoptedFailureStaysClosed()
    {
        var legacy = OneDriveLegacy();
        var governed = new MutableGovernedReader();
        var owner = new OneDriveApplication(null!, null!, null!, legacy, governed);
        var legacyRevision = new OneDriveConfiguration("legacy-onedrive", "legacy-secret",
            "http://localhost:4173/connections-api/local/v1/onedrive/callback").Revision;
        Assert.Equal(legacyRevision, await owner.ReadinessAsync("T001", default));

        var first = OneDrive("governed-onedrive", "governed-secret", 1);
        governed.Decision = GovernedProviderConfigurationDecision.Ready(first);
        Assert.Equal(new OneDriveConfiguration(first.ClientId!, first.Secret, first.CallbackUri!).Revision,
            await owner.ReadinessAsync("T001", default));
        foreach (var state in AdoptedFailures())
        {
            governed.Decision = GovernedProviderConfigurationDecision.Unavailable(state);
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ReadinessAsync(
                state == GovernedProviderAdoptionStates.OrganizationMismatch ? "T002" : "T001", default));
        }

        var repaired = OneDrive("repaired-onedrive", "repaired-secret", 8);
        governed.Decision = GovernedProviderConfigurationDecision.Ready(repaired);
        var repairedRevision = await owner.ReadinessAsync("T001", default);
        Assert.Equal(new OneDriveConfiguration(repaired.ClientId!, repaired.Secret, repaired.CallbackUri!).Revision,
            repairedRevision);
        Assert.NotEqual(legacyRevision, repairedRevision);
    }

    [Fact]
    public async Task AiAllowsLegacyOnlyBeforeAdoptionAndEveryAdoptedFailureStaysClosed()
    {
        var legacy = AiLegacy();
        var governed = new MutableGovernedReader();
        using var owner = new QaModelAdapter(legacy, new HttpClient(new RejectingHandler()), governed);
        var legacyConfiguration = await owner.ConfigurationAsync("T001", default);
        Assert.True(legacyConfiguration.Available);
        Assert.Equal("Legacy AI", legacyConfiguration.ProviderLabel);

        governed.Decision = GovernedProviderConfigurationDecision.Ready(Ai("Governed AI", "governed-key", 1));
        var adopted = await owner.ConfigurationAsync("T001", default);
        Assert.True(adopted.Available);
        Assert.Equal("Governed AI", adopted.ProviderLabel);
        Assert.NotEqual(legacyConfiguration.Revision, adopted.Revision);
        foreach (var state in AdoptedFailures())
        {
            governed.Decision = GovernedProviderConfigurationDecision.Unavailable(state);
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ConfigurationAsync(
                state == GovernedProviderAdoptionStates.OrganizationMismatch ? "T002" : "T001", default));
        }

        governed.Decision = GovernedProviderConfigurationDecision.Ready(Ai("Repaired AI", "repaired-key", 8));
        var repaired = await owner.ConfigurationAsync("T001", default);
        Assert.True(repaired.Available);
        Assert.Equal("Repaired AI", repaired.ProviderLabel);
        Assert.NotEqual(legacyConfiguration.Revision, repaired.Revision);
    }

    private static string[] AdoptedFailures() =>
    [
        GovernedProviderAdoptionStates.Disabled,
        GovernedProviderAdoptionStates.SecretUnavailable, // cleared or absent
        GovernedProviderAdoptionStates.SecretUnavailable, // present but unresolvable
        GovernedProviderAdoptionStates.ConfigurationIncomplete,
        GovernedProviderAdoptionStates.OrganizationMismatch,
    ];

    private static GovernedProviderRuntimeConfiguration Google(string client, string secret, long revision) =>
        new("google-drive", "Organization", "T001", true, revision, null, client,
            "http://localhost:4173/connections-api/local/v1/google-drive/callback", null,
            new Dictionary<string, string> { ["tenantId"] = "T001" }, secret);
    private static GovernedProviderRuntimeConfiguration OneDrive(string client, string secret, long revision) =>
        new("onedrive", "Organization", "T001", true, revision, null, client,
            "http://localhost:4173/connections-api/local/v1/onedrive/callback", null,
            new Dictionary<string, string> { ["tenantId"] = "T001" }, secret);
    private static GovernedProviderRuntimeConfiguration Ai(string label, string secret, long revision) =>
        new("ai", "Organization", "T001", true, revision, "https://governed.example/v1/chat/completions", null,
            null, "governed-model", new Dictionary<string, string> {
                ["tenantId"] = "T001", ["providerLabel"] = label, ["notice"] = "LOCAL governed fixture",
            }, secret);

    private static IConfigurationRoot GoogleLegacy() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["Monergy:GoogleDrive:Tenants:T001:Enabled"] = "true",
        ["Monergy:GoogleDrive:Tenants:T001:ClientId"] = "legacy-google",
        ["Monergy:GoogleDrive:Tenants:T001:ClientSecret"] = "legacy-secret",
        ["Monergy:GoogleDrive:Tenants:T001:RedirectUri"] = "http://localhost:4173/connections-api/local/v1/google-drive/callback",
    }).Build();
    private static IConfigurationRoot OneDriveLegacy() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["Monergy:OneDrive:Tenants:T001:Enabled"] = "true",
        ["Monergy:OneDrive:Tenants:T001:ClientId"] = "legacy-onedrive",
        ["Monergy:OneDrive:Tenants:T001:ClientSecret"] = "legacy-secret",
        ["Monergy:OneDrive:Tenants:T001:RedirectUri"] = "http://localhost:4173/connections-api/local/v1/onedrive/callback",
    }).Build();
    private static IConfigurationRoot AiLegacy() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["Monergy:AiModel:Tenants:T001:Enabled"] = "true",
        ["Monergy:AiModel:Tenants:T001:Endpoint"] = "https://legacy.example/v1/chat/completions",
        ["Monergy:AiModel:Tenants:T001:Model"] = "legacy-model",
        ["Monergy:AiModel:Tenants:T001:ApiKey"] = "legacy-key",
        ["Monergy:AiModel:Tenants:T001:ProviderLabel"] = "Legacy AI",
        ["Monergy:AiModel:Tenants:T001:Notice"] = "LOCAL legacy fixture",
    }).Build();

    private sealed class MutableGovernedReader : IGovernedProviderConfigurationReader
    {
        public GovernedProviderConfigurationDecision Decision { get; set; } =
            GovernedProviderConfigurationDecision.NeverAdopted();
        public Task<GovernedProviderConfigurationDecision> ReadAsync(string provider, string tenant, CancellationToken ct) =>
            Task.FromResult(Decision);
    }
    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Provider network calls are outside this configuration test.");
    }
}
