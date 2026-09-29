using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Application;
using Monergy.Services.FinancialRules.Domain;

namespace Monergy.Services.FinancialRules.Infrastructure;

public sealed class ReferenceRuleRegistry : IRuleRegistry
{
    private readonly Dictionary<(string Id, string Version), IDeterministicRule> definitions = [];
    private readonly object sync = new();

    public ReferenceRuleRegistry(IConfiguration configuration) => ReferenceAdapterGuard.EnsureAllowed(configuration);

    public void Register(IDeterministicRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (sync)
        {
            var key = (rule.Definition.RuleId, rule.Definition.Version);
            if (definitions.TryGetValue(key, out var current) &&
                current.Definition.DefinitionHash != rule.Definition.DefinitionHash)
            {
                throw new InvalidOperationException("Immutable rule identity cannot be replaced.");
            }

            definitions.TryAdd(key, rule);
        }
    }

    public IDeterministicRule? Find(string ruleId, string version)
    {
        lock (sync) { return definitions.GetValueOrDefault((ruleId, version)); }
    }
}

public sealed record ReferenceAccessGrant(
    TrustedSecurityContext Context,
    DateTimeOffset ExpiresAt,
    bool Revoked,
    bool ConsentRequired,
    DateTimeOffset? ConsentExpiresAt,
    bool ConsentRevoked);

// Explicit synthetic grants stand in for already-trusted policy context, never customer roles or policy authoring.
public sealed class ReferenceCalculationAccessPolicy : ICalculationAccessPolicy
{
    private readonly Dictionary<string, ReferenceAccessGrant> grants = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private readonly TimeProvider clock;

    public ReferenceCalculationAccessPolicy(IConfiguration configuration, TimeProvider clock)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        this.clock = clock;
    }

    public void SetGrant(ReferenceAccessGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (sync) { grants[grant.Context.Access.AuthorizationContextId] = grant; }
    }

    public Task<ContractError?> AuthorizeAsync(TrustedSecurityContext context, string customerId, string operation,
        string correlationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ContractErrorCategory? failure = null;
            if (!grants.TryGetValue(context.Access.AuthorizationContextId, out var grant) ||
                grant.Context != context || grant.Context.Access.CustomerId != customerId || grant.Revoked ||
                grant.ExpiresAt <= clock.GetUtcNow() ||
                operation is not (FinancialRulesContractNames.ExecuteCalculation or FinancialRulesContractNames.GetCalculationResult or FinancialRulesContractNames.ExplainCalculation))
            {
                failure = ContractErrorCategory.AccessDenied;
            }
            else if (grant.ConsentRequired)
            {
                failure = string.IsNullOrWhiteSpace(context.Access.ConsentReferenceId) ? ContractErrorCategory.ConsentRequired
                    : grant.ConsentRevoked ? ContractErrorCategory.ConsentRevoked
                    : grant.ConsentExpiresAt is null || grant.ConsentExpiresAt <= clock.GetUtcNow() ? ContractErrorCategory.ConsentExpired
                    : null;
            }

            return Task.FromResult(failure is null ? null : new ContractError(
                "calculation.policy.denied", failure.Value, "Current authorized access is required.", false, correlationId));
        }
    }
}

public sealed class InMemoryCalculationRepository : ICalculationRepository
{
    public string AdapterKind => "IN_MEMORY_REFERENCE";

    private readonly object sync = new();
    private readonly Dictionary<CalculationRequestIdentity, (string Fingerprint, CalculationExecution Execution)> requests = [];
    private readonly Dictionary<string, CalculationExecution> results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DomainEvent<CalculationOutcomePayload>> outbox = new(StringComparer.Ordinal);

    public InMemoryCalculationRepository(IConfiguration configuration) => ReferenceAdapterGuard.EnsureAllowed(configuration);

    // Bounded failure injection for the in-memory reference test harness; no external I/O hook under the lock.
    public bool FailNextCommit { get; set; }

    public CalculationExecution? FindRequest(CalculationRequestIdentity identity, string fingerprint)
    {
        lock (sync)
        {
            if (!requests.TryGetValue(identity, out var prior)) { return null; }
            if (prior.Fingerprint != fingerprint)
            {
                throw new CalculationException("calculation.idempotency.conflict", ContractErrorCategory.Conflict);
            }

            return prior.Execution;
        }
    }

    public CalculationExecution? FindResult(string calculationId, string customerId)
    {
        lock (sync)
        {
            var record = results.GetValueOrDefault(calculationId);
            return record?.CustomerId == customerId ? record : null;
        }
    }

    public CalculationExecution Commit(CalculationRequestIdentity identity, string fingerprint, Func<CalculationExecution> applicationTransition)
    {
        ArgumentNullException.ThrowIfNull(applicationTransition);
        lock (sync)
        {
            var prior = FindRequest(identity, fingerprint);
            if (prior is not null) { return prior; }
            var execution = applicationTransition();
            if (FailNextCommit)
            {
                FailNextCommit = false;
                throw new IOException("Injected reference failure before atomic publication.");
            }

            results.Add(execution.CalculationId, execution);
            requests.Add(identity, (fingerprint, execution));
            outbox.Add(execution.Event.EventId, execution.Event);
            return execution;
        }
    }

    public ImmutableArray<DomainEvent<CalculationOutcomePayload>> PendingEvents()
    {
        lock (sync) { return outbox.Values.OrderBy(item => item.EventId, StringComparer.Ordinal).ToImmutableArray(); }
    }

    public void Acknowledge(string eventId)
    {
        lock (sync) { outbox.Remove(eventId); }
    }
}

public sealed class CalculationOutboxDispatcher(ICalculationRepository repository, ICalculationEventSink sink)
{
    public async Task<int> DispatchAsync(CancellationToken cancellationToken = default)
    {
        var acknowledged = 0;
        foreach (var outcome in repository.PendingEvents())
        {
            // A failed/unknown acknowledgement retains intent. Receiver deduplication handles a later retry.
            await sink.PublishAsync(outcome, cancellationToken);
            repository.Acknowledge(outcome.EventId);
            acknowledged++;
        }

        return acknowledged;
    }
}
