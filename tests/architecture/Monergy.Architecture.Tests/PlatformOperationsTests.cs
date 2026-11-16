using Monergy.Platform.ControlPlane;
using Xunit;

namespace Monergy.Architecture.Tests;

public sealed class PlatformOperationsTests
{
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
