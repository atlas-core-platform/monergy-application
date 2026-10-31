using Monergy.Platform;
using Monergy.Services.ApplicationCase.Application;

namespace Monergy.Services.ApplicationCase;

public sealed class ApplicationCaseAuthority(CustomerOwnerClient owners, TenantAccessClient access)
{
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
