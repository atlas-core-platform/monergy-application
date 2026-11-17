using Monergy.Platform.ControlPlane;
using Xunit;

namespace Monergy.Architecture.Tests;

public sealed class PlatformOperationsTests
{
    [Fact]
    public async Task PlatformCredentialActivationIsProtectedSingleUseAndVersioned()
    {
        var path = TemporaryPath();
        try
        {
            using var store = new FilePlatformCredentialStore(path, "a950.platform@example.test", new string('t', 64), new FixedClock());
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.IssueActivationAsync(
                "a950.platform@example.test", "wrong", default));
            var activation = await store.IssueActivationAsync("a950.platform@example.test", new string('t', 64), default);
            await store.ActivateAsync(activation.Ticket, "Platform!Password123", default);
            var authentication = await store.AuthenticateAsync("a950.platform@example.test", "Platform!Password123", default);
            Assert.Equal("A950", authentication.ActorId);
            Assert.Equal(1, authentication.CredentialVersion);
            var replay = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ActivateAsync(activation.Ticket, "Another!Password123", default));
            Assert.Equal("PLATFORM_ACTIVATION_TICKET_INVALID", replay.Message);
            var persisted = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("Platform!Password123", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(activation.Ticket, persisted, StringComparison.Ordinal);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task IntegrationConfigurationIsVersionedTenantScopedAndContainsReferencesOnly()
    {
        var path = TemporaryPath();
        try
        {
            using var store = new FileIntegrationConfigurationStore(path, new FixedClock());
            var changed = await store.ChangeAsync(new(Guid.NewGuid().ToString("N"), 0, "email", true,
                "Organization", "https://mail.example.test", "safe-client", null, null,
                new Dictionary<string, string> { ["tenantId"] = "T001" }, ["smtp-primary"], "Configured", "A950"), default);
            Assert.Equal(1, changed.Revision);
            var provider = Assert.Single(changed.Providers, item => item.Code == "email");
            Assert.Equal("T001", provider.Options["tenantId"]);
            Assert.Equal(["smtp-primary"], provider.SecretReferences);
            var persisted = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("password", persisted, StringComparison.OrdinalIgnoreCase);
            var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ChangeAsync(new(
                Guid.NewGuid().ToString("N"), 0, "ai", true, "Platform", null, null, null, "model", null,
                ["ai-primary"], "Configured", "A950"), default));
            Assert.Equal("INTEGRATION_CONFIGURATION_CONFLICT", stale.Message);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task PostureChangesAreVersionedIdempotentAndAuditable()
    {
        var path = TemporaryPath();
        try
        {
            using var store = new FilePlatformOperationsStore(path, ["reporting", "evidence"], new FixedClock());
            var operationId = Guid.NewGuid().ToString("N");
            var change = new PlatformPostureChange(
                operationId, "reporting", PlatformOperationalPosture.Investigating,
                "Validating a release dependency.", "A950", 0);

            var changed = await store.ChangeAsync(change, default);
            var replay = await store.ChangeAsync(change, default);
            using var restoredStore = new FilePlatformOperationsStore(path, ["reporting", "evidence"]);
            var restored = await restoredStore.ReadAsync(default);

            Assert.Equal(1, changed.Version);
            Assert.Equal(1, replay.Version);
            Assert.Equal(1, restored.Version);
            Assert.Single(restored.Services);
            Assert.Single(restored.Receipts);
            Assert.Equal("reporting", restored.Services[0].ServiceId);
            Assert.Equal(PlatformOperationalPosture.Investigating, restored.Services[0].Posture);
            Assert.Equal(operationId, restored.Receipts[0].OperationId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task StaleUnknownAndAuthorityBypassChangesFailClosed()
    {
        var path = TemporaryPath();
        try
        {
            using var store = new FilePlatformOperationsStore(path, ["reporting"]);
            await store.ChangeAsync(Change("reporting", "A950", 0), default);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ChangeAsync(Change("reporting", "A950", 0), default));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ChangeAsync(Change("evidence", "A950", 1), default));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ChangeAsync(Change("reporting", "A900", 1), default));
            var snapshot = await store.ReadAsync(default);
            Assert.Equal(1, snapshot.Version);
            Assert.Single(snapshot.Receipts);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task OperationReplayCannotBeRepurposed()
    {
        var path = TemporaryPath();
        try
        {
            using var store = new FilePlatformOperationsStore(path, ["reporting"]);
            var operation = Change("reporting", "A950", 0);
            await store.ChangeAsync(operation, default);
            var repurposed = operation with { Posture = PlatformOperationalPosture.Maintenance };
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ChangeAsync(repurposed, default));
            Assert.Equal("PLATFORM_OPERATION_REPLAY_CONFLICT", failure.Message);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static PlatformPostureChange Change(string service, string actor, long expectedVersion) =>
        new(Guid.NewGuid().ToString("N"), service, PlatformOperationalPosture.Normal,
            "Operational review complete.", actor, expectedVersion);

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), "monergy-platform-operations-" + Guid.NewGuid().ToString("N") + ".json");

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.Parse("2026-11-16T12:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
    }
}
