using Monergy.Platform.ControlPlane;
using Xunit;

namespace Monergy.Architecture.Tests;

public sealed class TenantControlPlaneTests
{
    [Fact]
    public async Task OnboardingRequestIsIdempotentAndConflictingReplayFails()
    {
        var path = TemporaryRegistry();
        try
        {
            var clock = new FixedClock();
            var registry = new FileTenantRegistry(path);
            var request = Request();

            var first = await registry.CreateOrGetAsync(request, clock, default);
            var replay = await registry.CreateOrGetAsync(request, clock, default);

            Assert.Equal(first.TenantId, replay.TenantId);
            Assert.Equal(TenantLifecycleState.Requested, replay.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                registry.CreateOrGetAsync(request with { DisplayName = "Changed name" }, clock, default));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task FailedProvisioningResumesWithoutRepeatingSuccessfulStepsAndActivationRequiresReadiness()
    {
        var path = TemporaryRegistry();
        try
        {
            var clock = new FixedClock();
            var registry = new FileTenantRegistry(path);
            var operations = Enum.GetValues<TenantProvisioningStep>()
                .Select(step => new StubOperation(step,
                    step == TenantProvisioningStep.AccessManagementBootstrapped))
                .ToArray();
            var orchestrator = new TenantOnboardingOrchestrator(registry, operations, clock);
            var tenant = await orchestrator.RequestAsync(Request());

            var failed = await orchestrator.StartOrResumeAsync(tenant.TenantId);
            Assert.Equal(TenantLifecycleState.ProvisioningFailed, failed.State);
            Assert.Equal("REFERENCE_FAILURE", failed.LastFailureCode);
            Assert.Equal(4, failed.Receipts.Count);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                orchestrator.ActivateAsync(tenant.TenantId));

            var ready = await orchestrator.StartOrResumeAsync(tenant.TenantId);
            Assert.Equal(TenantLifecycleState.ReadyForAdmin, ready.State);
            Assert.Equal(6, ready.Receipts.Count);
            Assert.Equal(6, ready.Receipts.Select(receipt => receipt.Step).Distinct().Count());

            foreach (var operation in operations.Take(4))
                Assert.Equal(1, operation.Attempts);
            Assert.Equal(2, operations[4].Attempts);
            Assert.Equal(1, operations[5].Attempts);

            var active = await orchestrator.ActivateAsync(tenant.TenantId);
            Assert.Equal(TenantLifecycleState.Active, active.State);

            var replay = await orchestrator.StartOrResumeAsync(tenant.TenantId);
            Assert.Equal(TenantLifecycleState.Active, replay.State);
            Assert.All(operations.Take(4), operation => Assert.Equal(1, operation.Attempts));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static TenantOnboardingRequest Request() =>
        new(
            "request-tenant-001",
            "Example Financial Services Private Limited",
            "Example Financial Services",
            "IN",
            "Asia/Kolkata",
            "uat",
            "tenant.admin@example.test",
            TenantPlacementMode.Shared);

    private static string TemporaryRegistry() =>
        Path.Combine(Path.GetTempPath(), "monergy-control-plane-" + Guid.NewGuid().ToString("N") + ".json");

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.Parse("2026-10-10T00:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class StubOperation(
        TenantProvisioningStep step,
        bool failFirst) : ITenantProvisioningOperation
    {
        public TenantProvisioningStep Step { get; } = step;
        public int Attempts { get; private set; }

        public Task<TenantProvisioningResult> ExecuteAsync(
            TenantRegistryRecord tenant,
            string operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal($"{tenant.TenantId}:{(int)Step:D2}", operationId);
            Attempts++;
            if (failFirst && Attempts == 1)
                return Task.FromResult(TenantProvisioningResult.Failure("REFERENCE_FAILURE"));
            return Task.FromResult(TenantProvisioningResult.Success($"evidence:{Step}:{operationId}"));
        }
    }
}
