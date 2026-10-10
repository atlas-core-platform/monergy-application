using Monergy.Platform.ControlPlane;
using Xunit;

namespace Monergy.Architecture.Tests;

public sealed class TenantSetupTests
{
    [Fact]
    public async Task SetupRequiresCurrentReviewAndPersistsAnIdempotentReceiptAcrossInstances()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new FileTenantSetup(Path.Combine(folder, "setup.json"));
            var request = new TenantSetupSubmission("review-1", 7, true, true);
            var tenant = Ready("T001");
            Assert.False((await store.ReadAsync("T001", default)).Ready);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(tenant, "A900", 8, request, TimeProvider.System, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(tenant, "A900", 7, request with { AccessReviewed = false }, TimeProvider.System, default));
            var receipt = await store.CompleteAsync(tenant, "A900", 7, request, TimeProvider.System, default);
            var reopened = new FileTenantSetup(Path.Combine(folder, "setup.json"));
            Assert.Equal(receipt, await reopened.CompleteAsync(tenant, "A900", 8, request, TimeProvider.System, default));
            Assert.Equal(receipt.ReceiptReference, (await reopened.ReadAsync("T001", default)).ReceiptReference);
            Assert.False((await reopened.ReadAsync("T002", default)).Ready);
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.CompleteAsync(tenant, "A901", 7, request, TimeProvider.System, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.CompleteAsync(tenant, "A900", 8, request with { ExpectedPolicyVersion = 8 }, TimeProvider.System, default));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ParallelSetupReceiptsDoNotOverwriteOtherTenants()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "setup.json");
            var receipts = await Task.WhenAll(Enumerable.Range(1, 12).Select(index => new FileTenantSetup(path).CompleteAsync(
                Ready("tenant-" + index), "A900", 1, new("review-" + index, 1, true, true), TimeProvider.System, default)));
            foreach (var receipt in receipts) Assert.Equal(receipt, await new FileTenantSetup(path).GetAsync(receipt.TenantId, default));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task IncompleteProvisioningCannotProduceSetupReceipt()
    {
        var folder = TemporaryFolder();
        try
        {
            var setup = new FileTenantSetup(Path.Combine(folder, "setup.json"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => setup.CompleteAsync(Ready("T001") with { State = TenantLifecycleState.ProvisioningFailed },
                "A900", 1, new("review", 1, true, true), TimeProvider.System, default));
            Assert.False((await setup.ReadAsync("T001", default)).Ready);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task FixedFixtureTenantBindingsCannotApplyOutsideLocalOrCreateAnotherTenant()
    {
        var folder = TemporaryFolder();
        try
        {
            using var registry = new FileTenantRegistry(Path.Combine(folder, "registry.json"), new Dictionary<string, string> { ["local-request"] = "T001" });
            var request = new TenantOnboardingRequest("local-request", "Local organization", "Local", "US", "UTC", "LOCAL", "a900@example.test", TenantPlacementMode.Shared);
            Assert.Equal("T001", (await registry.CreateOrGetAsync(request, TimeProvider.System, default)).TenantId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CreateOrGetAsync(request with { Environment = "Production" }, TimeProvider.System, default));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static TenantRegistryRecord Ready(string tenant) => new(tenant, "request-" + tenant, "Local organization", "Local", "US", "UTC", "LOCAL",
        "a900@example.test", TenantPlacementMode.Shared, TenantLifecycleState.ReadyForAdmin, 8,
        Enum.GetValues<TenantProvisioningStep>().Select(step => new TenantProvisioningReceipt(step, $"{tenant}:{(int)step:D2}", "test-only:" + step, DateTimeOffset.UtcNow)).ToArray(),
        null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static string TemporaryFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "monergy-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); return folder;
    }
}
