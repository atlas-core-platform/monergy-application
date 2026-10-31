namespace Monergy.AccessManagement;

public sealed record DefaultPermissionTemplate(
    string PermissionId, string Code, string Label, IReadOnlyList<CapabilityGrant> Grants);

public sealed record DefaultRoleTemplate(
    string RoleId, string Code, string Label, IReadOnlyList<string> PermissionIds);

public static class DefaultAccessPack
{
    public const string Version = "2.6";
    public const string Identity = "monergy-default-access-pack/2.6";

    internal static IReadOnlyList<DefaultPermissionTemplate> Version1Permissions { get; } =
    [
        Permission("default-permission-profile-read", "financial-profile-read", "View customer financial profile",
            ("financial-profile.profile.read", AuthorizationScope.Resource)),
        Permission("default-permission-profile-update", "financial-profile-update", "Maintain customer financial profile",
            ("financial-profile.profile.update", AuthorizationScope.Resource)),
        Permission("default-permission-consent-read", "customer-consent-read", "View customer consent",
            ("consent.consent.read", AuthorizationScope.Resource)),
        Permission("default-permission-calculation-execute", "customer-calculations", "Run customer calculations",
            ("financial-rules.calculation.execute", AuthorizationScope.Resource)),
        Permission("default-permission-connections-manage", "customer-connections", "Manage customer data connections",
            ("integration-gateway.connection.manage", AuthorizationScope.Resource)),
        Permission("default-permission-ai-execute", "customer-ai-analysis", "Run customer financial analysis",
            ("ai-intelligence.analysis.execute", AuthorizationScope.Resource)),
        Permission("default-permission-search", "authorized-search", "Search authorized tenant information",
            ("search.query.execute", AuthorizationScope.Tenant)),
        Permission("default-permission-evidence-read", "evidence-read", "View authorized evidence",
            ("evidence.document.read", AuthorizationScope.Resource)),
        Permission("default-permission-evidence-upload", "evidence-upload", "Upload evidence",
            ("evidence.document.upload", AuthorizationScope.Tenant)),
        Permission("default-permission-report-read", "report-read", "View authorized reports",
            ("reporting.report.read", AuthorizationScope.Resource)),
        Permission("default-permission-report-generate", "report-generate", "Generate customer reports",
            ("reporting.report.generate", AuthorizationScope.Tenant)),
        Permission("default-permission-job-read", "processing-status-read", "View processing status",
            ("job-management.job.read", AuthorizationScope.Tenant))
    ];

    internal static IReadOnlyList<DefaultPermissionTemplate> Version2Permissions { get; } =
    [
        Permission("default-permission-profile-read", "financial-profile-read", "View customer financial profile",
            ("financial-profile.profile.read", AuthorizationScope.Resource)),
        Permission("default-permission-profile-update", "financial-profile-update", "Maintain customer financial profile",
            ("financial-profile.profile.update", AuthorizationScope.Resource)),
        Permission("default-permission-consent-read", "customer-consent-read", "View customer consent",
            ("consent.consent.read", AuthorizationScope.Resource)),
        Permission("default-permission-calculation-execute", "customer-calculations", "Run customer calculations",
            ("financial-rules.calculation.execute", AuthorizationScope.Resource)),
        Permission("default-permission-connections-manage", "customer-connections", "Manage customer data connections",
            ("integration-gateway.connection.manage", AuthorizationScope.Resource)),
        Permission("default-permission-ai-execute", "customer-ai-analysis", "Run customer financial analysis",
            ("ai-intelligence.analysis.execute", AuthorizationScope.Resource)),
        Permission("default-permission-search", "authorized-search", "Search authorized tenant information",
            ("search.query.execute", AuthorizationScope.Tenant)),
        Permission("default-permission-evidence-read", "evidence-read", "View authorized documents",
            ("evidence.document.read", AuthorizationScope.Resource)),
        Permission("default-permission-evidence-upload", "evidence-upload", "Upload documents",
            ("evidence.document.upload", AuthorizationScope.Tenant)),
        Permission("default-permission-report-read", "report-read", "View authorized reports",
            ("reporting.report.read", AuthorizationScope.Resource)),
        Permission("default-permission-report-generate", "report-generate", "Generate customer reports",
            ("reporting.report.generate", AuthorizationScope.Tenant)),
        Permission("default-permission-job-read", "processing-status-read", "View processing status",
            ("job-management.job.read", AuthorizationScope.Tenant)),
        Permission("default-permission-document-processing", "document-processing", "Extract document details",
            ("document-intelligence.document.process", AuthorizationScope.Resource))
    ];

    public static IReadOnlyList<DefaultPermissionTemplate> Permissions { get; } =
    [
        .. Version2Permissions,
        Permission("default-permission-case-create", "assistance-case-create", "Create an assistance case",
            ("application-case.case.create", AuthorizationScope.Tenant)),
        Permission("default-permission-case-accept", "assistance-case-accept", "Accept an assistance case",
            ("application-case.case.accept", AuthorizationScope.Tenant))
    ];


