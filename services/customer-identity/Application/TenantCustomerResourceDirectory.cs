namespace Monergy.Services.CustomerIdentity.Application;

public sealed record TenantCustomerResource(string ResourceId, string DisplayName, string? SecondaryLabel);

public sealed record CustomerWorkspaceContext(
    string TenantId,
    string CustomerId,
    string DisplayName,
    string? SecondaryLabel,
    string OwnerActorId,
    long Version,
    bool Active)
{
    public bool WorkspaceReady { get; init; } = true;
}

public interface ITenantCustomerResourceDirectory
{
    Task<IReadOnlyList<TenantCustomerResource>> ListAsync(string tenantId, CancellationToken cancellationToken);
}
