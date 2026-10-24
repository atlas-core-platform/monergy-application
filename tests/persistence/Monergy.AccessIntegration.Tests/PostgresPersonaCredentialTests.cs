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
            await owner.ExecuteAsync("DELETE FROM customer_identity.persona_credentials WHERE actor_id=@Actor;",
                new { Actor = actor });
            await owner.ExecuteAsync("DELETE FROM customer_identity.registration_challenges WHERE email=@Email;",
                new { Email = email });
            await owner.ExecuteAsync("DELETE FROM customer_identity.tenant_principals WHERE actor_id=@Actor;",
                new { Actor = actor });
        }
    }

    private static string Required(string key) =>
        Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException("A dedicated LOCAL/CI identity test database is required.");
}
