using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Reporting.Application;

namespace Monergy.Services.Reporting.Infrastructure;

public sealed class TenantReportingAuthorizationPolicy(IHttpContextAccessor accessor) : IReportingAuthorizationPolicy
{
    public Task<ContractError?> AuthorizeAsync(TrustedSecurityContext context, string customerId, string operation,
        string correlationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var proof = accessor.HttpContext?.Features.Get<TenantBoundaryProof>();
        var allowed = proof is not null && proof.ActorId == context.Actor.ActorId &&
            proof.SessionId == context.Actor.AuthenticationContextId && proof.CustomerId == customerId &&
            context.Access.CustomerId == customerId && proof.ContractName == operation;
        return Task.FromResult(allowed ? null : new ContractError("tenant.authorization.denied", ContractErrorCategory.AccessDenied,
            "Current tenant session and owner authorization are required.", false, correlationId));
    }
}
