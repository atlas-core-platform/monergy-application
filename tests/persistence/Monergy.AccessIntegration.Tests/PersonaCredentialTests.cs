using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class PersonaCredentialTests
{
    [Fact]
    public async Task ActivationEmailCodeIsOpaqueAndCannotBeUsedForRecovery()
    {
        var fixture = new Fixture();
        var challenge = await fixture.Owner.RequestAsync(new("T001", "a300@example.test"), false, default);
        var row = Assert.IsType<RegistrationCode>(fixture.Repository.Queued);
        Assert.Equal(64, challenge.ChallengeId.Length);
        Assert.Equal("persona-activation", row.Purpose);
        Assert.Equal(34, row.ProtectedCode.Length);
        Assert.Throws<AuthenticationTagMismatchException>(() =>
            fixture.Protector.Unprotect("T001:persona-recovery", row.ChallengeHash, row.ProtectedCode));
    }

    [Fact]
    public async Task ActivationStoresCredentialButCannotIssueAUserSession()
    {
        var fixture = new Fixture();
        var challenge = await fixture.Owner.RequestAsync(new("T001", "a300@example.test"), false, default);
        var row = fixture.Repository.Queued!;
        var code = fixture.Protector.Unprotect("T001:persona-activation", row.ChallengeHash, row.ProtectedCode);
        var result = await fixture.Owner.ActivateAsync(new("T001", challenge.ChallengeId, code, Fixture.Password), default);
        Assert.Equal(true, result.GetType().GetProperty("activated")!.GetValue(result));
        Assert.True(fixture.Passwords.Verify(fixture.Repository.Identity!.PasswordHash, Fixture.Password));
        Assert.Equal(0, fixture.Sessions.Created);
    }

    [Fact]
    public async Task ActivatedAdvisorCanSignInOnlyWithCurrentActiveMembership()
    {
        var fixture = new Fixture();
        var received = await fixture.Owner.SignInAsync(new("T001", "a300@example.test", Fixture.Password), default);
        Assert.Equal("A300", received.ActorId);
        fixture.Membership.Active = false;
        await Assert.ThrowsAsync<TenantAccessException>(() =>
            fixture.Owner.SignInAsync(new("T001", "a300@example.test", Fixture.Password), default));
        Assert.Equal(1, fixture.Sessions.Created);
    }

    [Fact]
    public async Task PasswordRejectionNeverSelectsAnActorOrCreatesASession()
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<TenantAccessException>(() =>
            fixture.Owner.SignInAsync(new("T001", "a300@example.test", "incorrect password"), default));
        await Assert.ThrowsAsync<TenantAccessException>(() =>
            fixture.Owner.SignInAsync(new("T002", "a300@example.test", Fixture.Password), default));
        Assert.Equal(0, fixture.Sessions.Created);
        Assert.False(fixture.Repository.LastValid);
    }

    [Fact]
    public async Task RecoveryIsPurposeBoundAndRacingCredentialsRevokeSession()
    {
        var fixture = new Fixture();
        var requested = await fixture.Owner.RequestAsync(new("T001", "a300@example.test"), true, default);
        var pending = fixture.Repository.Queued!;
        var code = fixture.Protector.Unprotect("T001:persona-recovery", pending.ChallengeHash, pending.ProtectedCode);
        _ = await fixture.Owner.RecoverAsync(new("T001", requested.ChallengeId, code, "another long secret value"), default);
        Assert.True(fixture.Passwords.Verify(fixture.Repository.Identity!.PasswordHash, "another long secret value"));
        fixture.Sessions.AfterCreate = () => fixture.Repository.Current = false;
        await Assert.ThrowsAsync<TenantAccessException>(() =>
            fixture.Owner.SignInAsync(new("T001", "a300@example.test", "another long secret value"), default));
        Assert.Equal(1, fixture.Sessions.Revoked);
    }

    private sealed class Fixture
    {
        internal const string Password = "a long password value";
        internal readonly CustomerPasswordPolicy Passwords = new();
        internal readonly CustomerEmailCodeProtector Protector =
            new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        internal readonly Repository Repository;
        internal readonly Membership Membership = new();
        internal readonly Sessions Sessions = new();
        internal readonly PersonaCredentialAccess Owner;

        internal Fixture()
        {
            var settings = new Dictionary<string, string?> {
                ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
                ["Monergy:AccessIntegration:Enabled"] = "true",
                ["Monergy:AccessIntegration:Identities:0:TenantId"] = "T001",
                ["Monergy:AccessIntegration:Identities:0:ActorId"] = "A300",
                ["Monergy:AccessIntegration:Identities:0:Token"] = new string('a', 32),
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            Repository = new(new("T001", "a300@example.test", "A300", Passwords.Hash(Password), 1));
            Owner = new(Repository, Protector, Passwords,
                new TenantSessionAuthority(Sessions, Membership, new ReferenceTenantAuthenticator(config)),
                TimeProvider.System);
        }
    }
    private sealed class Repository : IPersonaCredentialRepository
    {
        public PersonaCredentialIdentity? Identity;
        public Repository(PersonaCredentialIdentity identity) { Identity = identity; }
        public RegistrationCode? Queued;
        public bool Current = true;
        public bool LastValid;
        public Task QueueAsync(string tenant, RegistrationCode challenge, CancellationToken ct)
        { Queued = challenge; return Task.CompletedTask; }
        public Task<string?> ActivateAsync(string tenant, string hash, string verifier, string passwordHash, CancellationToken ct)
        {
            if (Queued is null || Queued.ChallengeHash != hash || Queued.CodeHash != verifier || tenant != "T001")
                return Task.FromResult<string?>(null);
            Identity = Identity! with { PasswordHash = passwordHash };
            return Task.FromResult<string?>(Identity.ActorId);
        }
        public Task<PersonaCredentialIdentity?> ReadAsync(string tenant, string email, CancellationToken ct) =>
            Task.FromResult<PersonaCredentialIdentity?>(
                Identity is not null && tenant == Identity.TenantId && email == Identity.Email ? Identity : null);
        public Task<bool> RecordAttemptAsync(PersonaCredentialIdentity current, bool valid, CancellationToken ct)
        { LastValid = valid; return Task.FromResult(true); }
        public Task<bool> IsCurrentAsync(PersonaCredentialIdentity current, CancellationToken ct) =>
            Task.FromResult(Current);
        public Task<bool> RecoverAsync(string tenant, string hash, string verifier, string newHash, CancellationToken ct)
        {
            if (Queued?.ChallengeHash != hash || Queued.CodeHash != verifier || tenant != "T001")
                return Task.FromResult(false);
            Identity = Identity! with { PasswordHash = newHash, CredentialVersion = Identity!.CredentialVersion + 1 };
            return Task.FromResult(true);
        }
    }
    private sealed class Membership : ITenantMembershipClient
    {
        public bool Active = true;
        public Task<TenantMembershipState> ReadAsync(string tenant, string actor, CancellationToken ct) =>
            Task.FromResult(new TenantMembershipState(tenant, actor, Active, 1, 1));
    }
    private sealed class Sessions : ITenantSessionRepository
    {
        public int Created, Revoked;
        public Action? AfterCreate;
        public Task<TenantSessionContext> CreateAsync(string tenant, string actor, string session, long version, CancellationToken ct)
        {
            Created++;
            AfterCreate?.Invoke();
            return Task.FromResult(new TenantSessionContext(tenant, actor, session, version,
                DateTimeOffset.UtcNow.AddMinutes(30)));
        }
        public Task<TenantSessionContext?> ReadCurrentAsync(string tenant, string session, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task RevokeAsync(string tenant, string session, CancellationToken ct)
        { Revoked++; return Task.CompletedTask; }
        public Task<AccessManagementReceipt> ConsumeAsync(string tenant, string destination,
            AccessManagementEvent message, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ReadyAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
