using System.Net.Mail;
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
            Assert.NotNull(await repo.QueueTesterAsync("T001", new("persona-activation", email,
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
            Assert.NotNull(await repo.QueueTesterAsync("T001", new(
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
            Assert.NotNull(await repo.QueueTesterAsync("T001", new("persona-activation", email,
                first, verifier, [], DateTimeOffset.UtcNow.AddMinutes(-2)), default));
            Assert.NotNull(await repo.QueueTesterAsync("T001", new("persona-activation", email,
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

            Assert.NotNull(await repo.QueueTesterAsync("T001", new("persona-activation", email,
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

            Assert.NotNull(await repo.QueueTesterAsync("T001", new("persona-activation", email,
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

    [Fact]
    public async Task ReplayingSameTesterInvitationIsIdempotentAndDoesNotConsumeRateLimit()
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
        var email = "retry-" + id + "@example.test";
        var ticket = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var hash = CustomerEmailCodeProtector.ChallengeHash(ticket);
        var verifier = new string('f', 64);
        try
        {
            await owner.ExecuteAsync("""
                INSERT INTO customer_identity.tenant_principals(actor_id,normalized_email)
                VALUES(@Actor,@Email);
                """, new { Actor = actor, Email = email });

            var requestedAt = DateTimeOffset.UtcNow;
            var challenge = new RegistrationCode(
                "persona-activation", email, hash, verifier, [], requestedAt);
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => repo.QueueTesterAsync("T001", challenge, default)));
            var first = Assert.IsType<DateTimeOffset>(concurrent[0]);
            Assert.All(concurrent, result =>
                Assert.Equal(first, Assert.IsType<DateTimeOffset>(result)));

            var replay = await repo.QueueTesterAsync("T001", new(
                "persona-activation", email, hash, verifier, [], requestedAt.AddSeconds(5)), default);
            Assert.Equal(first, Assert.IsType<DateTimeOffset>(replay));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT requests FROM customer_identity.registration_challenges
                WHERE purpose='persona-activation' AND email=@Email;
                """, new { Email = email }));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='ISSUED';
                """, new { Actor = actor, Hash = hash }));

            var different = CustomerEmailCodeProtector.ChallengeHash(
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
            Assert.Null(await repo.QueueTesterAsync("T001", new(
                "persona-activation", email, different, verifier, [], DateTimeOffset.UtcNow.AddSeconds(10)), default));
            Assert.Equal(hash, await owner.ExecuteScalarAsync<string>("""
                SELECT challenge_hash FROM customer_identity.registration_challenges
                WHERE purpose='persona-activation' AND email=@Email;
                """, new { Email = email }));

            Assert.Equal("Cancelled", (await invitations.CancelAsync("T001", actor, default)).State);
            Assert.Null(await repo.QueueTesterAsync("T001", new(
                "persona-activation", email, hash, verifier, [], DateTimeOffset.UtcNow.AddMinutes(2)), default));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='CANCELLED';
                """, new { Actor = actor, Hash = hash }));
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
    public async Task EmailDeliveredPersonaInvitationEntersLedgerOnlyAfterDeliveryAndPreservesLifecycle()
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
        var email = "mail-" + id + "@example.test";
        var protector = new CustomerEmailCodeProtector(
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var sender = new DeliverySender();
        var delivery = new CustomerRegistrationDelivery(
            sessions,
            new CustomerEmailDeliveryOptions(
                "smtp.example.test", 587, "noreply@example.test",
                "local-test-user", "local-test-password", ["T001"]),
            protector,
            sender);
        var firstId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var firstHash = CustomerEmailCodeProtector.ChallengeHash(firstId);
        const string firstCode = "123456";
        var firstVerifier = protector.Verifier(
            "T001:persona-activation", firstId, firstCode);
        var firstRequested = DateTimeOffset.UtcNow;
        try
        {
            await owner.ExecuteAsync("""
                INSERT INTO customer_identity.tenant_principals(actor_id,normalized_email)
                VALUES(@Actor,@Email);
                """, new { Actor = actor, Email = email });

            await repo.QueueAsync("T001", new(
                "persona-activation", email, firstHash, firstVerifier,
                protector.Protect("T001:persona-activation", firstHash, firstCode),
                firstRequested), default);
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash;
                """, new { Actor = actor, Hash = firstHash }));
            Assert.False(await owner.ExecuteScalarAsync<bool>("""
                SELECT delivered FROM customer_identity.registration_challenges
                WHERE challenge_hash=@Hash;
                """, new { Hash = firstHash }));
            Assert.NotNull(await owner.ExecuteScalarAsync<byte[]>("""
                SELECT protected_code FROM customer_identity.registration_challenges
                WHERE challenge_hash=@Hash;
                """, new { Hash = firstHash }));
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_credentials
                WHERE actor_id=@Actor;
                """, new { Actor = actor }));
            Assert.DoesNotContain(
                (await invitations.ListAsync("T001", null, default)).Items,
                item => item.ActorId == actor);

            sender.Fail = true;
            await delivery.DeliverAsync(default);
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash;
                """, new { Actor = actor, Hash = firstHash }));
            Assert.False(await owner.ExecuteScalarAsync<bool>("""
                SELECT delivered FROM customer_identity.registration_challenges
                WHERE challenge_hash=@Hash;
                """, new { Hash = firstHash }));
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_credentials
                WHERE actor_id=@Actor;
                """, new { Actor = actor }));

            await owner.ExecuteAsync("""
                UPDATE customer_identity.registration_challenges
                SET lease_until=clock_timestamp()-interval '1 second'
                WHERE challenge_hash=@Hash;
                """, new { Hash = firstHash });
            sender.Fail = false;
            await delivery.DeliverAsync(default);
            Assert.True(await owner.ExecuteScalarAsync<bool>("""
                SELECT delivered FROM customer_identity.registration_challenges
                WHERE challenge_hash=@Hash;
                """, new { Hash = firstHash }));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='ISSUED';
                """, new { Actor = actor, Hash = firstHash }));
            Assert.Null(await owner.ExecuteScalarAsync<byte[]>("""
                SELECT protected_code FROM customer_identity.registration_challenges
                WHERE challenge_hash=@Hash;
                """, new { Hash = firstHash }));
            await delivery.DeliverAsync(default);
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='ISSUED';
                """, new { Actor = actor, Hash = firstHash }));
            Assert.Equal("Pending",
                Assert.Single((await invitations.ListAsync("T001", null, default)).Items,
                    item => item.ActorId == actor).State);

            var throttledId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var throttledHash = CustomerEmailCodeProtector.ChallengeHash(throttledId);
            const string throttledCode = "222333";
            await repo.QueueAsync("T001", new(
                "persona-activation", email, throttledHash,
                protector.Verifier("T001:persona-activation", throttledId, throttledCode),
                protector.Protect("T001:persona-activation", throttledHash, throttledCode),
                firstRequested.AddSeconds(10)), default);
            Assert.Equal(firstHash, await owner.ExecuteScalarAsync<string>("""
                SELECT challenge_hash FROM customer_identity.registration_challenges
                WHERE purpose='persona-activation' AND email=@Email;
                """, new { Email = email }));
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='CANCELLED';
                """, new { Actor = actor, Hash = firstHash }));
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='EXPIRED';
                """, new { Actor = actor, Hash = firstHash }));
            Assert.True(await owner.ExecuteScalarAsync<bool>("""
                SELECT delivered AND NOT consumed AND expires_at>clock_timestamp()
                    AND code_hash=@Verifier
                FROM customer_identity.registration_challenges
                WHERE challenge_hash=@Hash;
                """, new { Hash = firstHash, Verifier = firstVerifier }));
            Assert.Equal("Pending",
                Assert.Single((await invitations.ListAsync("T001", null, default)).Items,
                    item => item.ActorId == actor).State);

            var secondId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var secondHash = CustomerEmailCodeProtector.ChallengeHash(secondId);
            const string secondCode = "654321";
            var secondVerifier = protector.Verifier(
                "T001:persona-activation", secondId, secondCode);
            await repo.QueueAsync("T001", new(
                "persona-activation", email, secondHash, secondVerifier,
                protector.Protect("T001:persona-activation", secondHash, secondCode),
                firstRequested.AddMinutes(2)), default);

            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='CANCELLED';
                """, new { Actor = actor, Hash = firstHash }));
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='ISSUED';
                """, new { Actor = actor, Hash = secondHash }));
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='EXPIRED';
                """, new { Actor = actor, Hash = firstHash }));
            Assert.Equal("Cancelled",
                Assert.Single((await invitations.ListAsync("T001", null, default)).Items,
                    item => item.ActorId == actor).State);

            await delivery.DeliverAsync(default);
            await delivery.DeliverAsync(default);
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='ISSUED';
                """, new { Actor = actor, Hash = secondHash }));
            Assert.Equal("Pending",
                Assert.Single((await invitations.ListAsync("T001", null, default)).Items,
                    item => item.ActorId == actor).State);
            Assert.Null(await repo.ActivateAsync(
                "T001", firstHash, firstVerifier, new string('x', 72), default));
            Assert.Equal(actor, await repo.ActivateAsync(
                "T001", secondHash, secondVerifier, new string('y', 72), default));
            Assert.Null(await repo.ActivateAsync(
                "T001", secondHash, secondVerifier, new string('z', 72), default));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='CONSUMED';
                """, new { Actor = actor, Hash = secondHash }));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_credentials
                WHERE actor_id=@Actor;
                """, new { Actor = actor }));
            Assert.Equal("Completed",
                Assert.Single((await invitations.ListAsync("T001", null, default)).Items,
                    item => item.ActorId == actor).State);

            var recoveryId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var recoveryHash = CustomerEmailCodeProtector.ChallengeHash(recoveryId);
            const string recoveryCode = "111222";
            await repo.QueueAsync("T001", new(
                "persona-recovery", email, recoveryHash,
                protector.Verifier("T001:persona-recovery", recoveryId, recoveryCode),
                protector.Protect("T001:persona-recovery", recoveryHash, recoveryCode),
                firstRequested.AddMinutes(4)), default);
            await delivery.DeliverAsync(default);
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE invitation_hash=@Hash;
                """, new { Hash = recoveryHash }));

            foreach (var purpose in new[] { "registration", "recovery" })
            {
                var nonPersonaId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                var nonPersonaHash = CustomerEmailCodeProtector.ChallengeHash(nonPersonaId);
                const string nonPersonaCode = "333222";
                await owner.ExecuteAsync("""
                    INSERT INTO customer_identity.registration_challenges
                      (purpose,email,challenge_hash,code_hash,protected_code,requested_at,
                       expires_at,window_at,requests)
                    VALUES(@Purpose,@Email,@Hash,@Verifier,@Protected,clock_timestamp(),
                           clock_timestamp()+interval '5 minutes',clock_timestamp(),1);
                    """, new {
                        Purpose = purpose,
                        Email = email,
                        Hash = nonPersonaHash,
                        Verifier = protector.Verifier("T001:" + purpose, nonPersonaId, nonPersonaCode),
                        Protected = protector.Protect("T001:" + purpose, nonPersonaHash, nonPersonaCode)
                    });
                await delivery.DeliverAsync(default);
                Assert.True(await owner.ExecuteScalarAsync<bool>("""
                    SELECT delivered FROM customer_identity.registration_challenges
                    WHERE challenge_hash=@Hash;
                    """, new { Hash = nonPersonaHash }));
                Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                    SELECT count(*) FROM customer_identity.persona_invitation_events
                    WHERE invitation_hash=@Hash;
                    """, new { Hash = nonPersonaHash }));
            }
            Assert.True(sender.Calls >= 6);
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
    public async Task PersonaInvitationExpiringWhileDeliveryIsInFlightIsNotIssued()
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
        var email = "delivery-expiry-" + id + "@example.test";
        var protector = new CustomerEmailCodeProtector(
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var sender = new BlockingDeliverySender();
        var delivery = new CustomerRegistrationDelivery(
            sessions,
            new CustomerEmailDeliveryOptions(
                "smtp.example.test", 587, "noreply@example.test",
                "local-test-user", "local-test-password", ["T001"]),
            protector,
            sender);
        var challenge = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var hash = CustomerEmailCodeProtector.ChallengeHash(challenge);
        const string code = "987654";
        var verifier = protector.Verifier("T001:persona-activation", challenge, code);
        try
        {
            await owner.ExecuteAsync("""
                INSERT INTO customer_identity.tenant_principals(actor_id,normalized_email)
                VALUES(@Actor,@Email);
                """, new { Actor = actor, Email = email });
            await repo.QueueAsync("T001", new(
                "persona-activation", email, hash, verifier,
                protector.Protect("T001:persona-activation", hash, code),
                DateTimeOffset.UtcNow), default);

            var deliveryTask = delivery.DeliverAsync(default);
            await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await owner.ExecuteAsync("""
                UPDATE customer_identity.registration_challenges
                SET expires_at=clock_timestamp()-interval '1 second'
                WHERE challenge_hash=@Hash;
                """, new { Hash = hash });
            sender.Release.TrySetResult();
            await deliveryTask;

            Assert.False(await owner.ExecuteScalarAsync<bool>("""
                SELECT delivered FROM customer_identity.registration_challenges
                WHERE challenge_hash=@Hash;
                """, new { Hash = hash }));
            Assert.Null(await owner.ExecuteScalarAsync<byte[]>("""
                SELECT protected_code FROM customer_identity.registration_challenges
                WHERE challenge_hash=@Hash;
                """, new { Hash = hash }));
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash;
                """, new { Actor = actor, Hash = hash }));
            Assert.Null(await repo.ActivateAsync(
                "T001", hash, verifier, new string('x', 72), default));
        }
        finally
        {
            sender.Release.TrySetResult();
            await owner.ExecuteAsync("""
                DELETE FROM customer_identity.persona_invitation_events WHERE actor_id=@Actor;
                DELETE FROM customer_identity.registration_challenges WHERE email=@Email;
                DELETE FROM customer_identity.tenant_principals WHERE actor_id=@Actor;
                """, new { Actor = actor, Email = email });
        }
    }

    [Fact]
    public async Task RenewalWhileDeliveryIsInFlightCannotIssueTheSupersededChallenge()
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
        var email = "delivery-renewal-" + id + "@example.test";
        var protector = new CustomerEmailCodeProtector(
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var sender = new BlockingDeliverySender();
        var delivery = new CustomerRegistrationDelivery(
            sessions,
            new CustomerEmailDeliveryOptions(
                "smtp.example.test", 587, "noreply@example.test",
                "local-test-user", "local-test-password", ["T001"]),
            protector,
            sender);
        var firstId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var firstHash = CustomerEmailCodeProtector.ChallengeHash(firstId);
        var firstVerifier = protector.Verifier("T001:persona-activation", firstId, "111222");
        var secondId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var secondHash = CustomerEmailCodeProtector.ChallengeHash(secondId);
        var secondVerifier = protector.Verifier("T001:persona-activation", secondId, "333444");
        var requestedAt = DateTimeOffset.UtcNow;
        try
        {
            await owner.ExecuteAsync("""
                INSERT INTO customer_identity.tenant_principals(actor_id,normalized_email)
                VALUES(@Actor,@Email);
                """, new { Actor = actor, Email = email });
            await repo.QueueAsync("T001", new(
                "persona-activation", email, firstHash, firstVerifier,
                protector.Protect("T001:persona-activation", firstHash, "111222"),
                requestedAt), default);

            var staleDelivery = delivery.DeliverAsync(default);
            await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await repo.QueueAsync("T001", new(
                "persona-activation", email, secondHash, secondVerifier,
                protector.Protect("T001:persona-activation", secondHash, "333444"),
                requestedAt.AddMinutes(2)), default);
            sender.Release.TrySetResult();
            await staleDelivery;

            Assert.Equal(secondHash, await owner.ExecuteScalarAsync<string>("""
                SELECT challenge_hash FROM customer_identity.registration_challenges
                WHERE purpose='persona-activation' AND email=@Email;
                """, new { Email = email }));
            Assert.False(await owner.ExecuteScalarAsync<bool>("""
                SELECT delivered FROM customer_identity.registration_challenges
                WHERE challenge_hash=@Hash;
                """, new { Hash = secondHash }));
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor;
                """, new { Actor = actor }));
            Assert.Null(await repo.ActivateAsync(
                "T001", firstHash, firstVerifier, new string('x', 72), default));

            await delivery.DeliverAsync(default);
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM customer_identity.persona_invitation_events
                WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='ISSUED';
                """, new { Actor = actor, Hash = secondHash }));
            Assert.Equal(actor, await repo.ActivateAsync(
                "T001", secondHash, secondVerifier, new string('y', 72), default));
        }
        finally
        {
            sender.Release.TrySetResult();
            await owner.ExecuteAsync("""
                DELETE FROM customer_identity.persona_invitation_events WHERE actor_id=@Actor;
                DELETE FROM customer_identity.persona_credentials WHERE actor_id=@Actor;
                DELETE FROM customer_identity.registration_challenges WHERE email=@Email;
                DELETE FROM customer_identity.tenant_principals WHERE actor_id=@Actor;
                """, new { Actor = actor, Email = email });
        }
    }

    [Fact]
    public async Task ConcurrentDeliveryActivationAndCancellationHaveAtMostOneTerminalOutcome()
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

        for (var iteration = 0; iteration < 5; iteration++)
        {
            var id = Guid.NewGuid().ToString("N");
            var actor = "ci-" + id;
            var email = "delivery-terminal-" + id + "@example.test";
            var protector = new CustomerEmailCodeProtector(
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var sender = new BlockingDeliverySender();
            var delivery = new CustomerRegistrationDelivery(
                sessions,
                new CustomerEmailDeliveryOptions(
                    "smtp.example.test", 587, "noreply@example.test",
                    "local-test-user", "local-test-password", ["T001"]),
                protector,
                sender);
            var challenge = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var hash = CustomerEmailCodeProtector.ChallengeHash(challenge);
            var verifier = protector.Verifier("T001:persona-activation", challenge, "555666");
            try
            {
                await owner.ExecuteAsync("""
                    INSERT INTO customer_identity.tenant_principals(actor_id,normalized_email)
                    VALUES(@Actor,@Email);
                    """, new { Actor = actor, Email = email });
                await repo.QueueAsync("T001", new(
                    "persona-activation", email, hash, verifier,
                    protector.Protect("T001:persona-activation", hash, "555666"),
                    DateTimeOffset.UtcNow), default);

                var deliveryTask = delivery.DeliverAsync(default);
                await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                sender.Release.TrySetResult();
                var activationTask = repo.ActivateAsync(
                    "T001", hash, verifier, new string('z', 72), default);
                var cancellationTask = invitations.CancelAsync("T001", actor, default);
                await Task.WhenAll(deliveryTask, activationTask, cancellationTask);
                var activation = await activationTask;
                _ = await cancellationTask;

                Assert.Equal(1, await owner.ExecuteScalarAsync<int>("""
                    SELECT count(*) FROM customer_identity.persona_invitation_events
                    WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='ISSUED';
                    """, new { Actor = actor, Hash = hash }));
                var consumed = await owner.ExecuteScalarAsync<int>("""
                    SELECT count(*) FROM customer_identity.persona_invitation_events
                    WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='CONSUMED';
                    """, new { Actor = actor, Hash = hash });
                var cancelled = await owner.ExecuteScalarAsync<int>("""
                    SELECT count(*) FROM customer_identity.persona_invitation_events
                    WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='CANCELLED';
                    """, new { Actor = actor, Hash = hash });
                var expired = await owner.ExecuteScalarAsync<int>("""
                    SELECT count(*) FROM customer_identity.persona_invitation_events
                    WHERE actor_id=@Actor AND invitation_hash=@Hash AND action='EXPIRED';
                    """, new { Actor = actor, Hash = hash });
                Assert.InRange(consumed + cancelled + expired, 0, 1);

                if (consumed + cancelled == 0)
                {
                    Assert.Equal("Cancelled", (await invitations.CancelAsync(
                        "T001", actor, default)).State);
                    cancelled = 1;
                }
                Assert.Equal(1, consumed + cancelled);
                Assert.Equal(consumed == 1, activation == actor);
                Assert.Equal(cancelled == 1, activation is null);
            }
            finally
            {
                sender.Release.TrySetResult();
                await owner.ExecuteAsync("""
                    DELETE FROM customer_identity.persona_invitation_events WHERE actor_id=@Actor;
                    DELETE FROM customer_identity.persona_credentials WHERE actor_id=@Actor;
                    DELETE FROM customer_identity.registration_challenges WHERE email=@Email;
                    DELETE FROM customer_identity.tenant_principals WHERE actor_id=@Actor;
                    """, new { Actor = actor, Email = email });
            }
        }
    }

    private sealed class DeliverySender : ICustomerEmailSender
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public Task SendAsync(string email, string code, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new SmtpException("Controlled physical test failure.");
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingDeliverySender : ICustomerEmailSender
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendAsync(string email, string code, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }

    private static string Required(string key) =>
        Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException("A dedicated LOCAL/CI identity test database is required.");
}
