namespace Monergy.Contracts;

public sealed record D14ContractDefinition(
    string Id,
    string Name,
    string Type,
    string Owner,
    string Treatment);

public static class D14ContractCatalog
{
    public static IReadOnlyList<D14ContractDefinition> All { get; } =
    [
        new("CID-001", D13ContractNames.GetCustomer, "QUERY", "Customer & Identity Service", "PRESERVED_D13"),
        new("CID-002", D13ContractNames.GetTrustedActorContext, "QUERY", "Customer & Identity Service", "ADVANCED_CURRENT_SESSION_TRUST"),
        new("CID-005", D14ContractNames.CustomerIdentityChanged, "EVENT", "Customer & Identity Service", "NEWLY_REALIZED_PRODUCER"),
    ];
}
