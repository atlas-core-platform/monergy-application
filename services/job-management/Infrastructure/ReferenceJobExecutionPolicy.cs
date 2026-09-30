using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;

namespace Monergy.Services.JobManagement.Infrastructure;

public sealed record ReferenceJobConsent(string ConsentReferenceId, string CustomerId,
    string Purpose, DateTimeOffset ExpiresAt, bool Revoked);

public sealed class ReferenceJobExecutionPolicy : IJobAuthorizationPolicy, IJobConsentDecisionPort
{
    private readonly object sync = new();
    private readonly HashSet<string> authorizationContexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReferenceJobConsent> consents = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;

    public ReferenceJobExecutionPolicy(IConfiguration configuration, TimeProvider clock)
    {
        D10ReferenceTransportGuard.EnsureAllowed(configuration);
        this.clock = clock;
    }

    public int AuthorizationEvaluations { get; private set; }
    public int ConsentEvaluations { get; private set; }

    public void GrantAuthorization(string authorizationContextId)
    {
        lock (sync) authorizationContexts.Add(authorizationContextId);
    }

    public void SetConsent(ReferenceJobConsent consent)
    {
        ArgumentNullException.ThrowIfNull(consent);
        lock (sync) consents[consent.ConsentReferenceId] = consent;
    }

    public Task<ContractError?> AuthorizeAsync(TrustedSecurityContext context, DurableJobRecord job,
        string correlationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            AuthorizationEvaluations++;
            var allowed = context.Access.CustomerId == job.CustomerId &&
                authorizationContexts.Contains(context.Access.AuthorizationContextId);
            return Task.FromResult<ContractError?>(allowed ? null : new("job.authorization.denied",
                ContractErrorCategory.AccessDenied, "Current server-side authorization is required.", false,
                correlationId));
        }
    }

    public Task<ContractError?> EvaluateAsync(TrustedSecurityContext context, DurableJobRecord job,
        string correlationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ConsentEvaluations++;
            var reference = context.Access.ConsentReferenceId;
            ContractErrorCategory? category = null;
            if (string.IsNullOrWhiteSpace(reference) || !consents.TryGetValue(reference, out var consent) ||
                consent.CustomerId != job.CustomerId || consent.Purpose != context.Access.Purpose)
                category = ContractErrorCategory.ConsentRequired;
            else if (consent.Revoked) category = ContractErrorCategory.ConsentRevoked;
            else if (consent.ExpiresAt <= clock.GetUtcNow()) category = ContractErrorCategory.ConsentExpired;
            return Task.FromResult<ContractError?>(category is null ? null : new("job.consent.denied",
                category.Value, "Current purpose-bound consent is required at execution time.", false,
                correlationId));
        }
    }
}
