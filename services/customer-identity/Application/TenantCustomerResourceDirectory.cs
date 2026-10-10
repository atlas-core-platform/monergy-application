namespace Monergy.Services.CustomerIdentity.Application;

public sealed record TenantCustomerResource(string ResourceId, string DisplayName, string? SecondaryLabel);

public interface ITenantCustomerResourceDirectory
{
    Task<IReadOnlyList<TenantCustomerResource>> ListAsync(string tenantId, CancellationToken cancellationToken);
}
