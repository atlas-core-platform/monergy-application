using System.Net;
using System.Net.Http.Json;
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

public sealed class SelfSessionsTests
{
    private static readonly TenantSessionLookup Lookup = new("T001", "current-session");
    private static readonly string Other = new('a', 64);
    private static SelfSessionRevocation Change => new(Guid.NewGuid().ToString("N"), Other);
    private static SelfSessionEntry Entry => new("A100", Other, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(20), "Active", false);

    [Fact]
    public async Task ListDerivesIdentityAndNeverReturnsTheBearerCredential()
    {
        var f = new Fixture();
        var page = await f.Owner.ListAsync(Lookup, null, default);
        Assert.Equal("T001", page.TenantId); Assert.Equal("A100", page.ActorId);
        Assert.Equal("A100", f.Repository.Current?.ActorId); Assert.Equal(2, f.Membership.Calls);
        Assert.DoesNotContain("current-session", JsonSerializer.Serialize(page), StringComparison.Ordinal);
        Assert.Equal(64, SelfSessionReferences.Hash("current-session").Length);
    }
    [Theory]
    [InlineData("T002", "current-session")]
    [InlineData("T001", "different-session")]
    public async Task ForeignTenantOrSessionCannotListOrRevoke(string tenant, string session)
    {
        var f = new Fixture(); var lookup = new TenantSessionLookup(tenant, session);
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ListAsync(lookup, null, default));
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.RevokeAsync(lookup, Change, default));
        Assert.Equal(0, f.Repository.Reads); Assert.Equal(0, f.Repository.Writes);
    }
    [Fact]
    public async Task InactiveMemberNeverReachesTheSessionRepository()
    {
        var f = new Fixture(); f.Membership.Read = _ => new("T001", "A100", false, 3, 8);
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ListAsync(Lookup, null, default));
        Assert.Equal(0, f.Repository.Reads); Assert.True(f.Sessions.Revoked);
    }
    [Theory]
    [InlineData("")]
    [InlineData("not-a-reference")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task InvalidCursorAndTargetFailBeforeRepositoryRead(string invalid)
    {
        var f = new Fixture();
        Assert.Equal(400, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ListAsync(Lookup, invalid, default))).StatusCode);
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.RevokeAsync(Lookup, Change with { SessionReference = invalid }, default));
        Assert.Equal(0, f.Repository.Reads); Assert.Equal(0, f.Repository.Writes);
    }
    [Fact]
    public async Task InvalidOperationFailsBeforeRevocation()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.RevokeAsync(Lookup, Change with { OperationId = "invalid" }, default));
        Assert.Equal(0, f.Repository.Writes);
    }
    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("entryActor")]
    [InlineData("reference")]
    [InlineData("duplicate")]
    [InlineData("current")]
    [InlineData("status")]
    [InlineData("dates")]
    [InlineData("cursor")]
    [InlineData("oversized")]
    public async Task MalformedOrForeignOwnerPageIsRejected(string kind)
    {
        var f = new Fixture(); var page = f.Repository.Page;
        f.Repository.Page = kind switch {
            "tenant" => page with { TenantId = "T002" },
            "actor" => page with { ActorId = "A900" },
            "entryActor" => page with { Items = [Entry with { ActorId = "A900" }] },
            "reference" => page with { Items = [Entry with { SessionReference = "current-session" }] },
            "duplicate" => page with { Items = [Entry, Entry] },
            "current" => page with { Items = [Entry with { IsCurrent = true }] },
            "status" => page with { Items = [Entry with { Status = "VerifiedDevice" }] },
            "dates" => page with { Items = [Entry with { ExpiresAt = Entry.EstablishedAt.AddMinutes(-1) }] },
            "cursor" => page with { NextCursor = Other },
            _ => page with { Items = Enumerable.Repeat(Entry, 101).ToArray() }
        };
        Assert.Equal(503, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ListAsync(Lookup, null, default))).StatusCode);
    }
    [Fact]
    public async Task ValidPaginationAndCurrentReferenceArePreservedWithoutBecomingAuthority()
    {
        var f = new Fixture();
        var entries = Enumerable.Range(0, 100).Select(n => Entry with { SessionReference = n.ToString("x64", System.Globalization.CultureInfo.InvariantCulture) }).ToArray();
        f.Repository.Page = new("T001", "A100", entries, entries[^1].SessionReference);
        var page = await f.Owner.ListAsync(Lookup, Other, default);
        Assert.Equal(100, page.Items.Count); Assert.Equal(page.Items[^1].SessionReference, page.NextCursor); Assert.Equal(Other, f.Repository.After);
        f.Repository.Page = new("T001", "A100", [Entry with { SessionReference = SelfSessionReferences.Hash("current-session"), IsCurrent = true }], null);
        Assert.True(Assert.Single((await f.Owner.ListAsync(Lookup, null, default)).Items).IsCurrent);
    }
    [Fact]
    public async Task CurrentSessionRequiresTheExistingSignOutOperation()
    {
        var f = new Fixture();
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.RevokeAsync(Lookup, Change with { SessionReference = SelfSessionReferences.Hash("current-session") }, default));
        Assert.Equal("USE_CURRENT_SESSION_SIGN_OUT", error.Code); Assert.Equal(409, error.StatusCode); Assert.Equal(0, f.Repository.Writes);
    }
    [Fact]
    public async Task SelectedRevocationBindsCurrentIdentityAndValidatesTheExactReceipt()
    {
        var f = new Fixture(); var change = Change;
        var receipt = await f.Owner.RevokeAsync(Lookup, change, default);
        Assert.Equal(new(change.OperationId, Other, true), receipt);
        Assert.Equal("A100", f.Repository.Current?.ActorId); Assert.Equal(change, f.Repository.Change);
        Assert.Equal(2, f.Membership.Calls); Assert.False(f.Sessions.Revoked);
    }
    [Theory]
    [InlineData("operation")]
    [InlineData("target")]
    [InlineData("outcome")]
    public async Task UnboundOrNegativeRevocationReceiptNeverConfirmsSuccess(string kind)
    {
        var f = new Fixture();
        f.Repository.Receipt = c => new(kind == "operation" ? "wrong" : c.OperationId, kind == "target" ? new string('b', 64) : c.SessionReference, kind != "outcome");
        Assert.Equal(503, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.RevokeAsync(Lookup, Change, default))).StatusCode);
    }
    [Theory]
    [InlineData("SESSION_NOT_FOUND", 404)]
    [InlineData("SESSION_OPERATION_CONFLICT", 409)]
    public async Task OwnerMissingOrConflictingReferenceIsNotReinterpreted(string code, int status)
    {
        var f = new Fixture(); f.Repository.Failure = new(code, status);
        Assert.Equal(code, (await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.RevokeAsync(Lookup, Change, default))).Code);
    }
    [Fact]
    public async Task ChangedMembershipAfterCommittedRevocationDoesNotReturnConfirmationOrClaimRollback()
    {
        var f = new Fixture(); f.Membership.Read = call => new("T001", "A100", true, call == 1 ? 3 : 4, 8);
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.RevokeAsync(Lookup, Change, default));
        Assert.Equal(1, f.Repository.Writes); Assert.True(f.Sessions.Revoked);
    }
    [Fact]
    public async Task MembershipChangeBeforeListConfirmationHidesTheResult()
    {
        var f = new Fixture(); f.Membership.Read = call => new("T001", "A100", true, call == 1 ? 3 : 4, 8);
        await Assert.ThrowsAsync<TenantAccessException>(() => f.Owner.ListAsync(Lookup, null, default));
        Assert.Equal(1, f.Repository.Reads); Assert.True(f.Sessions.Revoked);
    }
    [Fact]
    public async Task ActualHttpBoundaryGatesIdentityStrictJsonOriginAndCursor()
    {
        var f = new Fixture(); var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Services.AddSingleton(f.Owner);
        builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow; o.SerializerOptions.RespectRequiredConstructorParameters = true; });
        await using var app = builder.Build(); app.MapSelfSessions(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        using var anonymous = await client.GetAsync("/local/v1/me/sessions"); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001"); client.DefaultRequestHeaders.Add("X-Monergy-Session", "current-session");
        using var read = await client.GetAsync("/local/v1/me/sessions"); Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
        using var cursor = await client.GetAsync("/local/v1/me/sessions?after=invalid"); Assert.Equal(HttpStatusCode.BadRequest, cursor.StatusCode);
        using var extra = await client.PostAsJsonAsync("/local/v1/me/sessions/revoke", new { operationId = Change.OperationId, sessionReference = Other, actorId = "A900" }); Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        using var cross = new HttpRequestMessage(HttpMethod.Post, "/local/v1/me/sessions/revoke") { Content = JsonContent.Create(Change) }; cross.Headers.Add("Origin", "https://foreign.example");
        using var denied = await client.SendAsync(cross); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal(0, f.Repository.Writes);
        using var end = await client.PostAsJsonAsync("/local/v1/me/sessions/revoke", Change); Assert.Equal(HttpStatusCode.OK, end.StatusCode);
        await app.StopAsync();
    }
    private sealed class Fixture
    {
        public Repository Repository { get; } = new(); public Sessions Sessions { get; } = new(); public Membership Membership { get; } = new(); public SelfSessionsApplication Owner { get; }
        public Fixture()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["Monergy:ExecutionZone"] = "CI_EPHEMERAL", ["Monergy:AccessIntegration:Enabled"] = "true",
                ["Monergy:AccessIntegration:Identities:0:TenantId"] = "T001", ["Monergy:AccessIntegration:Identities:0:ActorId"] = "A100", ["Monergy:AccessIntegration:Identities:0:Token"] = new string('a', 64) }).Build();
            Owner = new(Repository, new(Sessions, Membership, new(config)));
        }
    }
    // Dependency doubles exercise application/HTTP behavior, not PostgreSQL SQL.
    private sealed class Repository : ISelfSessionRepository
    {
        public SelfSessionPage Page { get; set; } = new("T001", "A100", [Entry], null);
        public int Reads { get; private set; } public int Writes { get; private set; }
        public TenantSessionContext? Current { get; private set; } public string? After { get; private set; } public SelfSessionRevocation? Change { get; private set; }
        public TenantAccessException? Failure { get; set; }
        public Func<SelfSessionRevocation, SelfSessionReceipt> Receipt { get; set; } = c => new(c.OperationId, c.SessionReference, true);
        public Task<SelfSessionPage> ListAsync(TenantSessionContext current, string? after, CancellationToken ct) { Current = current; After = after; Reads++; return Task.FromResult(Page); }
        public Task<SelfSessionReceipt> RevokeAsync(TenantSessionContext current, SelfSessionRevocation change, CancellationToken ct) { Current = current; Change = change; if (Failure is not null) throw Failure; Writes++; return Task.FromResult(Receipt(change)); }
    }
    private sealed class Membership : ITenantMembershipClient
    {
        public int Calls { get; private set; } public Func<int, TenantMembershipState> Read { get; set; } = _ => new("T001", "A100", true, 3, 8);
        public Task<TenantMembershipState> ReadAsync(string tenantId, string actorId, CancellationToken ct) => Task.FromResult(Read(++Calls));
    }
    private sealed class Sessions : ITenantSessionRepository
    {
        public bool Revoked { get; private set; }
        public Task<TenantSessionContext?> ReadCurrentAsync(string tenantId, string sessionId, CancellationToken ct) => Task.FromResult<TenantSessionContext?>(!Revoked && tenantId == "T001" && sessionId == "current-session" ? new("T001", "A100", "current-session", 3, DateTimeOffset.UtcNow.AddMinutes(30)) : null);
        public Task RevokeAsync(string tenantId, string sessionId, CancellationToken ct) { Revoked = true; return Task.CompletedTask; }
        public Task<TenantSessionContext> CreateAsync(string tenantId, string actorId, string sessionId, long subjectVersion, CancellationToken ct) => throw new NotSupportedException();
        public Task<AccessManagementReceipt> ConsumeAsync(string tenant, string destination, AccessManagementEvent message, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ReadyAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
