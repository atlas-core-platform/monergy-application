namespace Monergy.Platform.ControlPlane;

public interface ITenantProvisioningOperation
{
    TenantProvisioningStep ProvisioningStep { get; }

    Task<TenantProvisioningResult> ExecuteAsync(
        TenantRegistryRecord tenant,
        string operationId,
        CancellationToken cancellationToken);
}

public sealed class TenantOnboardingOrchestrator
{
    private static readonly TenantProvisioningStep[] RequiredSteps =
    [
        TenantProvisioningStep.PlacementResolved,
        TenantProvisioningStep.ServiceDatabasesProvisioned,
        TenantProvisioningStep.ServiceMigrationsApplied,
        TenantProvisioningStep.InitialAdministratorIdentityProvisioned,
        TenantProvisioningStep.AccessManagementBootstrapped,
        TenantProvisioningStep.MandatoryReadinessValidated,
    ];

    private readonly ITenantRegistry registry;
    private readonly Dictionary<TenantProvisioningStep, ITenantProvisioningOperation> operations;
    private readonly TimeProvider clock;
    private readonly ITenantSetupReadiness? setupReadiness;

    public TenantOnboardingOrchestrator(
        ITenantRegistry registry,
        IEnumerable<ITenantProvisioningOperation> operations,
        TimeProvider clock,
        ITenantSetupReadiness? setupReadiness = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.setupReadiness = setupReadiness;

        var supplied = operations?.ToArray() ?? throw new ArgumentNullException(nameof(operations));
        if (supplied.Length != RequiredSteps.Length ||
            supplied.Select(item => item.ProvisioningStep).Distinct().Count() != supplied.Length ||
            RequiredSteps.Any(step => supplied.All(item => item.ProvisioningStep != step)))
            throw new InvalidOperationException("TENANT_PROVISIONING_OPERATIONS_INCOMPLETE");

        this.operations = supplied.ToDictionary(item => item.ProvisioningStep);
    }

    public Task<TenantRegistryRecord> RequestAsync(
        TenantOnboardingRequest request,
        CancellationToken cancellationToken = default) =>
        registry.CreateOrGetAsync(request, clock, cancellationToken);

    public async Task<TenantRegistryRecord> StartOrResumeAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var tenant = await registry.GetAsync(tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("TENANT_NOT_FOUND");

        if (tenant.State is TenantLifecycleState.ReadyForAdmin or TenantLifecycleState.Active)
            return tenant;
        if (tenant.State is TenantLifecycleState.Suspended or TenantLifecycleState.Decommissioning or TenantLifecycleState.Decommissioned)
            throw new InvalidOperationException("TENANT_LIFECYCLE_BLOCKS_PROVISIONING");
        if (tenant.State is not (TenantLifecycleState.Requested or TenantLifecycleState.Provisioning or TenantLifecycleState.ProvisioningFailed))
            throw new InvalidOperationException("TENANT_PROVISIONING_STATE_INVALID");

        if (tenant.State != TenantLifecycleState.Provisioning || tenant.LastFailureCode is not null)
        {
            tenant = await registry.ReplaceAsync(
                tenant with
                {
                    State = TenantLifecycleState.Provisioning,
                    LastFailureCode = null,
                    UpdatedAt = clock.GetUtcNow(),
                },
                tenant.ConfigurationVersion,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var step in RequiredSteps)
        {
            if (tenant.HasCompleted(step)) continue;

            var operationId = $"{tenant.TenantId}:{(int)step:D2}";
            TenantProvisioningResult result;
            try
            {
                result = await operations[step].ExecuteAsync(tenant, operationId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                result = TenantProvisioningResult.Failure("PROVISIONING_OPERATION_FAILED");
            }

            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.EvidenceReference))
            {
                tenant = await registry.ReplaceAsync(
                    tenant with
                    {
                        State = TenantLifecycleState.ProvisioningFailed,
                        LastFailureCode = NormalizeFailure(result.FailureCode),
                        UpdatedAt = clock.GetUtcNow(),
                    },
                    tenant.ConfigurationVersion,
                    cancellationToken).ConfigureAwait(false);
                return tenant;
            }

            var receipt = new TenantProvisioningReceipt(
                step,
                operationId,
                result.EvidenceReference,
                clock.GetUtcNow());
            tenant = await registry.ReplaceAsync(
                tenant with
                {
                    Receipts = tenant.Receipts.Concat([receipt]).ToArray(),
                    UpdatedAt = clock.GetUtcNow(),
                },
                tenant.ConfigurationVersion,
                cancellationToken).ConfigureAwait(false);
        }

        if (RequiredSteps.Any(step => !tenant.HasCompleted(step)))
            throw new InvalidOperationException("TENANT_READINESS_INCOMPLETE");

        return await registry.ReplaceAsync(
            tenant with
            {
                State = TenantLifecycleState.ReadyForAdmin,
                LastFailureCode = null,
                UpdatedAt = clock.GetUtcNow(),
            },
            tenant.ConfigurationVersion,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TenantRegistryRecord> ActivateAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var tenant = await registry.GetAsync(tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("TENANT_NOT_FOUND");
        if (tenant.State == TenantLifecycleState.Active) return tenant;
        if (tenant.State != TenantLifecycleState.ReadyForAdmin ||
            RequiredSteps.Any(step => !tenant.HasCompleted(step)))
            throw new InvalidOperationException("TENANT_NOT_READY_FOR_ACTIVATION");

        var setup = setupReadiness is null ? null :
            await setupReadiness.ReadAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (setup is null || !setup.Ready || setup.TenantId != tenantId ||
            string.IsNullOrWhiteSpace(setup.ReceiptReference) || setup.ReceiptReference.Length > 300 ||
            setup.ReceiptReference.Any(char.IsControl))
            throw new InvalidOperationException("TENANT_ADMIN_SETUP_REQUIRED");

        return await registry.ReplaceAsync(
            tenant with
            {
                State = TenantLifecycleState.Active,
                SetupReceiptReference = setup.ReceiptReference,
                LastFailureCode = null,
                UpdatedAt = clock.GetUtcNow(),
            },
            tenant.ConfigurationVersion,
            cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeFailure(string? failureCode) =>
        !string.IsNullOrWhiteSpace(failureCode) &&
        failureCode.Length <= 100 &&
        failureCode.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? failureCode
            : "PROVISIONING_OPERATION_FAILED";
}
