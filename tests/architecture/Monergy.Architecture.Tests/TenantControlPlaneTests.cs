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
            var orchestrator = new TenantOnboardingOrchestrator(registry, operations, clock, new SetupReadiness());
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
            Assert.Equal("setup-receipt:001", active.SetupReceiptReference);

            var replay = await orchestrator.StartOrResumeAsync(tenant.TenantId);
            Assert.Equal(TenantLifecycleState.Active, replay.State);
            Assert.All(operations.Take(4), operation => Assert.Equal(1, operation.Attempts));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ActivationWithoutSetupOwnerOrWithAnotherTenantsReceiptFailsClosed()
    {
        var path = TemporaryRegistry();
        try
        {
            var registry = new FileTenantRegistry(path);
            var operations = Enum.GetValues<TenantProvisioningStep>().Select(step => new StubOperation(step, false)).ToArray();
            var orchestrator = new TenantOnboardingOrchestrator(registry, operations, new FixedClock());
            var tenant = await orchestrator.RequestAsync(Request());
            await orchestrator.StartOrResumeAsync(tenant.TenantId);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ActivateAsync(tenant.TenantId));
            Assert.Equal("TENANT_ADMIN_SETUP_REQUIRED", error.Message);
            var mismatched = new TenantOnboardingOrchestrator(registry, operations, new FixedClock(), new SetupReadiness("another-tenant"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => mismatched.ActivateAsync(tenant.TenantId));
            Assert.Equal(TenantLifecycleState.ReadyForAdmin, (await registry.GetAsync(tenant.TenantId, default))!.State);
        }
        finally { File.Delete(path); File.Delete(path + ".lock"); }
    }

    [Fact]
    public async Task RegistryRejectsReceiptRewritesSkippedReadinessAndIdentityChanges()
    {
        var path = TemporaryRegistry();
        try
        {
            var registry = new FileTenantRegistry(path);
            var tenant = await registry.CreateOrGetAsync(Request(), new FixedClock(), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ReplaceAsync(
                tenant with { State = TenantLifecycleState.Active, SetupReceiptReference = "fake" }, tenant.ConfigurationVersion, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ReplaceAsync(
                tenant with { State = TenantLifecycleState.Provisioning, InitialAdministratorEmail = "replacement@example.test" }, tenant.ConfigurationVersion, default));
            var operations = Enum.GetValues<TenantProvisioningStep>().Select(step => new StubOperation(step, false)).ToArray();
            var ready = await new TenantOnboardingOrchestrator(registry, operations, new FixedClock()).StartOrResumeAsync(tenant.TenantId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ReplaceAsync(
                ready with { Receipts = ready.Receipts.Skip(1).ToArray() }, ready.ConfigurationVersion, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ReplaceAsync(
                ready with { Receipts = ready.Receipts.Select(receipt => receipt with { EvidenceReference = "rewritten" }).ToArray() }, ready.ConfigurationVersion, default));
            var unchanged = await registry.GetAsync(tenant.TenantId, default);
            Assert.Equal(ready.ConfigurationVersion, unchanged!.ConfigurationVersion);
            Assert.Equal(ready.Receipts, unchanged.Receipts);
        }
        finally { File.Delete(path); File.Delete(path + ".lock"); }
    }

    [Fact]
    public async Task IndependentRegistryInstancesDoNotLoseConcurrentRequestsAndRejectStaleWrites()
    {
        var path = TemporaryRegistry();
        try
        {
            var tasks = Enumerable.Range(0, 12).Select(i => new FileTenantRegistry(path).CreateOrGetAsync(
                Request() with { RequestId = "request-" + i }, new FixedClock(), default));
            var tenants = await Task.WhenAll(tasks);
            Assert.Equal(12, tenants.Select(tenant => tenant.TenantId).Distinct().Count());
            foreach (var tenant in tenants)
                Assert.NotNull(await new FileTenantRegistry(path).GetAsync(tenant.TenantId, default));
            var first = tenants[0];
            await new FileTenantRegistry(path).ReplaceAsync(first with { State = TenantLifecycleState.Provisioning }, first.ConfigurationVersion, default);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FileTenantRegistry(path).ReplaceAsync(
                first with { State = TenantLifecycleState.Provisioning }, first.ConfigurationVersion, default));
            Assert.Equal("TENANT_CONFIGURATION_CONFLICT", error.Message);
        }
        finally { File.Delete(path); File.Delete(path + ".lock"); }
    }

    private sealed class SetupReadiness(string? overrideTenant = null) : ITenantSetupReadiness
    {
        public Task<TenantSetupReadiness> ReadAsync(string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantSetupReadiness(overrideTenant ?? tenantId, true, "setup-receipt:001"));
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
            DateTimeOffset.Parse("2026-10-10T00:00:00+00:00", global::System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class StubOperation(
        TenantProvisioningStep step,
        bool failFirst) : ITenantProvisioningOperation
    {
        public TenantProvisioningStep ProvisioningStep { get; } = step;
        public int Attempts { get; private set; }

        public Task<TenantProvisioningResult> ExecuteAsync(
            TenantRegistryRecord tenant,
            string operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal($"{tenant.TenantId}:{(int)ProvisioningStep:D2}", operationId);
            Attempts++;
            if (failFirst && Attempts == 1)
                return Task.FromResult(TenantProvisioningResult.Failure("REFERENCE_FAILURE"));
            return Task.FromResult(TenantProvisioningResult.Success($"evidence:{ProvisioningStep}:{operationId}"));
        }
    }
}
