using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.Audit.Application;
using Monergy.Services.Audit.Infrastructure;
using Monergy.Services.CustomerIdentity.Infrastructure;
using Npgsql;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

[Trait("Category", "Physical")]
public sealed class PostgresOwnerTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        foreach (var tenant in new[] { "T001", "T002" })
        {
            await using var ci = await OpenAsync("CI", tenant, "OWNER");
            await ci.ExecuteAsync("TRUNCATE customer_identity.trusted_sessions,customer_identity.access_event_inbox,customer_identity.access_subject_versions; UPDATE customer_identity.access_policy_version SET version=0;");
            await using var audit = await OpenAsync("AUDIT", tenant, "OWNER");
            await audit.ExecuteAsync("TRUNCATE audit.access_event_inbox,audit.inbox,audit.evidence CASCADE;");
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DurableTenantRevocationDeduplicatesWithoutAffectingAnotherTenant()
    {
        await using var sessions = new PostgresTenantSessions(Config());
        await sessions.CreateAsync("T001", "A100", "same-session", 1, default);
        await sessions.CreateAsync("T002", "A100", "same-session", 1, default);
        var message = Event();
        Assert.Equal("Applied", (await sessions.ConsumeAsync("T001", "sessions", message, default)).Disposition);
        Assert.Equal("Duplicate", (await sessions.ConsumeAsync("T001", "sessions", message, default)).Disposition);
        await using var reopened = new PostgresTenantSessions(Config());
        Assert.Null(await reopened.ReadCurrentAsync("T001", "same-session", default));
        Assert.NotNull(await reopened.ReadCurrentAsync("T002", "same-session", default));
        await using var owner = await OpenAsync("CI", "T001", "OWNER");
        Assert.Equal(1, await owner.ExecuteScalarAsync<int>("SELECT count(*) FROM customer_identity.access_event_inbox;"));
        Assert.True(await owner.ExecuteScalarAsync<bool>("SELECT revoked FROM customer_identity.trusted_sessions;"));
        Assert.NotEqual("same-session", await owner.ExecuteScalarAsync<string>("SELECT session_hash FROM customer_identity.trusted_sessions;"));
    }

    [Fact]
    public async Task OlderEventsCannotRevokeNewerSessionsOrLowerWatermarks()
    {
        await using var sessions = new PostgresTenantSessions(Config());
        await sessions.CreateAsync("T001", "A100", "new-session", 5, default);
        await sessions.ConsumeAsync("T001", "sessions", Event(4), default);
        await sessions.ConsumeAsync("T001", "sessions", Event(2), default);
        Assert.NotNull(await sessions.ReadCurrentAsync("T001", "new-session", default));
        await Assert.ThrowsAsync<TenantAccessException>(() => sessions.CreateAsync("T001", "A100", "stale-session", 3, default));
        await using var owner = await OpenAsync("CI", "T001", "OWNER");
        Assert.Equal(4, await owner.ExecuteScalarAsync<int>("SELECT version FROM customer_identity.access_subject_versions;"));
        Assert.Equal(4, await owner.ExecuteScalarAsync<int>("SELECT version FROM customer_identity.access_policy_version;"));
    }

    [Fact]
    public async Task InboxFailureRollsBackSessionInvalidationAndWatermark()
    {
        await using var sessions = new PostgresTenantSessions(Config());
        await sessions.CreateAsync("T001", "A100", "rollback-session", 1, default);
        await using var owner = await OpenAsync("CI", "T001", "OWNER");
        await owner.ExecuteAsync("REVOKE INSERT ON customer_identity.access_event_inbox FROM am05_ci_t001_runtime;");
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => sessions.ConsumeAsync("T001", "sessions", Event(), default));
            Assert.NotNull(await sessions.ReadCurrentAsync("T001", "rollback-session", default));
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("SELECT count(*) FROM customer_identity.access_subject_versions;"));
        }
        finally { await owner.ExecuteAsync("GRANT INSERT ON customer_identity.access_event_inbox TO am05_ci_t001_runtime;"); }
    }

    [Fact]
    public async Task AuditAppendsCanonicalEvidenceAndBoundReceiptOnceAcrossConcurrentReplay()
    {
        await using var repository = new PostgresTenantAccessAudit(Config());
        var audit = new TenantAccessAudit(repository);
        var message = AuditEvent();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => audit.ConsumeAsync("T001", message, default)));
        Assert.Single(results.Where(receipt => receipt.Disposition == "Applied"));
        Assert.Single(results.Select(receipt => receipt.EvidenceReference).Distinct());
        await using var owner = await OpenAsync("AUDIT", "T001", "OWNER");
        Assert.Equal(1, await owner.ExecuteScalarAsync<int>("SELECT count(*) FROM audit.evidence WHERE source_contract_id='CID-070' AND producer='Access Management Service';"));
        Assert.Equal("T001", await owner.ExecuteScalarAsync<string>("SELECT envelope->>'tenantId' FROM audit.access_event_inbox;"));
        await using var reopened = new PostgresTenantAccessAudit(Config());
        Assert.Equal("Duplicate", (await reopened.AppendAsync(message, default)).Disposition);
        await Assert.ThrowsAsync<TenantAccessException>(() => reopened.AppendAsync(message with { Operation = "forged.change" }, default));
        await Assert.ThrowsAsync<TenantAccessException>(() => audit.ConsumeAsync("T002", message, default));
    }

    [Fact]
    public async Task AuditReceiptFailureRollsBackCanonicalEvidenceAndLegacyInbox()
    {
        await using var repository = new PostgresTenantAccessAudit(Config());
        await using var owner = await OpenAsync("AUDIT", "T001", "OWNER");
        await owner.ExecuteAsync("REVOKE INSERT ON audit.access_event_inbox FROM am05_audit_t001_runtime;");
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => repository.AppendAsync(AuditEvent(), default));
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("SELECT count(*) FROM audit.evidence;"));
            Assert.Equal(0, await owner.ExecuteScalarAsync<int>("SELECT count(*) FROM audit.inbox;"));
        }
        finally { await owner.ExecuteAsync("GRANT INSERT ON audit.access_event_inbox TO am05_audit_t001_runtime;"); }
    }

    [Fact]
    public async Task RuntimeCannotResurrectExtendSessionsOrChangeEvidenceAndTenantBindings()
    {
        await using var sessions = new PostgresTenantSessions(Config());
        await sessions.CreateAsync("T001", "A100", "immutable-session", 1, default);
        await sessions.RevokeAsync("T001", "immutable-session", default);
        await using var runtime = await OpenAsync("CI", "T001", "RUNTIME");
        foreach (var sql in new[]
        {
            "UPDATE customer_identity.trusted_sessions SET revoked=false;",
            "UPDATE customer_identity.trusted_sessions SET expires_at=clock_timestamp()+interval '1 year';",
            "DELETE FROM customer_identity.trusted_sessions;",
            "UPDATE customer_identity.tenant_identity SET tenant_id='T002';",
            "UPDATE customer_identity.access_event_inbox SET event_hash='forged';",
        }) await Assert.ThrowsAsync<PostgresException>(() => runtime.ExecuteAsync(sql));
        await using var audit = await OpenAsync("AUDIT", "T001", "RUNTIME");
        await Assert.ThrowsAsync<PostgresException>(() => audit.ExecuteAsync("DELETE FROM audit.evidence;"));
        await Assert.ThrowsAsync<PostgresException>(() => audit.ExecuteAsync("UPDATE audit.access_event_inbox SET event_hash='forged';"));
        var cross = new NpgsqlConnectionStringBuilder(Required("AM05_CI_T001_RUNTIME")) { Database = "am05_ci_t002" };
        await using var rejected = new NpgsqlConnection(cross.ConnectionString);
        await Assert.ThrowsAsync<PostgresException>(() => rejected.OpenAsync());
        await using var wrongBinding = new PostgresTenantSessions(Config(wrongBinding: true));
        await Assert.ThrowsAsync<TenantAccessException>(() => wrongBinding.ReadCurrentAsync("T002", "immutable-session", default));
    }

    [Fact]
    public async Task ExpirationAndConflictingSessionEventNeverReturnACurrentSession()
    {
        await using var sessions = new PostgresTenantSessions(Config());
        await sessions.CreateAsync("T001", "A100", "expired-session", 1, default);
        await using var owner = await OpenAsync("CI", "T001", "OWNER");
        await owner.ExecuteAsync("UPDATE customer_identity.trusted_sessions SET established_at=clock_timestamp()-interval '2 hours',expires_at=clock_timestamp()-interval '1 hour';");
        Assert.Null(await sessions.ReadCurrentAsync("T001", "expired-session", default));
        var message = Event();
        await sessions.ConsumeAsync("T001", "sessions", message, default);
        await Assert.ThrowsAsync<TenantAccessException>(() => sessions.ConsumeAsync("T001", "sessions", message with { SubjectVersion = 3 }, default));
    }

    private static IConfiguration Config(bool wrongBinding = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
            ["Monergy:AccessIntegration:Enabled"] = "true",
            ["Monergy:AccessIntegration:CustomerIdentityDatabases:T001"] = Required("AM05_CI_T001_RUNTIME"),
            ["Monergy:AccessIntegration:CustomerIdentityDatabases:T002"] = Required(wrongBinding ? "AM05_CI_T001_RUNTIME" : "AM05_CI_T002_RUNTIME"),
            ["Monergy:AccessIntegration:AuditDatabases:T001"] = Required("AM05_AUDIT_T001_RUNTIME"),
            ["Monergy:AccessIntegration:AuditDatabases:T002"] = Required("AM05_AUDIT_T002_RUNTIME"),
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Mandatory owner integration fixture is missing.");
    private static async Task<NpgsqlConnection> OpenAsync(string owner, string tenant, string kind)
    {
        var connection = new NpgsqlConnection(Required("AM05_" + owner + "_" + tenant + "_" + kind));
        await connection.OpenAsync();
        return connection;
    }
    private static AccessManagementEvent Event(long version = 2) => new("CID-069", Guid.NewGuid(), "TenantUserAccessChanged", "1.0.0",
        DateTimeOffset.UtcNow, "owner-tests", TenantAccessProtocol.Producer, "T001", "A100", "A900", version, version, "member.changed", "A100");
    private static AccessManagementEvent AuditEvent() => Event() with
    {
        ContractId = "CID-070",
        EventName = "AccessManagementAuditEvidence",
        SubjectActorId = null,
    };
}
