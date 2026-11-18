namespace Monergy.Platform.ControlPlane;

public enum TenantLifecycleState
{
    Requested,
    Provisioning,
    ReadyForAdmin,
    Active,
    ProvisioningFailed,
    Suspended,
    Decommissioning,
    Decommissioned,
}

public enum TenantPlacementMode
{
    Shared,
    Dedicated,
}

public enum TenantProvisioningStep
{
    PlacementResolved = 1,
    ServiceDatabasesProvisioned = 2,
    ServiceMigrationsApplied = 3,
    InitialAdministratorIdentityProvisioned = 4,
    AccessManagementBootstrapped = 5,
    MandatoryReadinessValidated = 6,
    // Kept out of the historic numeric sequence so persisted PRE-AZURE-02
    // receipts retain their original identities during the schema upgrade.
    TenantEndpointRegistered = 7,
}

public static class TenantProvisioningPlan
{
    public static IReadOnlyList<TenantProvisioningStep> RequiredSteps { get; } =
    [
        TenantProvisioningStep.PlacementResolved,
        TenantProvisioningStep.TenantEndpointRegistered,
        TenantProvisioningStep.ServiceDatabasesProvisioned,
        TenantProvisioningStep.ServiceMigrationsApplied,
        TenantProvisioningStep.InitialAdministratorIdentityProvisioned,
        TenantProvisioningStep.AccessManagementBootstrapped,
        TenantProvisioningStep.MandatoryReadinessValidated,
    ];

    public static int Count => RequiredSteps.Count;
}

public enum TenantEndpointStatus
{
    Reserved,
    Registered,
    Disabled,
}

public sealed record TenantEndpointRegistration(
    string TenantId,
    string OrganizationId,
    string TenantSlug,
    string Hostname,
    string EnvironmentReference,
    string DeploymentReference,
    TenantPlacementMode DeploymentMode,
    TenantEndpointStatus Status,
    bool IsPrimary,
    DateTimeOffset RegisteredAt);

public sealed record TenantEndpointResolution(
    string TenantId,
    string OrganizationId,
    string DisplayName,
    TenantEndpointRegistration Endpoint);

public sealed record TenantOnboardingRequest(
    string RequestId,
    string OrganizationName,
    string DisplayName,
    string CountryCode,
    string TimeZone,
    string Environment,
    string InitialAdministratorEmail,
    TenantPlacementMode PlacementMode,
    string? TenantSlug = null);

public sealed record TenantProvisioningReceipt(
    TenantProvisioningStep Step,
    string OperationId,
    string EvidenceReference,
    DateTimeOffset CompletedAt);

public sealed record TenantRegistryRecord(
    string TenantId,
    string RequestId,
    string OrganizationName,
    string DisplayName,
    string CountryCode,
    string TimeZone,
    string Environment,
    string InitialAdministratorEmail,
    TenantPlacementMode PlacementMode,
    TenantLifecycleState State,
    long ConfigurationVersion,
    IReadOnlyList<TenantProvisioningReceipt> Receipts,
    string? LastFailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool HasCompleted(TenantProvisioningStep step) => Receipts.Any(receipt => receipt.Step == step);
    public string? SetupReceiptReference { get; init; }
    public string OrganizationId { get; init; } = string.Empty;
    public TenantEndpointRegistration? Endpoint { get; init; }
}

// Supplied by a server-side setup owner after current tenant/session/admin checks;
// never bind this value directly from browser input. No adapter means not ready.
public sealed record TenantSetupReadiness(string TenantId, bool Ready, string? ReceiptReference);

public interface ITenantSetupReadiness
{
    Task<TenantSetupReadiness> ReadAsync(string tenantId, CancellationToken cancellationToken);
}

public sealed record TenantProvisioningResult(
    bool Succeeded,
    string? EvidenceReference,
    string? FailureCode)
{
    public static TenantProvisioningResult Success(string evidenceReference) =>
        new(true, evidenceReference, null);

    public static TenantProvisioningResult Failure(string failureCode) =>
        new(false, null, failureCode);
}
