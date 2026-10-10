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
}

public sealed record TenantOnboardingRequest(
    string RequestId,
    string OrganizationName,
    string DisplayName,
    string CountryCode,
    string TimeZone,
    string Environment,
    string InitialAdministratorEmail,
    TenantPlacementMode PlacementMode);

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
