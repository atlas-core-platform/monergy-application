using Monergy.Services.CustomerIdentity.Application;
using Monergy.Platform;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

// Operator-owned LOCAL/CI fixtures, resolved inside C&I. These labels neither
// establish customer ownership nor grant access. Production needs its own owner adapter.
public sealed class ReferenceTenantCustomerResourceDirectory : ITenantCustomerResourceDirectory
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TenantCustomerResource>> resources;

    public ReferenceTenantCustomerResourceDirectory(IConfiguration configuration)
    {
        TenantAccessIntegration.EnsureAllowed(configuration);
        var bindings = configuration.GetSection("Monergy:TenantBoundary:CustomerTenants");
        var entries = configuration.GetSection("Monergy:AccessIntegration:CustomerResources").GetChildren().ToArray();
        if (entries.Length > 1000) throw new InvalidOperationException("CUSTOMER_DIRECTORY_TOO_LARGE");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<(string Tenant, TenantCustomerResource Resource)>();
        foreach (var entry in entries)
        {
            var id = entry["ResourceId"] ?? "";
            var tenant = entry["TenantId"] ?? "";
            var label = entry["DisplayName"] ?? "";
            var secondary = entry["SecondaryLabel"];
            if (!TenantAccessClient.Identifier(id, 128) || !TenantAccessClient.Identifier(tenant, 64) ||
                bindings[id] != tenant || !seen.Add(id) || !ValidLabel(label) ||
                secondary is not null && !ValidLabel(secondary))
                throw new InvalidOperationException("CUSTOMER_DIRECTORY_BINDING_INVALID");
            items.Add((tenant, new(id, label.Trim(), secondary?.Trim())));
        }
        resources = items.GroupBy(item => item.Tenant, StringComparer.Ordinal).ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<TenantCustomerResource>)Array.AsReadOnly(group.Select(item => item.Resource)
                .OrderBy(item => item.DisplayName, StringComparer.Ordinal).ThenBy(item => item.ResourceId, StringComparer.Ordinal).ToArray()),
            StringComparer.Ordinal);
    }

    public IReadOnlyList<TenantCustomerResource> List(string tenantId) =>
        resources.GetValueOrDefault(tenantId) ?? [];

    private static bool ValidLabel(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 120 && !value.Any(char.IsControl);
}
