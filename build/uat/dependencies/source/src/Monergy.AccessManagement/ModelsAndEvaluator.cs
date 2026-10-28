using System.Collections.ObjectModel;
using Microsoft.Extensions.Caching.Memory;

namespace Monergy.AccessManagement;

public enum MembershipStatus { PendingIdentity, Active, Disabled }
public enum AdministrativeAuthority { PlatformSuperuser, PlatformAdmin, TenantAdmin }
public enum CapabilityLifecycle { Active, Deprecated, Retired }
public enum CapabilityRisk { OrdinaryRead, SensitiveRead, Mutation, Administrative }
public enum AuditRequirement { None, Denials, AllDecisions }
public enum AuthorizationScope { Tenant, Resource }
public enum AuthorizationOutcome { Allow, Deny }
public enum AuthorizationReasonCode
{
    Granted, CapabilityUnknown, CapabilityRetired, SubjectNotFound, SubjectDisabled,
    SubjectPendingIdentity, NoBusinessRole, CapabilityNotGranted, ResourceContextRequired,
    ResourceTypeMismatch, ResourceScopeNotGranted, PolicyUnavailable,
    PolicyVersionMismatch, DependencyUnavailable, DecisionStateChanged
}

public sealed record CapabilityDefinition(
    string CapabilityId, string Service, string? ResourceType, string Action, string DisplayName,
    CapabilityLifecycle Lifecycle, CapabilityRisk Risk, AuditRequirement AuditRequirement);

public sealed record AuthorizationEvaluationRequest(
    string CapabilityId, string? ResourceType = null, string? ResourceId = null);

public sealed record AuthorizationDecision(
    string DecisionId, AuthorizationOutcome Outcome, string TenantId, string ActorId,
    string CapabilityId, string? ResourceType, string? ResourceId, AuthorizationReasonCode ReasonCode,
    long PolicyVersion, long SubjectVersion, string CapabilityCatalogVersion,
    DateTimeOffset EvaluatedAt, DateTimeOffset? ExpiresAt, string CacheMode, string CorrelationId);

public sealed record TrustedActorContext(
    string TenantId, string ActorId, string AuthenticationContextId, string WorkloadIdentityId, string CorrelationId);

public sealed record AccessSubject(
    string ActorId, string NormalizedEmail, MembershipStatus Status, string? BusinessRoleId,
    AdministrativeAuthority? AdministrativeAuthority, long SubjectVersion);

public sealed record SubjectPolicyState(AccessSubject Subject, long PolicyVersion);
public sealed record CapabilityGrant(string CapabilityId, AuthorizationScope Scope);
public sealed record RolePolicySnapshot(string RoleId, long PolicyVersion, IReadOnlyList<CapabilityGrant> Grants);

public sealed class CapabilityCatalog
{
    public const string CurrentVersion = "2026-10-08.1";
    private readonly IReadOnlyDictionary<string, CapabilityDefinition> _byId;

    public CapabilityCatalog()
    {
        var values = new[]
        {
            New("financial-profile.profile.read", "financial-profile", "customer", "read", "Read financial profile", CapabilityRisk.SensitiveRead, AuditRequirement.Denials),
            New("financial-profile.profile.update", "financial-profile", "customer", "update", "Update financial profile", CapabilityRisk.Mutation, AuditRequirement.AllDecisions),
            New("evidence.document.read", "evidence", "document", "read", "Read evidence document", CapabilityRisk.SensitiveRead, AuditRequirement.Denials),
            New("evidence.document.upload", "evidence", "document", "upload", "Upload evidence document", CapabilityRisk.Mutation, AuditRequirement.AllDecisions),
            New("reporting.report.read", "reporting", "report", "read", "Read report", CapabilityRisk.SensitiveRead, AuditRequirement.Denials),
            New("reporting.report.generate", "reporting", "report", "generate", "Generate report", CapabilityRisk.Mutation, AuditRequirement.AllDecisions),
            New("search.query.execute", "search", null, "execute", "Execute search query", CapabilityRisk.OrdinaryRead, AuditRequirement.None),
            New("customer-identity.identity.read", "customer-identity", null, "read", "Read tenant identity", CapabilityRisk.SensitiveRead, AuditRequirement.Denials),
            New("consent.consent.read", "consent", "customer", "read", "Read consent", CapabilityRisk.SensitiveRead, AuditRequirement.Denials),
            New("document-intelligence.document.process", "document-intelligence", "document", "process", "Process document", CapabilityRisk.Mutation, AuditRequirement.AllDecisions),
            New("financial-rules.calculation.execute", "financial-rules", "customer", "execute", "Execute calculation", CapabilityRisk.Mutation, AuditRequirement.AllDecisions),
            New("integration-gateway.connection.manage", "integration-gateway", "customer", "manage", "Manage provider operation", CapabilityRisk.Mutation, AuditRequirement.AllDecisions),
            New("job-management.job.read", "job-management", null, "read", "Read tenant jobs", CapabilityRisk.OrdinaryRead, AuditRequirement.Denials),
            New("ai-intelligence.analysis.execute", "ai-intelligence", "customer", "execute", "Execute analysis", CapabilityRisk.Mutation, AuditRequirement.AllDecisions),
            New("audit.evidence.read", "audit", null, "read", "Read tenant audit evidence", CapabilityRisk.SensitiveRead, AuditRequirement.AllDecisions)
        };
        _byId = new ReadOnlyDictionary<string, CapabilityDefinition>(
            values.ToDictionary(value => value.CapabilityId, StringComparer.Ordinal));
    }

