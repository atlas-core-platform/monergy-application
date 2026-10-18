using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity;
using Monergy.Services.CustomerIdentity.Application;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class CustomerAccountAgreementTests
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
    private static AccountAgreementChange Change(string revision) => new(Guid.NewGuid().ToString("N"), revision, true, true, true);

    [Fact]
    public void AbsentOrOtherTenantPublicationNeverInventsDocuments()
    {
        Assert.Null(new AccountAgreementCatalog(Config([])).Read("T001", DateTimeOffset.UtcNow));
        Assert.Null(new AccountAgreementCatalog(Config(Publication())).Read("T002", DateTimeOffset.UtcNow));
    }
    [Theory]
    [InlineData("Documents:0:Kind", "CONSENT")]
    [InlineData("Documents:0:Version", "invalid version")]
    [InlineData("Documents:0:Body", "replaced text")]
    [InlineData("Documents:0:Sha256", "invalid")]
    [InlineData("Documents:0:Title", "")]
    [InlineData("Documents:0:Title", "Name\nInjected")]
    [InlineData("PublishedAt", "not a date")]
    public void InvalidPublicationFailsConfigurationInsteadOfServingLegalText(string key, string value)
    {
        var values = Publication(); values["Monergy:AccountAgreements:Publications:0:" + key] = value;
        Assert.Throws<InvalidOperationException>(() => new AccountAgreementCatalog(Config(values)));
    }
    [Fact]
    public void ExactBodyVersionAndTenantChangeTheRevisionAndReturnedArraysAreIsolated()
    {
        var values = Publication(); var catalog = new AccountAgreementCatalog(Config(values));
        var first = catalog.Read("T001", DateTimeOffset.UtcNow)!;
        first.Documents[0] = first.Documents[0] with { Body = "mutated" };
        Assert.NotEqual("mutated", catalog.Read("T001", DateTimeOffset.UtcNow)!.Documents[0].Body);
        values["Monergy:AccountAgreements:Publications:0:Documents:0:Version"] = "2.0";
        Assert.NotEqual(first.Revision, new AccountAgreementCatalog(Config(values)).Read("T001", DateTimeOffset.UtcNow)!.Revision);
        values["Monergy:AccountAgreements:Publications:0:TenantId"] = "T002";
        Assert.NotEqual(first.Revision, new AccountAgreementCatalog(Config(values)).Read("T002", DateTimeOffset.UtcNow)!.Revision);
        Assert.Null(catalog.Read("T001", DateTimeOffset.Parse("2025-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
    }
    [Fact]
    public async Task ReadAndExplicitAcceptanceUseCurrentOwnedIdentityAndStoreExactPublication()
    {
        var f = new Fixture(); var view = await f.Owner.ReadAsync(Lookup, default);
        Assert.Equal("C001", view.CustomerId); Assert.Null(view.Acceptance);
        var change = Change(view.Publication!.Revision);
        var receipt = await f.Owner.AcceptAsync(Lookup, change, default);
        Assert.Equal(change.OperationId, receipt.OperationId);
        Assert.Equal(0, receipt.AcceptedAt.UtcTicks % 10);
        Assert.Equal(("T001", "A100", "C001"), f.Repository.Scope);
        Assert.Equal(view.Publication.Revision, f.Repository.Publication?.Revision);
        Assert.Equal(3, f.Repository.Publication?.Documents.Length);
        Assert.Equal(receipt, (await f.Owner.ReadAsync(Lookup, default)).Acceptance);
    }
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task EveryExplicitChoiceIsRequiredWithoutAnyPersistence(bool terms, bool privacy, bool consent)
    {
        var f = new Fixture(); var change = Change(f.Revision) with { AcceptTerms = terms, AcknowledgePrivacy = privacy, ConsentToProcessing = consent };
        Assert.Equal(400, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.AcceptAsync(Lookup, change, default))).StatusCode);
        Assert.Equal(0, f.Repository.Saves);
    }
    [Fact]
    public async Task InvalidOperationStaleRevisionOrUnpublishedDocumentsCannotWrite()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.AcceptAsync(Lookup, Change(f.Revision) with { OperationId = "bad" }, default));
        Assert.Equal("AGREEMENTS_PUBLICATION_CHANGED", (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.AcceptAsync(Lookup, Change(new string('0', 64)), default))).Code);
        var missing = new Fixture(false);
        Assert.Null((await missing.Owner.ReadAsync(Lookup, default)).Publication);
        Assert.Equal("AGREEMENTS_NOT_PUBLISHED", (await Assert.ThrowsAsync<TenantAccessException>(() => missing.Owner.AcceptAsync(Lookup, Change(f.Revision), default))).Code);
        Assert.Equal(0, f.Repository.Saves); Assert.Equal(0, missing.Repository.Saves);
    }
    [Theory]
    [InlineData("T002", "current-session")]
    [InlineData("T001", "wrong-session")]
    public async Task ForeignTenantOrSessionCannotReachAgreementStorage(string tenant, string session)
    {
        var f = new Fixture(); var lookup = new TenantSessionLookup(tenant, session);
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ReadAsync(lookup, default));
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.AcceptAsync(lookup, Change(f.Revision), default));
        Assert.Equal(0, f.Repository.Saves); Assert.Equal(0, f.Repository.Reads);
    }
    [Fact]
    public async Task MissingCustomerAndLostOwnershipCannotBeAccepted()
    {
        var f = new Fixture(); f.Profile.Profile = null;
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.AcceptAsync(Lookup, Change(f.Revision), default));
        Assert.Equal(0, f.Repository.Saves);
        f = new Fixture(); f.Repository.OnSave = () => f.Profile.Owns = false;
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.AcceptAsync(Lookup, Change(f.Revision), default));
        Assert.Equal(1, f.Repository.Saves); // An uncertain committed result is not reported as rolled back.
    }
    [Fact]
    public async Task CustomerSwitchAfterWriteCannotReturnAnotherOwnersSuccess()
    {
        var f = new Fixture(); f.Repository.OnSave = () => f.Profile.Profile = f.Profile.Profile! with { CustomerId = "C002" };
        Assert.Equal("AGREEMENTS_CUSTOMER_CHANGED", (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.AcceptAsync(Lookup, Change(f.Revision), default))).Code);
    }
    [Fact]
    public async Task HttpRequiresSessionStrictPayloadSameOriginAndExplicitChoice()
    {
        var f = new Fixture(); var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow; o.SerializerOptions.RespectRequiredConstructorParameters = true; });
        builder.Services.AddSingleton(f.Owner);
        await using var app = builder.Build(); app.MapCustomerAccountAgreements(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/local/v1/me/account-agreements")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001"); client.DefaultRequestHeaders.Add("X-Monergy-Session", "current-session");
        var read = await client.GetAsync("/local/v1/me/account-agreements"); Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
        var change = Change(f.Revision);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/local/v1/me/account-agreements", new { change.OperationId, change.Revision, change.AcceptTerms, change.AcknowledgePrivacy, change.ConsentToProcessing, customerId = "C900" })).StatusCode);
        using var cross = new HttpRequestMessage(HttpMethod.Post, "/local/v1/me/account-agreements") { Content = JsonContent.Create(change) }; cross.Headers.Add("Origin", "https://foreign.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(cross)).StatusCode); Assert.Equal(0, f.Repository.Saves);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/local/v1/me/account-agreements", change)).StatusCode);
        await app.StopAsync();
    }
    private sealed class Fixture
    {
        public ProfileRepository Profile { get; } = new();
        public AgreementRepository Repository { get; } = new();
        public CustomerAccountAgreementApplication Owner { get; }
        public string Revision { get; }
        public Fixture(bool published = true)
        {
            var values = Publication(); if (!published) foreach (var k in values.Keys.Where(k => k.StartsWith("Monergy:AccountAgreements:", StringComparison.Ordinal)).ToArray()) values.Remove(k);
            var config = Config(values); var catalog = new AccountAgreementCatalog(config); Revision = catalog.Read("T001", DateTimeOffset.UtcNow)?.Revision ?? "";
            var profiles = new SelfCustomerProfileApplication(Profile, new(new Sessions(), new Membership(), new(config)), TimeProvider.System);
            Owner = new(Repository, profiles, catalog, TimeProvider.System);
        }
    }
    private sealed class AgreementRepository : ICustomerAccountAgreementRepository
    {
        public int Reads { get; private set; } public int Saves { get; private set; }
        public (string, string, string) Scope { get; private set; }
        public AccountAgreementPublication? Publication { get; private set; }
        public AccountAgreementReceipt? Receipt { get; private set; }
        public Action? OnSave { get; set; }
        public Task<AccountAgreementReceipt?> ReadAsync(string tenant, string actor, string customer, string revision, CancellationToken ct) { Reads++; return Task.FromResult(Receipt?.Revision == revision ? Receipt : null); }
        public Task<AccountAgreementReceipt> AcceptAsync(string tenant, string actor, string customer, AccountAgreementPublication publication, string operation, DateTimeOffset now, CancellationToken ct)
        { Saves++; Scope = (tenant, actor, customer); Publication = publication; Receipt = new(operation, publication.Revision, now); OnSave?.Invoke(); return Task.FromResult(Receipt); }
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
