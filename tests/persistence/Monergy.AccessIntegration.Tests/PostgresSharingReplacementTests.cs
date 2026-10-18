using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Platform;
using Monergy.Services.Consent;
using Npgsql;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

// Requires isolated, migrated Consent databases with the existing runtime grants.
// Never point these destructive fixtures at a shared or production database.
[Trait("Category", "Physical")]
public sealed class PostgresSharingReplacementTests : IAsyncLifetime
{
    private static readonly CustomerOwnerContext Owner = new("T001", "C001", "A100", 1, true);
    public async Task InitializeAsync()
    {
        await using var admin = await AdminAsync();
        await admin.ExecuteAsync("TRUNCATE consent.outbox,consent.audit,consent.receipts,consent.grants; UPDATE consent.state SET version=1 WHERE singleton;");
    }
    public Task DisposeAsync() => Task.CompletedTask;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementPreservesHistoryReplaysAndLeavesOneCurrentGrant(bool expired)
    {
        await using var store = new PostgresCustomerConsent(Config());
        var old = await SeedAsync(store);
        if (expired) { await using var admin = await AdminAsync(); await admin.ExecuteAsync("UPDATE consent.grants SET granted_at=clock_timestamp()-interval '3 days', expires_at=clock_timestamp()-interval '1 day' WHERE grant_id=@Id;", old); }
        var change = Change(old.Id); var result = await store.ReplaceAsync("T001", Owner, "A100", change, default);
        Assert.Equal(3, result.Revocation.Version); Assert.Equal(4, result.Receipt.Version);
        await using var reopened = new PostgresCustomerConsent(Config());
        Assert.Equal(result, await reopened.ReplaceAsync("T001", Owner, "A100", change, default));
        var overview = await reopened.ListAsync("T001", "C001", default);
        Assert.Equal(4, overview.Version); Assert.Equal(2, overview.Items.Count);
        Assert.NotNull(overview.Items.Single(x => x.Id == old.Id).RevokedAt);
        Assert.Equal(["financial-profile.profile.read"], overview.Items.Single(x => x.Id == result.Receipt.Id).CapabilityIds);
        Assert.Equal("CONSENT_IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<TenantBoundaryException>(() => reopened.ReplaceAsync("T001", Owner, "A100", change with { CapabilityIds = ["evidence.document.read"] }, default))).Code);
        await using var adminCheck = await AdminAsync();
        Assert.Equal(1, await adminCheck.ExecuteScalarAsync<int>("SELECT count(*) FROM consent.grants WHERE revoked_at IS NULL;"));
        Assert.Equal(3, await adminCheck.ExecuteScalarAsync<int>("SELECT count(*) FROM consent.audit;"));
        Assert.Equal(3, await adminCheck.ExecuteScalarAsync<int>("SELECT count(*) FROM consent.outbox;"));
        Assert.Equal(2, await adminCheck.ExecuteScalarAsync<int>("SELECT count(*) FROM consent.receipts;"));
        var removed = await reopened.EvaluateAsync("T001", Owner, new("T001", "A300", "C001", "evidence.document.read", "customer-advice"), default);
        Assert.False(removed.Allowed);
        Assert.True((await reopened.EvaluateAsync("T001", Owner, new("T001", "A300", "C001", "financial-profile.profile.read", "customer-advice"), default)).Allowed);
    }
    [Fact]
    public async Task ConcurrentChangesUseOneRevisionAndDoNotResurrectRevokedGrants()
    {
        await using var store = new PostgresCustomerConsent(Config()); var old = await SeedAsync(store);
        async Task<bool> Attempt() { try { await store.ReplaceAsync("T001", Owner, "A100", Change(old.Id), default); return true; } catch (TenantBoundaryException error) when (error.Code == "CONSENT_VERSION_CONFLICT") { return false; } }
        var outcomes = await Task.WhenAll(Attempt(), Attempt()); Assert.Single(outcomes, x => x);
        var current = (await store.ListAsync("T001", "C001", default)).Items.Single(x => x.RevokedAt is null);
        await store.RevokeAsync("T001", Owner, "A100", current.Id, new(Guid.NewGuid().ToString("N"), 4), default);
        Assert.Equal("CONSENT_NOT_CURRENT", (await Assert.ThrowsAsync<TenantBoundaryException>(() => store.ReplaceAsync("T001", Owner, "A100", Change(current.Id) with { ExpectedVersion = 5 }, default))).Code);
    }
    [Fact]
    public async Task FailedReceiptInsertRollsBackBothGrantsRevisionAuditAndOutbox()
    {
        await using var store = new PostgresCustomerConsent(Config()); var old = await SeedAsync(store);
        await using var admin = await AdminAsync();
        await using var runtime = new NpgsqlConnection(Required("T147_CONSENT_T001_RUNTIME")); await runtime.OpenAsync();
        var role = await runtime.ExecuteScalarAsync<string>("SELECT current_user;");
        using var identifiers = new NpgsqlCommandBuilder(); var quoted = identifiers.QuoteIdentifier(role!);
        await admin.ExecuteAsync("REVOKE INSERT ON consent.receipts FROM " + quoted);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => store.ReplaceAsync("T001", Owner, "A100", Change(old.Id), default));
            var overview = await store.ListAsync("T001", "C001", default); Assert.Equal(2, overview.Version); Assert.Null(Assert.Single(overview.Items).RevokedAt);
            Assert.Equal(1, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM consent.audit;"));
            Assert.Equal(1, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM consent.outbox;"));
            Assert.Equal(1, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM consent.receipts;"));
        }
        finally { await admin.ExecuteAsync("GRANT INSERT ON consent.receipts TO " + quoted); }
    }
    [Fact]
    public async Task ChangedOwnerVersionOrWrongTenantCannotReplaceTheOldGrant()
    {
        await using var store = new PostgresCustomerConsent(Config()); var old = await SeedAsync(store);
        Assert.Equal("CONSENT_NOT_CURRENT", (await Assert.ThrowsAsync<TenantBoundaryException>(() => store.ReplaceAsync("T001", Owner with { Version = 2 }, "A100", Change(old.Id), default))).Code);
        Assert.Equal("CUSTOMER_OWNER_REQUIRED", (await Assert.ThrowsAsync<TenantBoundaryException>(() => store.ReplaceAsync("T002", Owner, "A100", Change(old.Id), default))).Code);
        Assert.Null(Assert.Single((await store.ListAsync("T001", "C001", default)).Items).RevokedAt);
    }
    private static Task<ConsentReceipt> SeedAsync(PostgresCustomerConsent store) => store.GrantAsync("T001", Owner, "A100",
        new(Guid.NewGuid().ToString("N"), 1, "C001", "A300", "customer-advice", ["evidence.document.read"], DateTimeOffset.UtcNow.AddDays(1)), default);
    private static ConsentReplacementWrite Change(string grant) => new(Guid.NewGuid().ToString("N"), 2, "C001", grant, "A300", ["financial-profile.profile.read"], DateTimeOffset.UtcNow.AddDays(7));
    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["Monergy:ExecutionZone"] = "CI_EPHEMERAL", ["Monergy:AccessIntegration:Enabled"] = "true",
        ["Monergy:AccessIntegration:ConsentDatabases:T001"] = Required("T147_CONSENT_T001_RUNTIME") }).Build();
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Isolated T1-47 Consent fixture is required: " + name);
    private static async Task<NpgsqlConnection> AdminAsync() { var connection = new NpgsqlConnection(Required("T147_CONSENT_T001_OWNER")); await connection.OpenAsync(); return connection; }
}
