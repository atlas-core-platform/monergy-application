using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;
using Npgsql;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

// Run ONLY against isolated LOCAL/CI tenant test databases. This test never
// updates reference A100/A200/A300/A900 or their role/membership state.
[Trait("Category", "Physical")]
[Collection("CustomerIdentityPhysicalOwners")]
public sealed class PostgresPersonaCredentialTests
{
    [Fact]
    public async Task ExistingIdentityActivationAndRecoveryAreSingleUseTenantBoundAndRevokeSessions()
    {
        var settings = new Dictionary<string, string?> {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
            ["Monergy:AccessIntegration:Enabled"] = "true",
            ["Monergy:AccessIntegration:CustomerIdentityDatabases:T001"] = Required("AM05_CI_T001_RUNTIME"),
            ["Monergy:AccessIntegration:CustomerIdentityDatabases:T002"] = Required("AM05_CI_T002_RUNTIME"),
        };
        await using var sessions = new PostgresTenantSessions(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var repo = new PostgresPersonaCredentials(sessions);
        await using var owner = new NpgsqlConnection(Required("AM05_CI_T001_OWNER"));
        await owner.OpenAsync();

        var unique = Guid.NewGuid().ToString("N");
        var actor = "ci-" + unique;
        var email = "persona-" + unique + "@example.test";
        string? sessionId = null;
        try
        {
            await owner.ExecuteAsync("""
                INSERT INTO customer_identity.tenant_principals(actor_id,normalized_email)
                VALUES(@Actor,@Email);
                """, new { Actor = actor, Email = email });

            var activation = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var verifier = new string('a', 64);
            await repo.QueueAsync("T001", new("persona-activation", email, activation,
                verifier, new byte[34], DateTimeOffset.UtcNow), default);
            await owner.ExecuteAsync("""
                UPDATE customer_identity.registration_challenges
                SET delivered=true,protected_code=NULL WHERE challenge_hash=@Hash;
                """, new { Hash = activation });

            Assert.Null(await repo.ActivateAsync("T002", activation, verifier, new string('s', 72), default));
            Assert.Null(await repo.ActivateAsync("T001", activation, "invalid", new string('s', 72), default));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT attempts FROM customer_identity.registration_challenges WHERE challenge_hash=@Hash;
                """, new { Hash = activation }));

            Assert.Equal(actor, await repo.ActivateAsync("T001", activation, verifier, new string('s', 72), default));
            Assert.Null(await repo.ActivateAsync("T001", activation, verifier, "replay", default));
            var first = Assert.IsType<PersonaCredentialIdentity>(
                await repo.ReadAsync("T001", email, default));
            Assert.Equal(actor, first.ActorId);
            Assert.Equal(new string('s', 72), first.PasswordHash);
            Assert.True(await repo.IsCurrentAsync(first, default));
            Assert.Null(await repo.ReadAsync("T002", email, default));

            await owner.ExecuteAsync("""
                DELETE FROM customer_identity.persona_credentials WHERE actor_id=@Actor;
                DELETE FROM customer_identity.registration_challenges WHERE email=@Email;
                """, new { Actor = actor, Email = email });
            var testerTicket = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var testerHash = CustomerEmailCodeProtector.ChallengeHash(testerTicket);
            var testerVerifier = new string('b', 64);
            Assert.True(await repo.QueueTesterAsync("T001", new("persona-activation", email,
                testerHash, testerVerifier, [], DateTimeOffset.UtcNow), default));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='ISSUED';
                """, new { Actor = actor, Hash = testerHash }));
            Assert.Equal(actor, await repo.ActivateAsync("T001", testerHash, testerVerifier,
                new string('t', 72), default));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='CONSUMED';
                """, new { Actor = actor, Hash = testerHash }));
            Assert.Null(await repo.ActivateAsync("T001", testerHash, testerVerifier, "replay", default));

            sessionId = "persona-session-" + unique;
            await sessions.CreateAsync("T001", actor, sessionId, 1, default);
            Assert.NotNull(await sessions.ReadCurrentAsync("T001", sessionId, default));

            var recovery = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            await repo.QueueAsync("T001", new("persona-recovery", email, recovery,
                verifier, new byte[34], DateTimeOffset.UtcNow), default);
            await owner.ExecuteAsync("""
                UPDATE customer_identity.registration_challenges
                SET delivered=true,protected_code=NULL WHERE challenge_hash=@Hash;
                """, new { Hash = recovery });
            Assert.False(await repo.RecoverAsync("T002", recovery, verifier, new string('n', 72), default));
            Assert.True(await repo.RecoverAsync("T001", recovery, verifier, new string('n', 72), default));
            Assert.False(await repo.IsCurrentAsync(first, default));
            Assert.Null(await sessions.ReadCurrentAsync("T001", sessionId, default));
            Assert.False(await repo.RecoverAsync("T001", recovery, verifier, new string('r', 72), default));
        }
        finally
        {
            // The physical CI fixture cleans up only its own unique actor,
            // challenges and sessions. Never use these owner grants in t105.
            if (sessionId is not null)
                await owner.ExecuteAsync("DELETE FROM customer_identity.trusted_sessions WHERE actor_id=@Actor;",
                    new { Actor = actor });
            await owner.ExecuteAsync("DELETE FROM customer_identity.persona_invitation_events WHERE actor_id=@Actor;",
                new { Actor = actor });
            await owner.ExecuteAsync("DELETE FROM customer_identity.persona_credentials WHERE actor_id=@Actor;",
                new { Actor = actor });
            await owner.ExecuteAsync("DELETE FROM customer_identity.registration_challenges WHERE email=@Email;",
                new { Email = email });
            await owner.ExecuteAsync("DELETE FROM customer_identity.tenant_principals WHERE actor_id=@Actor;",
                new { Actor = actor });
        }
    }

    [Fact]
    public async Task CancelledInvitationCannotActivateAndRepeatedCancelIsNoOp()
    {
        var settings = new Dictionary<string, string?> {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
            ["Monergy:AccessIntegration:Enabled"] = "true",
            ["Monergy:AccessIntegration:CustomerIdentityDatabases:T001"] = Required("AM05_CI_T001_RUNTIME"),
        };
        await using var sessions = new PostgresTenantSessions(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var repo = new PostgresPersonaCredentials(sessions);
        var invitations = new PostgresPersonaInvitationStatus(sessions);
        await using var owner = new NpgsqlConnection(Required("AM05_CI_T001_OWNER"));
        await owner.OpenAsync();
        var nonce = Guid.NewGuid().ToString("N");
        var actor = "ci-" + nonce;
        var email = "cancel-" + nonce + "@example.test";
        var ticket = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var hash = CustomerEmailCodeProtector.ChallengeHash(ticket);
        var verifier = new string('c', 64);
        try
        {
            await owner.ExecuteAsync("""
                INSERT INTO customer_identity.tenant_principals(actor_id,normalized_email)
                VALUES(@Actor,@Email);
                """, new { Actor = actor, Email = email });
            Assert.True(await repo.QueueTesterAsync("T001", new(
                "persona-activation", email, hash, verifier, [], DateTimeOffset.UtcNow), default));
            Assert.Equal("Cancelled", (await invitations.CancelAsync("T001", actor, default)).State);
            Assert.Equal("NotCancelled", (await invitations.CancelAsync("T001", actor, default)).State);
            Assert.Null(await repo.ActivateAsync("T001", hash, verifier, new string('x', 72), default));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='CANCELLED';
                """, new { Actor = actor, Hash = hash }));
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='CONSUMED';
                """, new { Actor = actor, Hash = hash }));
            // Query by cursor so this remains stable when the isolated tenant
            // has more than 50 existing invitation actors.
            var page = await invitations.ListAsync("T001", actor, default);
            Assert.DoesNotContain(page.Items, item => item.ActorId == actor);
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='ISSUED';
                """, new { Actor = actor, Hash = hash }));
        }
        finally
        {
            await owner.ExecuteAsync("""
                DELETE FROM customer_identity.persona_invitation_events WHERE actor_id=@Actor;
                DELETE FROM customer_identity.registration_challenges WHERE email=@Email;
                DELETE FROM customer_identity.tenant_principals WHERE actor_id=@Actor;
                """, new { Actor = actor, Email = email });
        }
    }

    [Fact]
    public async Task RenewingPendingTesterInvitationSupersedesOldTicketAndPreservesAudit()
    {
        var settings = new Dictionary<string, string?> {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
            ["Monergy:AccessIntegration:Enabled"] = "true",
            ["Monergy:AccessIntegration:CustomerIdentityDatabases:T001"] = Required("AM05_CI_T001_RUNTIME"),
        };
        await using var sessions = new PostgresTenantSessions(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var repo = new PostgresPersonaCredentials(sessions);
        await using var owner = new NpgsqlConnection(Required("AM05_CI_T001_OWNER"));
        await owner.OpenAsync();
        var id = Guid.NewGuid().ToString("N");
        var actor = "ci-" + id;
        var email = "renew-" + id + "@example.test";
        var first = CustomerEmailCodeProtector.ChallengeHash(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        var second = CustomerEmailCodeProtector.ChallengeHash(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        var verifier = new string('d', 64);
        try
        {
            await owner.ExecuteAsync("""
                INSERT INTO customer_identity.tenant_principals(actor_id,normalized_email)
                VALUES(@Actor,@Email);
                """, new { Actor = actor, Email = email });
            Assert.True(await repo.QueueTesterAsync("T001", new("persona-activation", email,
                first, verifier, [], DateTimeOffset.UtcNow.AddMinutes(-2)), default));
            Assert.True(await repo.QueueTesterAsync("T001", new("persona-activation", email,
                second, verifier, [], DateTimeOffset.UtcNow), default));
            Assert.Null(await repo.ActivateAsync("T001", first, verifier, new string('x', 72), default));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='CANCELLED';
                """, new { Actor = actor, Hash = first }));
            Assert.Equal(actor, await repo.ActivateAsync("T001", second, verifier,
                new string('y', 72), default));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='CONSUMED';
                """, new { Actor = actor, Hash = second }));
        }
        finally
        {
            await owner.ExecuteAsync("""
                DELETE FROM customer_identity.persona_invitation_events WHERE actor_id=@Actor;
                DELETE FROM customer_identity.persona_credentials WHERE actor_id=@Actor;
                DELETE FROM customer_identity.registration_challenges WHERE email=@Email;
                DELETE FROM customer_identity.tenant_principals WHERE actor_id=@Actor;
                """, new { Actor = actor, Email = email });
        }
    }

    [Fact]
    public async Task ExpiredInvitationIsMaterializedExactlyOnceAndRenewalPreservesTerminalHistory()
    {
        var settings = new Dictionary<string, string?> {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
            ["Monergy:AccessIntegration:Enabled"] = "true",
            ["Monergy:AccessIntegration:CustomerIdentityDatabases:T001"] = Required("AM05_CI_T001_RUNTIME"),
        };
        await using var sessions = new PostgresTenantSessions(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var repo = new PostgresPersonaCredentials(sessions);
        var invitations = new PostgresPersonaInvitationStatus(sessions);
        await using var owner = new NpgsqlConnection(Required("AM05_CI_T001_OWNER"));
        await owner.OpenAsync();

        var id = Guid.NewGuid().ToString("N");
        var actor = "ci-" + id;
        var email = "expired-" + id + "@example.test";
        var first = CustomerEmailCodeProtector.ChallengeHash(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        var replacement = CustomerEmailCodeProtector.ChallengeHash(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        var verifier = new string('e', 64);
        try
        {
            await owner.ExecuteAsync("""
                INSERT INTO customer_identity.tenant_principals(actor_id,normalized_email)
                VALUES(@Actor,@Email);
                """, new { Actor = actor, Email = email });

            Assert.True(await repo.QueueTesterAsync("T001", new("persona-activation", email,
                first, verifier, [], DateTimeOffset.UtcNow.AddMinutes(-6)), default));

            _ = await invitations.ListAsync("T001", null, default);
            _ = await invitations.ListAsync("T001", null, default);
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='EXPIRED';
                """, new { Actor = actor, Hash = first }));
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='CANCELLED';
                """, new { Actor = actor, Hash = first }));
            Assert.Null(await repo.ActivateAsync("T001", first, verifier, new string('x', 72), default));
            Assert.Equal("NotCancelled", (await invitations.CancelAsync("T001", actor, default)).State);

            Assert.True(await repo.QueueTesterAsync("T001", new("persona-activation", email,
                replacement, verifier, [], DateTimeOffset.UtcNow), default));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='EXPIRED';
                """, new { Actor = actor, Hash = first }));
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='CANCELLED';
                """, new { Actor = actor, Hash = first }));
            Assert.Equal(actor, await repo.ActivateAsync("T001", replacement, verifier,
                new string('y', 72), default));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='CONSUMED';
                """, new { Actor = actor, Hash = replacement }));
        }
        finally
        {
            await owner.ExecuteAsync("""
                DELETE FROM customer_identity.persona_invitation_events WHERE actor_id=@Actor;
                DELETE FROM customer_identity.persona_credentials WHERE actor_id=@Actor;
                DELETE FROM customer_identity.registration_challenges WHERE email=@Email;
                DELETE FROM customer_identity.tenant_principals WHERE actor_id=@Actor;
                """, new { Actor = actor, Email = email });
        }
    }

    private static string Required(string key) =>
        Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException("A dedicated LOCAL/CI identity test database is required.");
}
