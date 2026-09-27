namespace Monergy.Contracts;

public static class D06ContractCatalog
{
    public static IReadOnlyList<ContractDefinition> All { get; } = Array.AsReadOnly<ContractDefinition>(
    [
        new("CID-015", IntegrationGatewayContractNames.ExecuteProviderRequest, "INTEGRATION", "Integration Gateway Service", "IDEMPOTENCY_KEY_REQUIRED"),
        new("CID-016", IntegrationGatewayContractNames.GetProviderOperationStatus, "QUERY", "Integration Gateway Service", "READ_ONLY"),
        new("CID-017", IntegrationGatewayContractNames.ProviderResultReceived, "EVENT", "Integration Gateway Service", "IMMUTABLE_REPLAY_SAFE"),
        new("CID-018", IntegrationGatewayContractNames.ProviderOperationFailed, "EVENT", "Integration Gateway Service", "IMMUTABLE_REPLAY_SAFE"),
    ]);
}
