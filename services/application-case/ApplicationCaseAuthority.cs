using Monergy.Platform;
using Monergy.Services.ApplicationCase.Application;

namespace Monergy.Services.ApplicationCase;

public sealed class ApplicationCaseAuthority(CustomerOwnerClient owners, TenantAccessClient access)
{
    public async Task<AdvisorEngagementContext> RequireAdvisorEngagementAsync(HttpContext http, string customerId)
    {
        if (!TenantAccessClient.Identifier(customerId, 128))
            throw new ApplicationCaseException("CUSTOMER_CONTEXT_INVALID", 400);
        var session = await owners.SessionAsync(http).ConfigureAwait(false);
        var advisor = await access.RequireAdvisorAsync(http).ConfigureAwait(false);
        if (advisor.TenantId != session.TenantId || advisor.ActorId != session.ActorId ||
            advisor.SubjectVersion != session.SubjectVersion)
            throw new ApplicationCaseException("ADVISOR_CASE_ACCESS_REQUIRED", 403);

        async Task<CustomerAccessDecision> RequireAsync(string capability)
        {
            var decision = await access.EvaluateCustomerAsync(http, capability, customerId).ConfigureAwait(false);
            if (decision is not { Outcome: "Allow", AccessBasis: "Advisor", ConsentVersion: > 0 } ||
                decision.PolicyVersion != advisor.PolicyVersion || decision.SubjectVersion != advisor.SubjectVersion)
                throw new ApplicationCaseException("ADVISOR_CUSTOMER_ACCESS_REQUIRED", 403);
            return decision;
        }

        var profile = await RequireAsync("financial-profile.profile.read").ConfigureAwait(false);
        var evidence = await RequireAsync("evidence.document.read").ConfigureAwait(false);
        var engagement = await RequireAsync(AdvisorEngagementPolicy.Capability).ConfigureAwait(false);
        if (profile.CustomerVersion != evidence.CustomerVersion || profile.CustomerVersion != engagement.CustomerVersion ||
            profile.ConsentVersion != evidence.ConsentVersion || profile.ConsentVersion != engagement.ConsentVersion)
            throw new ApplicationCaseException("ADVISOR_AUTHORITY_CHANGED", 409);
        var customer = await owners.CustomerAsync(session.TenantId, customerId, http.RequestAborted).ConfigureAwait(false);
        if (customer.OwnerActorId == session.ActorId || customer.Version != profile.CustomerVersion)
            throw new ApplicationCaseException("ADVISOR_CUSTOMER_ACCESS_REQUIRED", 403);
        return new(session.TenantId, session.ActorId, customerId, profile.PolicyVersion,
            profile.SubjectVersion, profile.CustomerVersion, profile.ConsentVersion);
    }

    public async Task<CaseActorContext> RequireCustomerAsync(HttpContext http)
    {
        var session = await owners.SessionAsync(http).ConfigureAwait(false);
        var customer = await owners.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted)
            .ConfigureAwait(false) ?? throw new ApplicationCaseException("CUSTOMER_CASE_ACCESS_REQUIRED", 403);
        var decision = await access.EvaluateAsync(http,
            new(AssistanceCaseApplication.CustomerCapability, null, null)).ConfigureAwait(false);
        if (decision.Outcome != "Allow" || decision.ActorId != session.ActorId || decision.SubjectVersion != session.SubjectVersion)
            throw new ApplicationCaseException("CUSTOMER_CASE_ACCESS_REQUIRED", 403);
        return new(session.TenantId, session.ActorId, customer.CustomerId,
            decision.PolicyVersion, decision.SubjectVersion, decision.CapabilityId);
    }

    public async Task<CaseActorContext> RequireDsaAsync(HttpContext http)
    {
        var session = await owners.SessionAsync(http).ConfigureAwait(false);
        var decision = await access.EvaluateAsync(http,
            new(AssistanceCaseApplication.DsaCapability, null, null)).ConfigureAwait(false);
        if (decision.Outcome != "Allow" || decision.ActorId != session.ActorId || decision.SubjectVersion != session.SubjectVersion)
            throw new ApplicationCaseException("DSA_CASE_ACCESS_REQUIRED", 403);
        return new(session.TenantId, session.ActorId, null,
            decision.PolicyVersion, decision.SubjectVersion, decision.CapabilityId);
    }
}
