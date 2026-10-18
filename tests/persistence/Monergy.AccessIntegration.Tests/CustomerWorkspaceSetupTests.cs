using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity;
using Monergy.Services.CustomerIdentity.Application;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class CustomerWorkspaceSetupTests
{
    private static readonly TenantSessionLookup Lookup = new("T001", "current-session");
    private static Dictionary<string, string?> Publication()
    {
        var result = new Dictionary<string, string?> {
            ["Monergy:AccessIntegration:Enabled"] = "true", ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
            ["Monergy:AccessIntegration:Identities:0:TenantId"] = "T001", ["Monergy:AccessIntegration:Identities:0:ActorId"] = "A100",
            ["Monergy:AccessIntegration:Identities:0:Token"] = new string('a', 64),
            ["Monergy:AccountAgreements:Publications:0:TenantId"] = "T001",
            ["Monergy:AccountAgreements:Publications:0:PublishedAt"] = "2026-01-01T00:00:00.0000000+00:00" };
        var kinds = new[] { "TERMS", "PRIVACY", "CONSENT" };
        for (var i = 0; i < kinds.Length; i++)
        {
            var prefix = $"Monergy:AccountAgreements:Publications:0:Documents:{i}:";
            var body = "Synthetic test fixture only. " + kinds[i];
            result[prefix + "Kind"] = kinds[i]; result[prefix + "Version"] = "1.0";
            result[prefix + "Title"] = "Fixture " + kinds[i]; result[prefix + "Body"] = body;
            result[prefix + "Sha256"] = AccountAgreementCatalog.Hash(body);
        }
        return result;
    }
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public async Task RequiredSetupNeedsAllCurrentOwnerPrerequisites()
    {
        var f = new Fixture(); var state = await f.Owner.ReadAsync(Lookup, default);
        Assert.True(state.Ready); Assert.True(state.Required); Assert.Equal(f.Revision, state.Revision);
        Assert.Equal(("T001", "A100", "C001"), f.Agreements.Scope);
    }
    [Theory]
    [InlineData("email")]
    [InlineData("profile")]
    [InlineData("acceptance")]
    [InlineData("publication")]
    public async Task CompletingOneStepDoesNotHideAnotherMissingPrerequisite(string missing)
    {
        var f = new Fixture(published: missing != "publication");
        if (missing == "email") f.Profile.Profile = f.Profile.Profile! with { EmailVerifiedAt = null };
        if (missing == "profile") f.Profile.Profile = f.Profile.Profile! with { Version = 0, UpdatedAt = null };
        if (missing == "acceptance") f.Agreements.Receipt = null;
        var state = await f.Owner.ReadAsync(Lookup, default);
        Assert.False(state.Ready); Assert.True(state.Required);
        if (missing == "publication") { Assert.False(state.AgreementsPublished); Assert.Equal(0, f.Agreements.Reads); }
    }
    [Fact]
    public async Task DefaultOffPreservesExistingAdmissionWithoutReadingAgreementStorage()
    {
        var f = new Fixture(required: false, published: false);
        f.Profile.Profile = f.Profile.Profile! with { Version = 0, UpdatedAt = null, EmailVerifiedAt = null };
        var state = await f.Owner.ReadAsync(Lookup, default);
        Assert.False(state.Required); Assert.True(state.Ready); Assert.Equal(0, f.Agreements.Reads);
    }
    [Fact]
    public void RequiredSetupWithoutEmailEntryFailsConfiguration()
    {
        var values = Publication(); values["Monergy:CustomerWorkspaceSetup:Required"] = "true";
        var config = Config(values);
        Assert.Throws<InvalidOperationException>(() => new CustomerWorkspaceSetupApplication(new ProfileRepository(), new Agreements(),
            new(config), new(new Sessions(), new Membership(), new(config)), TimeProvider.System, config));
    }
    [Theory]
    [InlineData("revision")]
    [InlineData("future")]
    [InlineData("before-publication")]
    [InlineData("operation")]
    public async Task MalformedOrStaleAcceptanceCannotGrantAdmission(string mutation)
    {
        var f = new Fixture(); var receipt = f.Agreements.Receipt!;
        f.Agreements.Receipt = mutation switch
        {
            "revision" => receipt with { Revision = new string('b', 64) },
            "future" => receipt with { AcceptedAt = DateTimeOffset.UtcNow.AddDays(1) },
            "operation" => receipt with { OperationId = "bad" },
            _ => receipt with { AcceptedAt = DateTimeOffset.UnixEpoch },
        };
        Assert.Equal("SETUP_ACCEPTANCE_INVALID", (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ReadAsync(Lookup, default))).Code);
    }
    [Fact]
    public async Task ReplacementPublicationRequiresFreshAcceptance()
    {
        var f = new Fixture(); var values = Publication(); values["Monergy:CustomerEmailAccess:Enabled"] = "true";
        values["Monergy:CustomerWorkspaceSetup:Required"] = "true";
        values["Monergy:AccountAgreements:Publications:0:Documents:0:Version"] = "2.0";
        var config = Config(values); var catalog = new AccountAgreementCatalog(config);
        f.Agreements.FilterRevision = true;
        var next = new CustomerWorkspaceSetupApplication(f.Profile, f.Agreements, catalog, new(new Sessions(), new Membership(), new(config)), TimeProvider.System, config);
        Assert.False((await next.ReadAsync(Lookup, default)).Ready);
        Assert.NotEqual(f.Revision, (await next.ReadAsync(Lookup, default)).Revision);
    }
    [Theory]
    [InlineData("T002", "current-session")]
    [InlineData("T001", "foreign")]
    public async Task ForeignSessionCannotReadSetup(string tenant, string session)
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ReadAsync(new(tenant, session), default));
        Assert.Equal(0, f.Agreements.Reads);
    }
    [Fact]
    public async Task LostOwnershipAndForeignProfileFailClosed()
    {
        var f = new Fixture(); f.Profile.Owns = false;
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ReadAsync(Lookup, default));
        f = new Fixture(); f.Profile.Profile = f.Profile.Profile! with { TenantId = "T002" };
        Assert.Equal("SETUP_BINDING_INVALID", (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ReadAsync(Lookup, default))).Code);
    }
    [Fact]
    public async Task ActualHttpProtectsSelectedActorWorkloadPortAndCurrentSessionPort()
    {
        var f = new Fixture(); var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Services.AddSingleton(f.Owner);
        await using var app = builder.Build(); app.MapCustomerWorkspaceSetup(new string('d', 32)); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/local/v1/me/workspace-setup")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/internal/v1/customer-workspace-setup/A100")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001"); client.DefaultRequestHeaders.Add("X-Monergy-Session", "current-session");
        var read = await client.GetAsync("/local/v1/me/workspace-setup"); Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
        using var cross = new HttpRequestMessage(HttpMethod.Get, "/local/v1/me/workspace-setup"); cross.Headers.Add("Origin", "https://foreign.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(cross)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Monergy-Owner-Token", new string('d', 32));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/internal/v1/customer-workspace-setup/A100")).StatusCode);
        await app.StopAsync();
    }
    private sealed class Fixture
    {
        public ProfileRepository Profile { get; } = new();
        public Agreements Agreements { get; } = new();
        public CustomerWorkspaceSetupApplication Owner { get; }
        public string Revision { get; }
        public Fixture(bool required = true, bool published = true)
        {
            var values = Publication(); values["Monergy:CustomerWorkspaceSetup:Required"] = required ? "true" : "false";
            values["Monergy:CustomerEmailAccess:Enabled"] = "true";
            if (!published) foreach (var key in values.Keys.Where(k => k.StartsWith("Monergy:AccountAgreements:", StringComparison.Ordinal)).ToArray()) values.Remove(key);
            var config = Config(values); var catalog = new AccountAgreementCatalog(config);
            Revision = catalog.Read("T001", DateTimeOffset.UtcNow)?.Revision ?? "";
            Profile.Profile = Profile.Profile! with { Version = 1, UpdatedAt = DateTimeOffset.UnixEpoch, EmailVerifiedAt = DateTimeOffset.UnixEpoch };
            Agreements.Receipt = new(Guid.NewGuid().ToString("N"), Revision, DateTimeOffset.UtcNow.AddHours(-1));
            Owner = new(Profile, Agreements, catalog, new(new Sessions(), new Membership(), new(config)), TimeProvider.System, config);
        }
    }
    private sealed class Agreements : ICustomerAccountAgreementRepository
    {
        public AccountAgreementReceipt? Receipt { get; set; }
        public bool FilterRevision { get; set; }
        public int Reads { get; private set; }
        public (string, string, string) Scope { get; private set; }
        public Task<AccountAgreementReceipt?> ReadAsync(string tenant, string actor, string customer, string revision, CancellationToken ct)
        { Reads++; Scope = (tenant, actor, customer); return Task.FromResult(FilterRevision && Receipt?.Revision != revision ? null : Receipt); }
        public Task<AccountAgreementReceipt> AcceptAsync(string tenant, string actor, string customer, AccountAgreementPublication publication, string operation, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class ProfileRepository : ISelfCustomerProfileRepository
    {
        public SelfCustomerProfile? Profile { get; set; } = new("T001", "A100", "C001", "Customer", "owner@example.test", null, 0, null, null);
        public bool Owns { get; set; } = true;
        public Task<SelfCustomerProfile?> ReadAsync(string tenant, string actor, CancellationToken ct) => Task.FromResult(Profile);
        public Task<bool> OwnsAsync(string tenant, string actor, string customer, CancellationToken ct) => Task.FromResult(Owns);
        public Task<SelfCustomerProfileReceipt> SaveAsync(string tenant, string actor, string customer, SelfCustomerProfileChange change, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Membership : ITenantMembershipClient
    {
        public Task<TenantMembershipState> ReadAsync(string tenantId, string actorId, CancellationToken ct) => Task.FromResult(new TenantMembershipState("T001", "A100", true, 3, 8));
    }
    private sealed class Sessions : ITenantSessionRepository
    {
        public Task<TenantSessionContext?> ReadCurrentAsync(string tenantId, string sessionId, CancellationToken ct) => Task.FromResult<TenantSessionContext?>(tenantId == "T001" && sessionId == "current-session" ? new("T001", "A100", "current-session", 3, DateTimeOffset.UtcNow.AddMinutes(30)) : null);
        public Task RevokeAsync(string tenantId, string sessionId, CancellationToken ct) => Task.CompletedTask;
        public Task<TenantSessionContext> CreateAsync(string tenantId, string actorId, string sessionId, long subjectVersion, CancellationToken ct) => throw new NotSupportedException();
        public Task<AccessManagementReceipt> ConsumeAsync(string tenant, string destination, AccessManagementEvent message, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ReadyAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