    internal static IReadOnlyList<DefaultRoleTemplate> Version1Roles { get; } =
    [
        Role("default-role-customer", "customer", "Customer",
            "default-permission-profile-read",
            "default-permission-profile-update",
            "default-permission-consent-read",
            "default-permission-calculation-execute",
            "default-permission-connections-manage",
            "default-permission-ai-execute",
            "default-permission-search",
            "default-permission-evidence-upload",
            "default-permission-report-generate"),
        Role("default-role-financial-advisor", "financial-advisor", "Financial Advisor",
            "default-permission-profile-read",
            "default-permission-consent-read",
            "default-permission-calculation-execute",
            "default-permission-ai-execute",
            "default-permission-search"),
        Role("default-role-operations-support", "operations-support", "Operations Support",
            "default-permission-job-read")
    ];

    internal static IReadOnlyList<DefaultRoleTemplate> Version2Roles { get; } =
    [
        Role("default-role-customer", "customer", "Customer",
            "default-permission-evidence-read",
            "default-permission-evidence-upload",
            "default-permission-document-processing"),
        Role("default-role-financial-advisor", "financial-advisor", "Financial Advisor / Relationship Manager"),
        Role("default-role-operations-support", "operations-support", "Operations Support"),
        Role("default-role-dsa-case-manager", "dsa-case-manager", "DSA / Case Manager"),
        Role("default-role-lender-professional", "lender-professional", "Lender Professional"),
        Role("default-role-financial-professional", "financial-professional", "Chartered Accountant / Financial Professional"),
        Role("default-role-property-valuer", "property-valuer", "Property Valuer"),
        Role("default-role-legal-advisor", "legal-advisor", "Legal Advisor / Advocate")
    ];

    internal static IReadOnlyList<DefaultRoleTemplate> Version21Roles { get; } =
    [
        Role("default-role-customer", "customer", "Customer",
            "default-permission-profile-read",
            "default-permission-profile-update",
            "default-permission-evidence-read",
            "default-permission-evidence-upload",
            "default-permission-document-processing"),
        Role("default-role-financial-advisor", "financial-advisor", "Financial Advisor / Relationship Manager"),
        Role("default-role-operations-support", "operations-support", "Operations Support"),
        Role("default-role-dsa-case-manager", "dsa-case-manager", "DSA / Case Manager"),
        Role("default-role-lender-professional", "lender-professional", "Lender Professional"),
        Role("default-role-financial-professional", "financial-professional", "Chartered Accountant / Financial Professional"),
        Role("default-role-property-valuer", "property-valuer", "Property Valuer"),
        Role("default-role-legal-advisor", "legal-advisor", "Legal Advisor / Advocate")
    ];

    internal static IReadOnlyList<DefaultRoleTemplate> Version22Roles { get; } =
    [
        Role("default-role-customer", "customer", "Customer",
            "default-permission-profile-read",
            "default-permission-profile-update",
            "default-permission-evidence-read",
            "default-permission-evidence-upload",
            "default-permission-document-processing",
            "default-permission-report-read",
            "default-permission-report-generate"),
        Role("default-role-financial-advisor", "financial-advisor", "Financial Advisor / Relationship Manager"),
        Role("default-role-operations-support", "operations-support", "Operations Support"),
        Role("default-role-dsa-case-manager", "dsa-case-manager", "DSA / Case Manager"),
        Role("default-role-lender-professional", "lender-professional", "Lender Professional"),
        Role("default-role-financial-professional", "financial-professional", "Chartered Accountant / Financial Professional"),
        Role("default-role-property-valuer", "property-valuer", "Property Valuer"),
        Role("default-role-legal-advisor", "legal-advisor", "Legal Advisor / Advocate")
    ];

    internal static IReadOnlyList<DefaultRoleTemplate> Version23Roles { get; } =
    [
        Role("default-role-customer", "customer", "Customer",
            "default-permission-profile-read",
            "default-permission-profile-update",
            "default-permission-evidence-read",
            "default-permission-evidence-upload",
            "default-permission-document-processing",
            "default-permission-report-read",
            "default-permission-report-generate",
            "default-permission-search"),
        Role("default-role-financial-advisor", "financial-advisor", "Financial Advisor / Relationship Manager"),
        Role("default-role-operations-support", "operations-support", "Operations Support"),
        Role("default-role-dsa-case-manager", "dsa-case-manager", "DSA / Case Manager"),
        Role("default-role-lender-professional", "lender-professional", "Lender Professional"),
        Role("default-role-financial-professional", "financial-professional", "Chartered Accountant / Financial Professional"),
        Role("default-role-property-valuer", "property-valuer", "Property Valuer"),
        Role("default-role-legal-advisor", "legal-advisor", "Legal Advisor / Advocate")
    ];

