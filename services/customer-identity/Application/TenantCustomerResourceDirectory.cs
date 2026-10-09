namespace Monergy.Services.CustomerIdentity.Application;

public sealed record TenantCustomerResource(string ResourceId, string DisplayName, string? SecondaryLabel);

public interface ITenantCustomerResourceDirectory
{
    IReadOnlyList<TenantCustomerResource> List(string tenantId);
}
