namespace Monergy.Contracts;

public static class D13ContractCatalog
{
    public static IReadOnlyList<ContractDefinition> All { get; } =
    [
        new("CID-001", D13ContractNames.GetCustomer, "QUERY", "Customer & Identity Service", "READ_ONLY"),
        new("CID-002", D13ContractNames.GetTrustedActorContext, "QUERY", "Customer & Identity Service", "READ_ONLY"),
        new("CID-003", D13ContractNames.RegisterOrUpdateCustomer, "COMMAND", "Customer & Identity Service", "IDEMPOTENCY_KEY_REQUIRED"),
        new("CID-004", D13ContractNames.RecordKycResult, "COMMAND", "Customer & Identity Service", "IDEMPOTENCY_KEY_REQUIRED"),
    ];
}