    internal static IReadOnlyList<DefaultRoleTemplate> Version24Roles { get; } =
    [
        Role("default-role-customer", "customer", "Customer",
            "default-permission-profile-read",
            "default-permission-profile-update",
            "default-permission-evidence-read",
            "default-permission-evidence-upload",
            "default-permission-document-processing",
            "default-permission-report-read",
            "default-permission-report-generate",
            "default-permission-search",
            "default-permission-job-read"),
        Role("default-role-financial-advisor", "financial-advisor", "Financial Advisor / Relationship Manager"),
        Role("default-role-operations-support", "operations-support", "Operations Support"),
        Role("default-role-dsa-case-manager", "dsa-case-manager", "DSA / Case Manager"),
        Role("default-role-lender-professional", "lender-professional", "Lender Professional"),
        Role("default-role-financial-professional", "financial-professional", "Chartered Accountant / Financial Professional"),
        Role("default-role-property-valuer", "property-valuer", "Property Valuer"),
        Role("default-role-legal-advisor", "legal-advisor", "Legal Advisor / Advocate")
    ];

    internal static IReadOnlyList<DefaultRoleTemplate> Version25Roles { get; } =
    [
        Role("default-role-customer", "customer", "Customer",
            "default-permission-profile-read",
            "default-permission-profile-update",
            "default-permission-evidence-read",
            "default-permission-evidence-upload",
            "default-permission-document-processing",
            "default-permission-report-read",
            "default-permission-report-generate",
            "default-permission-search",
            "default-permission-job-read",
            "default-permission-connections-manage"),
        Role("default-role-financial-advisor", "financial-advisor", "Financial Advisor / Relationship Manager"),
        Role("default-role-operations-support", "operations-support", "Operations Support"),
        Role("default-role-dsa-case-manager", "dsa-case-manager", "DSA / Case Manager"),
        Role("default-role-lender-professional", "lender-professional", "Lender Professional"),
        Role("default-role-financial-professional", "financial-professional", "Chartered Accountant / Financial Professional"),
        Role("default-role-property-valuer", "property-valuer", "Property Valuer"),
        Role("default-role-legal-advisor", "legal-advisor", "Legal Advisor / Advocate")
    ];

    public static IReadOnlyList<DefaultRoleTemplate> Roles { get; } =
    [
        Role("default-role-customer", "customer", "Customer",
            "default-permission-profile-read",
            "default-permission-profile-update",
            "default-permission-evidence-read",
            "default-permission-evidence-upload",
            "default-permission-document-processing",
            "default-permission-report-read",
            "default-permission-report-generate",
            "default-permission-search",
            "default-permission-job-read",
            "default-permission-connections-manage",
            "default-permission-case-create"),
        Role("default-role-financial-advisor", "financial-advisor", "Financial Advisor / Relationship Manager"),
        Role("default-role-operations-support", "operations-support", "Operations Support"),
        Role("default-role-dsa-case-manager", "dsa-case-manager", "DSA / Case Manager",
            "default-permission-case-accept"),
        Role("default-role-lender-professional", "lender-professional", "Lender Professional"),
        Role("default-role-financial-professional", "financial-professional", "Chartered Accountant / Financial Professional"),
        Role("default-role-property-valuer", "property-valuer", "Property Valuer"),
        Role("default-role-legal-advisor", "legal-advisor", "Legal Advisor / Advocate")
    ];

    public static void Validate(CapabilityCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var permissionIds = Permissions.Select(x => x.PermissionId).ToHashSet(StringComparer.Ordinal);
        if (permissionIds.Count != Permissions.Count ||
            Roles.Select(x => x.RoleId).Distinct(StringComparer.Ordinal).Count() != Roles.Count ||
            Permissions.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Permissions.Count ||
            Roles.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Roles.Count)
            throw new InvalidOperationException("DEFAULT_ACCESS_PACK_DUPLICATE_DEFINITION");

        foreach (var permission in Permissions)
        {
            AccessInput.Identifier(permission.PermissionId);
            _ = AccessInput.Code(permission.Code);
            _ = AccessInput.Label(permission.Label);
            foreach (var grant in permission.Grants)
            {
                if (!catalog.TryGet(grant.CapabilityId, out var capability) ||
                    capability is not { Lifecycle: CapabilityLifecycle.Active } ||
                    grant.Scope == AuthorizationScope.Resource && capability.ResourceType is null)
                    throw new InvalidOperationException("DEFAULT_ACCESS_PACK_CAPABILITY_MISMATCH");
            }
        }

        foreach (var role in Roles)
        {
            AccessInput.Identifier(role.RoleId);
            _ = AccessInput.Code(role.Code);
            _ = AccessInput.Label(role.Label);
            if (role.PermissionIds.Distinct(StringComparer.Ordinal).Count() != role.PermissionIds.Count ||
                role.PermissionIds.Any(id => !permissionIds.Contains(id)))
                throw new InvalidOperationException("DEFAULT_ACCESS_PACK_ROLE_MISMATCH");
        }
    }

    private static DefaultPermissionTemplate Permission(
        string id, string code, string label, params (string CapabilityId, AuthorizationScope Scope)[] grants) =>
        new(id, code, label, grants.Select(x => new CapabilityGrant(x.CapabilityId, x.Scope)).ToArray());

    private static DefaultRoleTemplate Role(
        string id, string code, string label, params string[] permissionIds) =>
        new(id, code, label, permissionIds);
}
