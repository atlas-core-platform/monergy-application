using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace Monergy.AccessManagement;

public sealed record AccessRequestPermissionDefinition(
    string PermissionId, string PermissionCode, string CapabilityId, AuthorizationScope Scope);

public sealed record AccessRequestTargetDefinition(
    string Code,
    string Label,
    string GrantExplanation,
    IReadOnlyList<string> EligibleRoleCodes,
    IReadOnlyList<AccessRequestPermissionDefinition> Permissions,
    string DefinitionHash,
    bool TenantGrantable,
    string OwnerNotes);

/// <summary>
/// The only source of requestable access targets. Browser payloads name one target; they never
/// supply permissions, actors or tenants. Changes to a definition deliberately change its hash
/// so that an old request cannot approve a different grant after deployment.
/// </summary>
public sealed class AccessRequestTargetCatalog
{
    public const string AdvisorTarget = "financial-advisor.assigned-customer-financial-review";
    private readonly IReadOnlyDictionary<string, AccessRequestTargetDefinition> byCode;

    public AccessRequestTargetCatalog(CapabilityCatalog capabilities)
    {
        var values = new[]
        {
            Target("customer.financial-information", "Financial information",
                "Allows this customer to view and maintain their financial information.", ["customer"],
                "Customer-owned financial information only; customer consent remains a separate owner.",
                Permission("default-permission-profile-read", "financial-profile-read", "financial-profile.profile.read", AuthorizationScope.Resource),
                Permission("default-permission-profile-update", "financial-profile-update", "financial-profile.profile.update", AuthorizationScope.Resource)),
            Target("customer.documents", "Documents",
                "Allows this customer to view, upload and process their documents.", ["customer"],
                "Does not grant consent or another customer's documents.",
                Permission("default-permission-evidence-read", "evidence-read", "evidence.document.read", AuthorizationScope.Resource),
                Permission("default-permission-evidence-upload", "evidence-upload", "evidence.document.upload", AuthorizationScope.Tenant),
                Permission("default-permission-document-processing", "document-processing", "document-intelligence.document.process", AuthorizationScope.Resource)),
            Target("customer.processing-status", "Processing status",
                "Allows this customer to view processing status for their tenant activity.", ["customer"],
                "Status visibility only; it does not grant access to document contents.",
                Permission("default-permission-job-read", "processing-status-read", "job-management.job.read", AuthorizationScope.Tenant)),
            Target("customer.calculations", "Financial calculations",
                "Allows this customer to use calculations based on their financial information.", ["customer"],
                "Includes the financial-information read dependency used by the calculation experience.",
                Permission("default-permission-calculation-execute", "customer-calculations", "financial-rules.calculation.execute", AuthorizationScope.Resource),
                Permission("default-permission-profile-read", "financial-profile-read", "financial-profile.profile.read", AuthorizationScope.Resource)),
            Target("customer.search", "Search",
                "Allows this customer to search information they are already authorized to use.", ["customer"],
                "Search never expands underlying record access.",
                Permission("default-permission-search", "authorized-search", "search.query.execute", AuthorizationScope.Tenant)),
            Target("customer.financial-qa", "Ask Monergy",
                "Allows this customer to ask source-grounded questions about their authorized financial information and documents.", ["customer"],
                "AI cannot approve authorization; existing record and consent checks still apply.",
                Permission("default-permission-ai-execute", "customer-ai-analysis", "ai-intelligence.analysis.execute", AuthorizationScope.Resource),
                Permission("default-permission-search", "authorized-search", "search.query.execute", AuthorizationScope.Tenant),
                Permission("default-permission-profile-read", "financial-profile-read", "financial-profile.profile.read", AuthorizationScope.Resource),
                Permission("default-permission-evidence-read", "evidence-read", "evidence.document.read", AuthorizationScope.Resource)),
            Target("customer.reports", "Reports",
                "Allows this customer to view and generate reports from information they are authorized to use.", ["customer"],
                "Report access does not grant access to another customer's source records.",
                Permission("default-permission-report-read", "report-read", "reporting.report.read", AuthorizationScope.Resource),
                Permission("default-permission-report-generate", "report-generate", "reporting.report.generate", AuthorizationScope.Tenant)),
            Target("customer.data-connections", "Data connections",
                "Allows this customer to connect and manage their financial-data providers.", ["customer"],
                "Provider credentials remain owned by the integration boundary and are never stored in access requests.",
                Permission("default-permission-connections-manage", "customer-connections", "integration-gateway.connection.manage", AuthorizationScope.Resource)),
            Target(AdvisorTarget, "Assigned customer financial review",
                "Allows this Financial Advisor to read financial information only for customers separately assigned to them, with the required record access and current customer consent.", ["financial-advisor"],
                "Customer assignment and consent remain with their existing owners.",
                Permission("default-permission-profile-read", "financial-profile-read", "financial-profile.profile.read", AuthorizationScope.Resource),
                Permission("default-permission-evidence-read", "evidence-read", "evidence.document.read", AuthorizationScope.Resource))
        };

        foreach (var target in values)
        {
            if (!target.TenantGrantable || target.EligibleRoleCodes.Count == 0 || target.Permissions.Count == 0)
                throw new InvalidOperationException("INVALID_ACCESS_REQUEST_TARGET:" + target.Code);
            foreach (var permission in target.Permissions)
                if (!capabilities.TryGet(permission.CapabilityId, out var capability) ||
                    capability is not { Lifecycle: CapabilityLifecycle.Active })
                    throw new InvalidOperationException("UNKNOWN_ACCESS_REQUEST_CAPABILITY:" + permission.CapabilityId);
        }

        byCode = new ReadOnlyDictionary<string, AccessRequestTargetDefinition>(
            values.ToDictionary(value => value.Code, StringComparer.Ordinal));
    }

    public IReadOnlyCollection<AccessRequestTargetDefinition> All => byCode.Values.ToArray();
    public bool TryGet(string code, out AccessRequestTargetDefinition? definition) =>
        byCode.TryGetValue(code, out definition);

    private static AccessRequestPermissionDefinition Permission(
        string id, string code, string capability, AuthorizationScope scope) => new(id, code, capability, scope);

    private static AccessRequestTargetDefinition Target(
        string code, string label, string explanation, IReadOnlyList<string> roles, string ownerNotes,
        params AccessRequestPermissionDefinition[] permissions)
    {
        var canonical = string.Join('|', new[]
        {
            code,
            label,
            explanation,
            string.Join(',', roles.Order(StringComparer.Ordinal)),
            string.Join(',', permissions.OrderBy(value => value.PermissionId, StringComparer.Ordinal)
                .Select(value => $"{value.PermissionId}:{value.PermissionCode}:{value.CapabilityId}:{value.Scope}")),
            "tenant-grantable=true",
            ownerNotes
        });
        return new(code, label, explanation, roles, permissions,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(),
            true, ownerNotes);
    }
}
