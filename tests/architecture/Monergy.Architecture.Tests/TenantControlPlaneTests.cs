using Monergy.DatabaseMigrator;
using Monergy.Platform.ControlPlane;
using System.Text.Json;
using Xunit;

namespace Monergy.Architecture.Tests;

public sealed class TenantControlPlaneTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("T001", "t001")]
    [InlineData("T002", "t002")]
    public async Task ReferenceBootstrapRequestIsStableAcrossPreparationAndReadback(
        string tenantId,
        string expectedSlug)
    {
        var path = TemporaryRegistry();
        try
        {
            var instanceId = Guid.NewGuid().ToString("N");
            var request = LocalUatBootstrap.ReferenceTenantRequest(instanceId, tenantId);
            using var registry = new FileTenantRegistry(path,
                new Dictionary<string, string>(StringComparer.Ordinal)
                { [instanceId + ":" + tenantId] = tenantId });

            var prepared = await registry.CreateOrGetAsync(request, new FixedClock(), default);
            var readback = await registry.CreateOrGetAsync(
                LocalUatBootstrap.ReferenceTenantRequest(instanceId, tenantId), new FixedClock(), default);

            Assert.Equal(tenantId, prepared.TenantId);
            Assert.Equal(expectedSlug, prepared.Endpoint?.TenantSlug);
            Assert.Equal(prepared.TenantId, readback.TenantId);
            Assert.Equal(prepared.RequestId, readback.RequestId);
            Assert.Equal(prepared.ConfigurationVersion, readback.ConfigurationVersion);
            Assert.Equal(prepared.Endpoint, readback.Endpoint);
            Assert.Equal(prepared.Receipts, readback.Receipts);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".lock")) File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task LifecycleAuditIsRetainedIdempotentAndHasNoSecretPayloadChannel()
    {
        var path = TemporaryRegistry();
        try
        {
            using var audit = new FileTenantLifecycleAudit(path);
            var at = DateTimeOffset.Parse("2026-11-18T01:02:03Z",
                global::System.Globalization.CultureInfo.InvariantCulture);
            await audit.AppendAsync("tenant-03:OrganizationRequested:1", "tenant-03", "A950",
                "OrganizationRequested", "Succeeded", 1, at, default);
            await audit.AppendAsync("tenant-03:OrganizationRequested:1", "tenant-03", "A950",
                "OrganizationRequested", "Succeeded", 1, at, default);
            var items = await audit.ListAsync(default);
            var item = Assert.Single(items);
            Assert.Equal(1, item.LifecycleRevision);
            Assert.Equal("A950", item.ActorId);
            Assert.DoesNotContain("password", await File.ReadAllTextAsync(path),
                StringComparison.OrdinalIgnoreCase);
            await Assert.ThrowsAsync<InvalidOperationException>(() => audit.AppendAsync(
                "tenant-03:OrganizationRequested:1", "tenant-03", "another-actor",
                "OrganizationRequested", "Succeeded", 1, at, default));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".lock")) File.Delete(path + ".lock");
        }
    }

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
            var operations = TenantProvisioningPlan.RequiredSteps
                .Select(step => new StubOperation(step,
                    step == TenantProvisioningStep.AccessManagementBootstrapped))
                .ToArray();
            var orchestrator = new TenantOnboardingOrchestrator(registry, operations, clock, new SetupReadiness());
            var tenant = await orchestrator.RequestAsync(Request());

            var failed = await orchestrator.StartOrResumeAsync(tenant.TenantId);
            Assert.Equal(TenantLifecycleState.ProvisioningFailed, failed.State);
            Assert.Equal("REFERENCE_FAILURE", failed.LastFailureCode);
            Assert.Equal(5, failed.Receipts.Count);
            Assert.Equal(TenantEndpointStatus.Registered, failed.Endpoint?.Status);
            Assert.Contains(failed.Receipts, receipt => receipt.Step == TenantProvisioningStep.TenantEndpointRegistered);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                orchestrator.ActivateAsync(tenant.TenantId));

            var ready = await orchestrator.StartOrResumeAsync(tenant.TenantId);
            Assert.Equal(TenantLifecycleState.ReadyForAdmin, ready.State);
            Assert.Equal(TenantProvisioningPlan.Count, ready.Receipts.Count);
            Assert.Equal(TenantProvisioningPlan.Count, ready.Receipts.Select(receipt => receipt.Step).Distinct().Count());

            foreach (var operation in operations.Take(5))
                Assert.Equal(1, operation.Attempts);
            Assert.Equal(2, operations[5].Attempts);
            Assert.Equal(1, operations[6].Attempts);

            var active = await orchestrator.ActivateAsync(tenant.TenantId);
            Assert.Equal(TenantLifecycleState.Active, active.State);
            Assert.Equal("setup-receipt:001", active.SetupReceiptReference);

            var replay = await orchestrator.StartOrResumeAsync(tenant.TenantId);
            Assert.Equal(TenantLifecycleState.Active, replay.State);
            Assert.All(operations.Take(5), operation => Assert.Equal(1, operation.Attempts));
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
            var operations = TenantProvisioningPlan.RequiredSteps.Select(step => new StubOperation(step, false)).ToArray();
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
            var operations = TenantProvisioningPlan.RequiredSteps.Select(step => new StubOperation(step, false)).ToArray();
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
                Request() with { RequestId = "request-" + i, TenantSlug = "example-" + i }, new FixedClock(), default));
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

    [Fact]
    public async Task EndpointRegistrationIsStableResolvableAndEnvironmentUnique()
    {
        var path = TemporaryRegistry();
        try
        {
            var staticEndpoint = new TenantEndpointRegistration("T001", "org-t001", "t001", "t001.localhost",
                "uat", "uat-shared", TenantPlacementMode.Shared, TenantEndpointStatus.Registered, true, DateTimeOffset.UnixEpoch);
            using var registry = new FileTenantRegistry(path, staticEndpoints:
                new Dictionary<string, TenantEndpointResolution>(StringComparer.OrdinalIgnoreCase)
                { ["t001.localhost"] = new("T001", "org-t001", "Reference tenant", staticEndpoint) });
            var request = Request() with { TenantSlug = "example-finance" };
            var tenant = await registry.CreateOrGetAsync(request, new FixedClock(), default);
            var replay = await registry.CreateOrGetAsync(request, new FixedClock(), default);
            Assert.Equal(tenant.Endpoint, replay.Endpoint);
            Assert.Equal("example-finance.localhost", tenant.Endpoint?.Hostname);
            Assert.Equal(TenantEndpointStatus.Reserved, tenant.Endpoint?.Status);

            var operations = TenantProvisioningPlan.RequiredSteps.Select(step => new StubOperation(step, false));
            var ready = await new TenantOnboardingOrchestrator(registry, operations, new FixedClock())
                .StartOrResumeAsync(tenant.TenantId);
            var resolved = await registry.ResolveEndpointAsync("example-finance.localhost", "uat", default);
            Assert.Equal(ready.TenantId, resolved?.TenantId);
            Assert.Equal(ready.OrganizationId, resolved?.OrganizationId);
            Assert.Equal(TenantEndpointStatus.Registered, resolved?.Endpoint.Status);
            Assert.Equal("T001", (await registry.ResolveEndpointAsync("T001.LOCALHOST", "uat", default))?.TenantId);
            Assert.Null(await registry.ResolveEndpointAsync("unknown.localhost", "uat", default));

            var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CreateOrGetAsync(
                request with { RequestId = "another-request", OrganizationName = "Another", TenantSlug = "example-finance" },
                new FixedClock(), default));
            Assert.Equal("TENANT_ENDPOINT_CONFLICT", conflict.Message);
        }
        finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".lock")) File.Delete(path + ".lock"); }
    }

    [Theory]
    [InlineData("platform", "TENANT_SLUG_RESERVED")]
    [InlineData("UPPERCASE", "INVALID_TENANT_SLUG")]
    [InlineData("has spaces", "INVALID_TENANT_SLUG")]
    [InlineData("-leading", "INVALID_TENANT_SLUG")]
    public async Task UnsafeOrReservedSlugsFailClosed(string slug, string expected)
    {
        var path = TemporaryRegistry();
        try
        {
            using var registry = new FileTenantRegistry(path);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CreateOrGetAsync(
                Request() with { TenantSlug = slug }, new FixedClock(), default));
            Assert.Equal(expected, failure.Message);
        }
        finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".lock")) File.Delete(path + ".lock"); }
    }

    [Fact]
    public async Task VersionOneRegistryUpgradeAddsOnlyDeterministicEndpointReceipt()
    {
        var path = TemporaryRegistry();
        try
        {
            var now = new FixedClock().GetUtcNow();
            var receipts = Enumerable.Range(1, 6).Select(value => new
            {
                step = value, operationId = $"tenant-legacy:{value:D2}", evidenceReference = "legacy:" + value,
                completedAt = now,
            });
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                tenants = new[] { new
                {
                    tenantId = "tenant-legacy", requestId = "legacy-request", organizationName = "Legacy Finance",
                    displayName = "Legacy Finance", countryCode = "IN", timeZone = "Asia/Kolkata", environment = "uat",
                    initialAdministratorEmail = "admin@example.test", placementMode = 0, state = 2,
                    configurationVersion = 9, receipts, lastFailureCode = (string?)null, createdAt = now, updatedAt = now,
                } },
            }, WebJson));
            using var registry = new FileTenantRegistry(path);
            var upgraded = await registry.GetAsync("tenant-legacy", default);
            Assert.NotNull(upgraded);
            Assert.Equal(TenantProvisioningPlan.Count, upgraded.Receipts.Count);
            Assert.Equal(TenantProvisioningPlan.RequiredSteps, upgraded.Receipts.Select(item => item.Step));
            Assert.Equal("tenant-legacy:07", upgraded.Receipts[1].OperationId);
            Assert.Equal(TenantEndpointStatus.Registered, upgraded.Endpoint?.Status);
            Assert.Equal(2, JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement.GetProperty("schemaVersion").GetInt32());
        }
        finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".lock")) File.Delete(path + ".lock"); }
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
