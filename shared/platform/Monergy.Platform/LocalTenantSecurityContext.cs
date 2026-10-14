using Monergy.Contracts;

namespace Monergy.Platform;

public static class LocalTenantSecurityContext
{
    public static string RequireTenant(
        TrustedSecurityContext security,
        string? expectedTenant = null,
        params string[] allowedWorkloads)
    {
        ArgumentNullException.ThrowIfNull(security);
        var identity = security.Workload?.WorkloadIdentityId ?? string.Empty;
        var separator = identity.LastIndexOf(':');
        if (separator < 1 || separator == identity.Length - 1)
            throw new TenantBoundaryException("TRUSTED_TENANT_CONTEXT_REQUIRED", 403);
        var workload = identity[..separator];
        var tenant = identity[(separator + 1)..];
        if (!TenantAccessClient.Identifier(tenant, 64) ||
            allowedWorkloads.Length > 0 && !allowedWorkloads.Contains(workload, StringComparer.Ordinal) ||
            expectedTenant is not null && !string.Equals(tenant, expectedTenant, StringComparison.Ordinal))
            throw new TenantBoundaryException("TRUSTED_TENANT_CONTEXT_INVALID", 403);
        return tenant;
    }
}
