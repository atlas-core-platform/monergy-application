using Monergy.Services.CustomerIdentity.Application;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

// Development-only bounded directory used to prove the human-resource picker.
// Production must resolve tenant-owned Customers from the authoritative C&I
// customer boundary; Access Management never stores these labels.
public sealed class ReferenceTenantCustomerResourceDirectory(IConfiguration configuration)
    : ITenantCustomerResourceDirectory
{
    public IReadOnlyList<TenantCustomerResource> List(string tenantId)
    {
        Monergy.Platform.ReferenceAdapterGuard.EnsureAllowed(configuration);
        return tenantId switch
        {
            "T001" =>
            [
                new("customer-a", "Reference Customer A", "customer-a")
            ],
            "T002" =>
            [
                new("customer-b", "Reference Customer B", "customer-b")
            ],
            _ => []
        };
    }
}
