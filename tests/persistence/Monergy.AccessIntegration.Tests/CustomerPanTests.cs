using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
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
public sealed class CustomerPanTests
{
    private const string SyntheticPan = "ABCDE1234F";
    private const string Ref = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly TenantSessionLookup Lookup = new("T001", "current-session");
    private static Dictionary<string, string?> Values(bool enabled = true) => new()
    {
        ["Monergy:ExecutionZone"] = "CI_EPHEMERAL", ["Monergy:AccessIntegration:Enabled"] = "true",
        ["Monergy:AccessIntegration:Identities:0:TenantId"] = "T001", ["Monergy:AccessIntegration:Identities:0:ActorId"] = "A100",
        ["Monergy:AccessIntegration:Identities:0:Token"] = new string('a', 64),
        ["Monergy:CustomerPan:Enabled"] = enabled ? "true" : "false", ["Monergy:CustomerPan:CurrentKeyId"] = "test-key",
        ["Monergy:CustomerPan:Keys:test-key"] = Convert.ToBase64String(Enumerable.Repeat((byte)17, 32).ToArray()),
    };
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    private static CustomerPanChange Change(string? pan = SyntheticPan) => new(Guid.NewGuid().ToString("N"), 0, pan, true, pan is null ? null : Ref);
    [Fact]
    public void RandomizedAuthenticatedEncryptionReturnsOnlyAMaskAndRedactsRequestToString()
    {
        var protector = new CustomerPanProtection(Config(Values()));
        var encrypted = protector.Protect("T001", "A100", "C001", 1, Ref, SyntheticPan);
        Assert.Equal(38, encrypted.Length);
        Assert.NotEqual(encrypted, protector.Protect("T001", "A100", "C001", 1, Ref, SyntheticPan));
        Assert.DoesNotContain(SyntheticPan, Convert.ToHexString(encrypted));
        Assert.Equal("******234F", protector.Mask("T001", "A100", "C001", new("A100", 1, "test-key", encrypted, Ref, DateTimeOffset.UtcNow)));
        Assert.DoesNotContain(SyntheticPan, Change().ToString());
    }
    [Theory]
    [InlineData("tenant")][InlineData("actor")][InlineData("customer")][InlineData("version")][InlineData("reference")][InlineData("bytes")][InlineData("length")]
    public void CiphertextCannotBeMovedAcrossOwnerVersionOrReferenceBindings(string mutation)
    {
        var p = new CustomerPanProtection(Config(Values())); var bytes = p.Protect("T001", "A100", "C001", 1, Ref, SyntheticPan);
        if (mutation == "bytes") bytes[15] ^= 1;
        if (mutation == "length") bytes = bytes[..37];
        var state = new PanState("A100", mutation == "version" ? 2 : 1, "test-key", bytes, mutation == "reference" ? new string('b', 64) : Ref, DateTimeOffset.UtcNow);
        Assert.ThrowsAny<CryptographicException>(() => p.Mask(mutation == "tenant" ? "T002" : "T001", mutation == "actor" ? "A900" : "A100", mutation == "customer" ? "C900" : "C001", state));
    }
    [Fact]
    public void RetainedOldKeysSupportReadAndReplayAfterCurrentKeyRotation()
    {
        var config = Values(); var original = new CustomerPanProtection(Config(config));
        var change = Change(); var fingerprint = original.Fingerprint("test-key", "T001", "A100", "C001", change);
        var encrypted = original.Protect("T001", "A100", "C001", 1, Ref, SyntheticPan);
        config["Monergy:CustomerPan:CurrentKeyId"] = "rotated"; config["Monergy:CustomerPan:Keys:rotated"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var rotated = new CustomerPanProtection(Config(config));
        Assert.Equal("******234F", rotated.Mask("T001", "A100", "C001", new("A100", 1, "test-key", encrypted, Ref, DateTimeOffset.UtcNow)));
        Assert.Equal(fingerprint, rotated.Fingerprint("test-key", "T001", "A100", "C001", change));
        Assert.NotEqual(fingerprint, rotated.Fingerprint("rotated", "T001", "A100", "C001", change));
        Assert.NotEqual(fingerprint, original.Fingerprint("test-key", "T002", "A100", "C001", change));
        Assert.NotEqual(fingerprint, original.Fingerprint("test-key", "T001", "A100", "C001", change with { Pan = "ABCDE1234G" }));
        Assert.NotEqual(fingerprint, original.Fingerprint("test-key", "T001", "A100", "C001", change with { ExpectedVersion = 1 }));
    }
    [Theory]
    [InlineData("missing")][InlineData("malformed")][InlineData("short")][InlineData("unknown-current")]
    public void EnabledFeatureRequiresConfiguredOwnerKeys(string mutation)
    {
        var v = Values();
        if (mutation == "missing") v.Remove("Monergy:CustomerPan:Keys:test-key");
        if (mutation == "malformed") v["Monergy:CustomerPan:Keys:test-key"] = "not-base64";
        if (mutation == "short") v["Monergy:CustomerPan:Keys:test-key"] = Convert.ToBase64String(new byte[16]);
        if (mutation == "unknown-current") v["Monergy:CustomerPan:CurrentKeyId"] = "absent";
        Assert.Throws<InvalidOperationException>(() => new CustomerPanProtection(Config(v)));
    }
    [Theory]
    [InlineData("")][InlineData("ABCDE123F")][InlineData("ABCD11234F")][InlineData("ABCDE１２３４F")][InlineData("ABCDE1234-")][InlineData("ABCDE1234F\n")]
    public async Task InvalidIdentifiersNeverReachPanPersistence(string pan)
    {
        var f = new Fixture(); await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change(pan), default));
        Assert.Equal(0, f.Pan.Writes);
    }
    [Fact]
    public async Task FeatureDefaultsUnavailableWithoutReadingSensitiveStorage()
    {
        var f = new Fixture(enabled: false); Assert.False((await f.Owner.ReadAsync(Lookup, default)).Enabled); Assert.Equal(0, f.Pan.Reads);
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change(), default)); Assert.Equal(0, f.Pan.Writes);
    }
    [Fact]
    public async Task OwnerReadNeverReturnsThePlainIdentifierAndRequiresCurrentBinding()
    {
        var f = new Fixture(); f.Pan.State = new("A100", 1, "test-key", f.Protection.Protect("T001", "A100", "C001", 1, Ref, SyntheticPan), Ref, DateTimeOffset.UtcNow.AddMinutes(-1));
        var view = await f.Owner.ReadAsync(Lookup, default);
        Assert.Equal("******234F", view.MaskedPan); Assert.DoesNotContain(SyntheticPan, JsonSerializer.Serialize(view));
        Assert.Equal(("T001", "A100", "C001"), f.Pan.Scope);
        f.Pan.State = f.Pan.State with { ActorId = "A900" };
        Assert.Equal(503, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ReadAsync(Lookup, default))).StatusCode);
    }
    [Fact]
    public async Task CurrentAgreementAcceptanceAndExactRevisionAreRequiredForRecordingButNotRemoval()
    {
        var f = new Fixture(); Assert.False((await f.Owner.ReadAsync(Lookup, default)).CanSave);
        Assert.Equal(409, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change(), default))).StatusCode);
        Assert.Equal(0, f.Pan.Writes);
        await f.Owner.SaveAsync(Lookup, Change(null), default); Assert.Null(f.Pan.LastChange!.Pan); Assert.Equal(1, f.Pan.Writes);
        f = new Fixture(published: true); f.Agreements.Accepted = false;
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change() with { AgreementRevision = f.Revision }, default));
        f.Agreements.Accepted = true;
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change(), default));
        await f.Owner.SaveAsync(Lookup, Change(" abcde1234f ") with { AgreementRevision = f.Revision }, default);
        Assert.Equal(SyntheticPan, f.Pan.LastChange!.Pan); Assert.Equal(1, f.Pan.Writes);
    }
    [Theory]
    [InlineData("T002", "current-session")][InlineData("T001", "foreign")]
    public async Task ForeignTenantOrSessionNeverAccessPanStorage(string tenant, string session)
    {
        var f = new Fixture(); await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ReadAsync(new(tenant, session), default));
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(new(tenant, session), Change(null), default));
        Assert.Equal(0, f.Pan.Reads); Assert.Equal(0, f.Pan.Writes);
    }
    [Fact]
    public async Task MissingConfirmationAndInvalidOperationFailBeforeMutation()
    {
        var f = new Fixture();
        foreach (var c in new[] { Change(null) with { Confirm = false }, Change(null) with { OperationId = "wrong" }, Change(null) with { ExpectedVersion = -1 } })
            await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, c, default));
        Assert.Equal(0, f.Pan.Writes);
    }
    [Fact]
    public async Task OwnershipLossAfterWriteReturnsUncertaintyAndDoesNotPretendRollback()
    {
        var f = new Fixture(); f.Pan.AfterWrite = () => f.Profiles.Owns = false;
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change(null), default));
        Assert.Equal(1, f.Pan.Writes);
    }
    [Fact]
    public async Task ActualHttpNeverEchoesRawPanAndRejectsScopeKycAndOriginOverrides()
    {
        var f = new Fixture(published: true); var b = WebApplication.CreateBuilder(); b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow; o.SerializerOptions.RespectRequiredConstructorParameters = true; });
        b.Services.AddSingleton(f.Owner); await using var app = b.Build(); app.MapCustomerPan(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/local/v1/me/pan")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001"); client.DefaultRequestHeaders.Add("X-Monergy-Session", "current-session");
        using var bad = await client.PutAsJsonAsync("/local/v1/me/pan", new { operationId = Guid.NewGuid().ToString("N"), expectedVersion = 0, pan = SyntheticPan, confirm = true, agreementRevision = f.Revision, customerId = "C900", kycStatus = "VERIFIED" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); Assert.DoesNotContain(SyntheticPan, await bad.Content.ReadAsStringAsync());
        using var cross = new HttpRequestMessage(HttpMethod.Put, "/local/v1/me/pan") { Content = JsonContent.Create(Change() with { AgreementRevision = f.Revision }) };
        cross.Headers.Add("Origin", "https://foreign.example"); Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(cross)).StatusCode);
        var change = Change() with { AgreementRevision = f.Revision }; var response = await client.PutAsJsonAsync("/local/v1/me/pan", change);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.DoesNotContain(SyntheticPan, await response.Content.ReadAsStringAsync());
        var read = await client.GetAsync("/local/v1/me/pan"); Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
        var text = await read.Content.ReadAsStringAsync(); Assert.Contains("******234F", text); Assert.DoesNotContain(SyntheticPan, text);
        await app.StopAsync();
    }
    [Theory]
    [InlineData("version")][InlineData("reference")][InlineData("time")][InlineData("pair")]
    public async Task InvalidStoredMetadataCannotBeExposed(string mutation)
    {
        var f = new Fixture(); var state = new PanState("A100", 1, "test-key", f.Protection.Protect("T001", "A100", "C001", 1, Ref, SyntheticPan), Ref, DateTimeOffset.UtcNow.AddMinutes(-1));
        f.Pan.State = mutation switch
        {
            "version" => state with { Version = 0 }, "reference" => state with { VerificationReference = "bad" },
            "time" => state with { UpdatedAt = DateTimeOffset.UtcNow.AddDays(1) }, _ => state with { KeyId = null },
        };
        Assert.Equal(503, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ReadAsync(Lookup, default))).StatusCode);
    }
    [Theory]
    [InlineData("operation")][InlineData("version")][InlineData("reference")]
    public async Task InvalidOwnerReceiptCannotClaimACompletedWrite(string mutation)
    {
        var f = new Fixture(); f.Pan.InvalidReceipt = mutation;
        Assert.Equal("PAN_RECEIPT_INVALID", (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.SaveAsync(Lookup, Change(null), default))).Code);
        Assert.Equal(1, f.Pan.Writes);
    }
    [Fact]
    public async Task OwnershipLossAfterPanReadSuppressesTheProtectedResponse()
    {
        var f = new Fixture(); f.Pan.AfterRead = () => f.Profiles.Owns = false;
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ReadAsync(Lookup, default)); Assert.Equal(1, f.Pan.Reads);
    }
    [Fact]
    public void RemovingAnOldKeyCannotSilentlyDecryptWithTheNewCurrentKey()
    {
        var v = Values(); var original = new CustomerPanProtection(Config(v));
        var bytes = original.Protect("T001", "A100", "C001", 1, Ref, SyntheticPan);
        v.Remove("Monergy:CustomerPan:Keys:test-key"); v["Monergy:CustomerPan:CurrentKeyId"] = "rotated";
        v["Monergy:CustomerPan:Keys:rotated"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var next = new CustomerPanProtection(Config(v));
        Assert.Equal(503, Assert.Throws<TenantAccessException>(() => next.Mask("T001", "A100", "C001", new("A100", 1, "test-key", bytes, Ref, DateTimeOffset.UtcNow))).StatusCode);
    }
    private sealed class Fixture
    {
        public Profiles Profiles { get; } = new(); public PanRepository Pan { get; }
        public Agreements Agreements { get; } = new(); public CustomerPanProtection Protection { get; }
        public CustomerPanApplication Owner { get; } public string? Revision { get; }
        public Fixture(bool enabled = true, bool published = false)
        {
            var values = Values(enabled);
            if (published)
            {
                values["Monergy:AccountAgreements:Publications:0:TenantId"] = "T001";
                values["Monergy:AccountAgreements:Publications:0:PublishedAt"] = "2026-01-01T00:00:00.0000000+00:00";
                var kinds = new[] { "TERMS", "PRIVACY", "CONSENT" };
                for (var i = 0; i < kinds.Length; i++)
                {
                    var prefix = $"Monergy:AccountAgreements:Publications:0:Documents:{i}:"; var body = "Synthetic PAN fixture only " + kinds[i];
                    values[prefix + "Kind"] = kinds[i]; values[prefix + "Version"] = "1"; values[prefix + "Title"] = "Fixture";
                    values[prefix + "Body"] = body; values[prefix + "Sha256"] = AccountAgreementCatalog.Hash(body);
                }
            }
            var config = Config(values); Protection = new(config); Pan = new(Protection); var catalog = new AccountAgreementCatalog(config);
            Revision = catalog.Read("T001", DateTimeOffset.UtcNow)?.Revision;
            Owner = new(Pan, new(Profiles, new(new Sessions(), new Membership(), new(config)), TimeProvider.System), Agreements, catalog, Protection, TimeProvider.System);
        }
    }
    // Explicit behavioral doubles; no physical SQL acceptance is inferred.
    private sealed class PanRepository(CustomerPanProtection protector) : ICustomerPanRepository
    {
        public PanState? State { get; set; } public int Reads { get; private set; } public int Writes { get; private set; }
        public (string, string, string)? Scope { get; private set; } public CustomerPanChange? LastChange { get; private set; }
        public Action? AfterWrite { get; set; } public Action? AfterRead { get; set; } public string? InvalidReceipt { get; set; }
        public Task<PanState?> ReadAsync(string tenant, string actor, string customer, DateTimeOffset now, CancellationToken ct) { Reads++; Scope = (tenant, actor, customer); AfterRead?.Invoke(); return Task.FromResult(State); }
        public Task<CustomerPanReceipt> SaveAsync(string tenant, string actor, string customer, CustomerPanChange change, DateTimeOffset now, CancellationToken ct)
        {
            Writes++; LastChange = change; var version = change.ExpectedVersion + 1;
            State = new(actor, version, change.Pan is null ? null : protector.CurrentKeyId, change.Pan is null ? null : protector.Protect(tenant, actor, customer, version, Ref, change.Pan), Ref, now);
            AfterWrite?.Invoke(); return Task.FromResult(new CustomerPanReceipt(InvalidReceipt == "operation" ? "bad" : change.OperationId, InvalidReceipt == "version" ? version + 1 : version, InvalidReceipt == "reference" ? "bad" : Ref));
        }
    }
    private sealed class Agreements : ICustomerAccountAgreementRepository
    {
        public bool Accepted { get; set; } = true;
        public Task<AccountAgreementReceipt?> ReadAsync(string tenant, string actor, string customer, string revision, CancellationToken ct) => Task.FromResult<AccountAgreementReceipt?>(Accepted ? new(Guid.NewGuid().ToString("N"), revision, DateTimeOffset.UtcNow.AddMinutes(-1)) : null);
        public Task<AccountAgreementReceipt> AcceptAsync(string tenant, string actor, string customer, AccountAgreementPublication p, string operation, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Profiles : ISelfCustomerProfileRepository
    {
        public bool Owns { get; set; } = true;
        public Task<SelfCustomerProfile?> ReadAsync(string tenant, string actor, CancellationToken ct) => Task.FromResult<SelfCustomerProfile?>(new("T001", "A100", "C001", "Fixture", "fixture@example.test", null, 1, null, null));
        public Task<bool> OwnsAsync(string tenant, string actor, string customer, CancellationToken ct) => Task.FromResult(Owns);
        public Task<SelfCustomerProfileReceipt> SaveAsync(string tenant, string actor, string customer, SelfCustomerProfileChange change, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Membership : ITenantMembershipClient
    {
        public Task<TenantMembershipState> ReadAsync(string tenant, string actor, CancellationToken ct) => Task.FromResult(new TenantMembershipState(tenant, actor, true, 3, 8));
    }
    private sealed class Sessions : ITenantSessionRepository
    {
        public Task<TenantSessionContext?> ReadCurrentAsync(string tenant, string session, CancellationToken ct) => Task.FromResult<TenantSessionContext?>(tenant == "T001" && session == "current-session" ? new(tenant, "A100", session, 3, DateTimeOffset.UtcNow.AddMinutes(30)) : null);
        public Task RevokeAsync(string tenant, string session, CancellationToken ct) => Task.CompletedTask;
        public Task<TenantSessionContext> CreateAsync(string tenant, string actor, string session, long version, CancellationToken ct) => throw new NotSupportedException();
        public Task<AccessManagementReceipt> ConsumeAsync(string tenant, string destination, AccessManagementEvent e, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ReadyAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
