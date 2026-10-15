namespace Monergy.AccessManagement;

public interface ITrustedSecurityContextResolver
{
    bool IsReady { get; }
    ValueTask<(TrustedActorContext? Context, string? Failure, bool Available)> ResolveAsync(HttpContext httpContext);
}

public sealed class ReferenceTrustedSecurityContextResolver(IWebHostEnvironment environment) : ITrustedSecurityContextResolver
{
    public bool IsReady => environment.IsDevelopment();

    public ValueTask<(TrustedActorContext? Context, string? Failure, bool Available)> ResolveAsync(HttpContext httpContext) =>
        ValueTask.FromResult(Resolve(httpContext));

    private (TrustedActorContext? Context, string? Failure, bool Available) Resolve(HttpContext httpContext)
    {
        if (!environment.IsDevelopment())
            return (null, "TRUSTED_CONTEXT_UNAVAILABLE", false);

        if (!Read("X-Monergy-Reference-Tenant", 64, out var tenant) ||
            !Read("X-Monergy-Reference-Actor", 128, out var actor) ||
            !Read("X-Monergy-Reference-Authentication-Context", 160, out var auth) ||
            !Read("X-Monergy-Reference-Workload", 160, out var workload))
            return (null, "REFERENCE_CONTEXT_REQUIRED", true);

        if (!Safe(tenant, false) || !Safe(actor, true) || !Safe(auth, true) || !Safe(workload, true))
            return (null, "REFERENCE_CONTEXT_INVALID", true);

        return (new TrustedActorContext(tenant, actor, auth, workload, httpContext.TraceIdentifier), null, true);

        bool Read(string name, int max, out string value)
        {
            value = string.Empty;
            if (!httpContext.Request.Headers.TryGetValue(name, out var values) ||
                values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
                return false;
            value = values[0]!.Trim();
            return value.Length > 0 && value.Length <= max;
        }
    }

    private static bool Safe(string value, bool dots) =>
        value.Length > 0 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' || (dots && character == '.'));
}

public sealed class RejectingTrustedSecurityContextResolver : ITrustedSecurityContextResolver
{
    public bool IsReady => false;
    public ValueTask<(TrustedActorContext? Context, string? Failure, bool Available)> ResolveAsync(HttpContext httpContext) =>
        new((null, "TRUSTED_CONTEXT_UNAVAILABLE", false));
}

public sealed class ReferenceAccessPolicyStore : IAccessPolicyStore
{
    public async ValueTask<bool> IsDecisionStateCurrentAsync(
        string tenantId, string actorId, string roleId, long policyVersion, long subjectVersion, CancellationToken ct)
    {
        var state = await GetSubjectPolicyStateAsync(tenantId, actorId, ct).ConfigureAwait(false);
        return state is not null && state.Subject.Status == MembershipStatus.Active &&
            state.Subject.BusinessRoleId == roleId && state.PolicyVersion == policyVersion &&
            state.Subject.SubjectVersion == subjectVersion;
    }

    private const long Revision = 7;
    private static readonly Dictionary<string, AccessSubject> Subjects =
        new Dictionary<string, AccessSubject>(StringComparer.Ordinal)
        {
            ["A100"] = new("A100", "analyst@example.test", MembershipStatus.Active, "role-analyst", null, 3),
            ["A200"] = new("A200", "disabled@example.test", MembershipStatus.Disabled, "role-analyst", null, 4),
            ["A300"] = new("A300", "no-role@example.test", MembershipStatus.Active, null, null, 1),
            ["A900"] = new("A900", "tenant-admin@example.test", MembershipStatus.Active, null, AdministrativeAuthority.TenantAdmin, 1)
        };

    private static readonly RolePolicySnapshot AnalystPolicy = new(
        "role-analyst", Revision,
        [
            new CapabilityGrant("financial-profile.profile.read", AuthorizationScope.Resource),
            new CapabilityGrant("search.query.execute", AuthorizationScope.Tenant)
        ]);

    public bool IsReady => true;

    public ValueTask<SubjectPolicyState?> GetSubjectPolicyStateAsync(string tenantId, string actorId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SubjectPolicyState? state = tenantId == "T001" && Subjects.TryGetValue(actorId, out var subject)
            ? new SubjectPolicyState(subject, Revision)
            : null;
        return ValueTask.FromResult(state);
    }

    public ValueTask<RolePolicySnapshot?> GetRolePolicyAsync(string tenantId, string roleId, long policyVersion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RolePolicySnapshot? policy = tenantId == "T001" && roleId == AnalystPolicy.RoleId && policyVersion == Revision
            ? AnalystPolicy
            : null;
        return ValueTask.FromResult(policy);
    }

    public ValueTask<bool> HasResourceGrantAsync(
        string tenantId, string actorId, string capabilityId, string resourceType, string resourceId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var allowed = tenantId == "T001" && actorId == "A100" &&
            capabilityId == "financial-profile.profile.read" && resourceType == "customer" &&
            resourceId == "customer-100";
        return ValueTask.FromResult(allowed);
    }
}

public sealed class UnavailableAccessPolicyStore : IAccessPolicyStore
{
    public ValueTask<bool> IsDecisionStateCurrentAsync(
        string tenantId, string actorId, string roleId, long policyVersion, long subjectVersion, CancellationToken ct) =>
        ValueTask.FromResult(false);

    public bool IsReady => false;
    public ValueTask<SubjectPolicyState?> GetSubjectPolicyStateAsync(string tenantId, string actorId, CancellationToken ct) =>
        ValueTask.FromResult<SubjectPolicyState?>(null);
    public ValueTask<RolePolicySnapshot?> GetRolePolicyAsync(string tenantId, string roleId, long policyVersion, CancellationToken ct) =>
        ValueTask.FromResult<RolePolicySnapshot?>(null);
    public ValueTask<bool> HasResourceGrantAsync(string tenantId, string actorId, string capabilityId, string resourceType, string resourceId, CancellationToken ct) =>
        ValueTask.FromResult(false);
}