    public IReadOnlyCollection<CapabilityDefinition> All => _byId.Values.ToArray();
    public bool TryGet(string id, out CapabilityDefinition? definition) => _byId.TryGetValue(id, out definition);

    private static CapabilityDefinition New(
        string id, string service, string? resource, string action, string name,
        CapabilityRisk risk, AuditRequirement audit) =>
        new(id, service, resource, action, name, CapabilityLifecycle.Active, risk, audit);
}

public interface IAccessPolicyStore
{
    bool IsReady { get; }
    ValueTask<SubjectPolicyState?> GetSubjectPolicyStateAsync(string tenantId, string actorId, CancellationToken ct);
    ValueTask<RolePolicySnapshot?> GetRolePolicyAsync(string tenantId, string roleId, long policyVersion, CancellationToken ct);
    ValueTask<IReadOnlyList<CapabilityGrant>> GetSubjectCapabilityGrantsAsync(
        string tenantId, string actorId, long policyVersion, long subjectVersion, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<CapabilityGrant>>([]);
    ValueTask<bool> HasResourceGrantAsync(string tenantId, string actorId, string capabilityId, string resourceType, string resourceId, CancellationToken ct);
    ValueTask<bool> IsDecisionStateCurrentAsync(string tenantId, string actorId, string roleId, long policyVersion, long subjectVersion, CancellationToken ct);
}

public sealed class AuthorizationEvaluator(
    IAccessPolicyStore store, CapabilityCatalog catalog, IMemoryCache cache, TimeProvider clock)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    public async ValueTask<AuthorizationDecision> EvaluateAsync(
        TrustedActorContext context, AuthorizationEvaluationRequest request, CancellationToken ct)
    {
        try
        {
            return await EvaluateCoreAsync(context, request, ct).ConfigureAwait(false);
        }
        catch (AccessStoreUnavailableException)
        {
            return new(Guid.NewGuid().ToString("N"), AuthorizationOutcome.Deny, context.TenantId, context.ActorId,
                request.CapabilityId, request.ResourceType, request.ResourceId, AuthorizationReasonCode.DependencyUnavailable,
                0, 0, CapabilityCatalog.CurrentVersion, clock.GetUtcNow(), null,
                "NO_EXTERNAL_ALLOW_CACHE", context.CorrelationId);
        }
    }

    private async ValueTask<AuthorizationDecision> EvaluateCoreAsync(
        TrustedActorContext context, AuthorizationEvaluationRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!store.IsReady)
            return Deny(AuthorizationReasonCode.DependencyUnavailable, 0, 0);

        if (!catalog.TryGet(request.CapabilityId, out var capability) || capability is null)
            return Deny(AuthorizationReasonCode.CapabilityUnknown, 0, 0);
        if (capability.Lifecycle == CapabilityLifecycle.Retired)
            return Deny(AuthorizationReasonCode.CapabilityRetired, 0, 0);

        var current = await store.GetSubjectPolicyStateAsync(context.TenantId, context.ActorId, ct).ConfigureAwait(false);
        if (current is null)
            return Deny(AuthorizationReasonCode.SubjectNotFound, 0, 0);
        var subject = current.Subject;
        if (subject.Status == MembershipStatus.Disabled)
            return Deny(AuthorizationReasonCode.SubjectDisabled, current.PolicyVersion, subject.SubjectVersion);
        if (subject.Status == MembershipStatus.PendingIdentity)
            return Deny(AuthorizationReasonCode.SubjectPendingIdentity, current.PolicyVersion, subject.SubjectVersion);
        if (string.IsNullOrWhiteSpace(subject.BusinessRoleId))
            return Deny(AuthorizationReasonCode.NoBusinessRole, current.PolicyVersion, subject.SubjectVersion);

