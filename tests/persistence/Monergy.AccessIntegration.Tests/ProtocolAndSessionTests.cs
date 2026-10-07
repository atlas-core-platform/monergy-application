using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class ProtocolAndSessionTests
{
    [Fact]
    public void WireHashMatchesIndependentAm04VectorAndRejectsTenantSubstitution()
    {
        var message = new AccessManagementEvent("CID-070", Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "AccessManagementAuditEvidence", "1.0.0", new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero), "unit-test",
            "Access Management Service", "T001", "role-one", "A900", 1, null, "role.saved", null);
        Assert.Equal("4089f0fe47d2ba9f7323f048b3043c8887775fc6ac02901f9021e62909127498", TenantAccessProtocol.Hash(message));
        TenantAccessProtocol.Validate(message, "T001", "audit");
        Assert.Throws<TenantAccessException>(() => TenantAccessProtocol.Validate(message, "T002", "audit"));
        Assert.Throws<TenantAccessException>(() => TenantAccessProtocol.Validate(message, "T001", "sessions"));
    }

    [Fact]
    public async Task EstablishmentAuthenticatesCanonicalActorAndConfirmsMembershipTwice()
    {
        var repository = new Sessions();
        var membership = new Membership();
        var authority = Authority(repository, membership);
        var session = await authority.EstablishAsync("T001", new string('a', 64), default);
        Assert.Equal("A100", session.ActorId);
        Assert.Equal(64, session.AuthenticationContextId.Length);
        Assert.Equal(2, membership.Calls);
        Assert.Equal(3, session.SubjectVersion);
    }

    [Fact]
    public async Task DisabledMembershipCannotCreateASession()
    {
        var repository = new Sessions();
        var membership = new Membership { Read = _ => new("T001", "A100", false, 3, 8) };
        await Assert.ThrowsAsync<TenantAccessException>(() => Authority(repository, membership).EstablishAsync("T001", new string('a', 64), default));
        Assert.Null(repository.Current);
    }

    [Fact]
    public async Task MembershipChangeDuringIssuanceRevokesTheUnreturnedSession()
    {
        var repository = new Sessions();
        var membership = new Membership { Read = call => new("T001", "A100", true, call == 1 ? 3 : 4, 8) };
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => Authority(repository, membership).EstablishAsync("T001", new string('a', 64), default));
        Assert.Equal("MEMBERSHIP_CHANGED", error.Code);
        Assert.True(repository.Revoked);
    }

    [Fact]
    public async Task EveryValidationChecksCurrentMembershipWithoutCachedAllowFallback()
    {
        var repository = new Sessions();
        var membership = new Membership();
        var authority = Authority(repository, membership);
        var session = await authority.EstablishAsync("T001", new string('a', 64), default);
        membership.Read = _ => throw new TenantAccessException("MEMBERSHIP_UNAVAILABLE", 503);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => authority.ValidateAsync(new("T001", session.AuthenticationContextId), default));
        Assert.Equal(503, error.StatusCode);
    }

    [Fact]
    public async Task VersionChangeInvalidatesSessionBeforeAnEventArrives()
    {
        var repository = new Sessions();
        var membership = new Membership();
        var authority = Authority(repository, membership);
        var session = await authority.EstablishAsync("T001", new string('a', 64), default);
        membership.Read = _ => new("T001", "A100", true, 4, 9);
        await Assert.ThrowsAsync<TenantAccessException>(() => authority.ValidateAsync(new("T001", session.AuthenticationContextId), default));
        Assert.True(repository.Revoked);
    }

    [Fact]
    public async Task AuthenticationReferenceCannotSelectAnotherTenant()
    {
        var repository = new Sessions();
        var membership = new Membership();
        await Assert.ThrowsAsync<TenantAccessException>(() => Authority(repository, membership).EstablishAsync("T002", new string('a', 64), default));
        Assert.Equal(0, membership.Calls);
    }

    private static TenantSessionAuthority Authority(Sessions repository, Membership membership)
    {
        var values = new Dictionary<string, string?>
        {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
            ["Monergy:AccessIntegration:Enabled"] = "true",
            ["Monergy:AccessIntegration:Identities:0:TenantId"] = "T001",
            ["Monergy:AccessIntegration:Identities:0:ActorId"] = "A100",
            ["Monergy:AccessIntegration:Identities:0:Token"] = new string('a', 64),
        };
        return new(repository, membership, new ReferenceTenantAuthenticator(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }

    private sealed class Membership : ITenantMembershipClient
    {
        public int Calls { get; private set; }
        public Func<int, TenantMembershipState> Read { get; set; } = _ => new("T001", "A100", true, 3, 8);
        public Task<TenantMembershipState> ReadAsync(string tenantId, string actorId, CancellationToken cancellationToken) => Task.FromResult(Read(++Calls));
    }

    private sealed class Sessions : ITenantSessionRepository
    {
        public TenantSessionContext? Current { get; private set; }
        public bool Revoked { get; private set; }
        public Task<TenantSessionContext> CreateAsync(string tenantId, string actorId, string sessionId, long subjectVersion, CancellationToken cancellationToken)
        {
            Current = new(tenantId, actorId, sessionId, subjectVersion, DateTimeOffset.UtcNow.AddMinutes(30));
            return Task.FromResult(Current);
        }
        public Task<TenantSessionContext?> ReadCurrentAsync(string tenantId, string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(!Revoked && Current?.TenantId == tenantId && Current.AuthenticationContextId == sessionId ? Current : null);
        public Task RevokeAsync(string tenantId, string sessionId, CancellationToken cancellationToken) { Revoked = true; return Task.CompletedTask; }
        public Task<AccessManagementReceipt> ConsumeAsync(string trustedTenant, string destination, AccessManagementEvent message, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> ReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