        if (capability.ResourceType is null)
        {
            if (request.ResourceType is not null || request.ResourceId is not null)
                return Deny(AuthorizationReasonCode.ResourceTypeMismatch, current.PolicyVersion, subject.SubjectVersion);
        }
        else
        {
            if (!string.Equals(request.ResourceType, capability.ResourceType, StringComparison.Ordinal))
                return Deny(
                    request.ResourceType is null ? AuthorizationReasonCode.ResourceContextRequired : AuthorizationReasonCode.ResourceTypeMismatch,
                    current.PolicyVersion, subject.SubjectVersion);
            if (string.IsNullOrWhiteSpace(request.ResourceId))
                return Deny(AuthorizationReasonCode.ResourceContextRequired, current.PolicyVersion, subject.SubjectVersion);
        }

        var cacheKey = new RolePolicyCacheKey(context.TenantId, current.PolicyVersion, subject.BusinessRoleId, CapabilityCatalog.CurrentVersion);
        var policy = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheLifetime;
            entry.Size = 1;
            return await store.GetRolePolicyAsync(context.TenantId, subject.BusinessRoleId, current.PolicyVersion, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (policy is null)
            return Deny(AuthorizationReasonCode.PolicyUnavailable, current.PolicyVersion, subject.SubjectVersion);
        if (policy.PolicyVersion != current.PolicyVersion || policy.RoleId != subject.BusinessRoleId)
            return Deny(AuthorizationReasonCode.PolicyVersionMismatch, current.PolicyVersion, subject.SubjectVersion);

        var subjectGrants = await store.GetSubjectCapabilityGrantsAsync(context.TenantId, context.ActorId,
            current.PolicyVersion, subject.SubjectVersion, ct).ConfigureAwait(false);
        var grants = policy.Grants.Concat(subjectGrants).Where(grant =>
            string.Equals(grant.CapabilityId, capability.CapabilityId, StringComparison.Ordinal)).ToArray();
        if (grants.Length == 0)
            return Deny(AuthorizationReasonCode.CapabilityNotGranted, current.PolicyVersion, subject.SubjectVersion);
        if (grants.Any(grant => grant.Scope == AuthorizationScope.Tenant))
            return await ConfirmAllowAsync().ConfigureAwait(false);

        var hasResourceGrant = capability.ResourceType is not null && request.ResourceId is not null &&
            grants.Any(grant => grant.Scope == AuthorizationScope.Resource) &&
            await store.HasResourceGrantAsync(
                context.TenantId, context.ActorId, capability.CapabilityId, capability.ResourceType, request.ResourceId, ct)
                .ConfigureAwait(false);
        return hasResourceGrant
            ? await ConfirmAllowAsync().ConfigureAwait(false)
            : Deny(AuthorizationReasonCode.ResourceScopeNotGranted, current.PolicyVersion, subject.SubjectVersion);

        async ValueTask<AuthorizationDecision> ConfirmAllowAsync()
        {
            var unchanged = await store.IsDecisionStateCurrentAsync(context.TenantId, context.ActorId,
                subject.BusinessRoleId, current.PolicyVersion, subject.SubjectVersion, ct).ConfigureAwait(false);
            return unchanged
                ? Create(AuthorizationOutcome.Allow, AuthorizationReasonCode.Granted, current.PolicyVersion, subject.SubjectVersion)
                : Deny(AuthorizationReasonCode.DecisionStateChanged, current.PolicyVersion, subject.SubjectVersion);
        }

        AuthorizationDecision Deny(AuthorizationReasonCode reason, long policyVersion, long subjectVersion) =>
            Create(AuthorizationOutcome.Deny, reason, policyVersion, subjectVersion);

        AuthorizationDecision Create(AuthorizationOutcome outcome, AuthorizationReasonCode reason, long policyVersion, long subjectVersion) =>
            new(Guid.NewGuid().ToString("N"), outcome, context.TenantId, context.ActorId,
                request.CapabilityId, request.ResourceType, request.ResourceId, reason,
                policyVersion, subjectVersion, CapabilityCatalog.CurrentVersion, now, null,
                "VERSIONED_SERVER_POLICY_SNAPSHOT;CURRENT_STATE_CONFIRMED_BEFORE_ALLOW", context.CorrelationId);
    }
}

internal sealed record RolePolicyCacheKey(
    string TenantId, long PolicyVersion, string RoleId, string CapabilityCatalogVersion);
