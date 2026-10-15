using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Monergy.AccessManagement;

public sealed record CustomerContext(string TenantId, string CustomerId, string? OwnerActorId, long Version, bool Active);
public sealed record CustomerAccessRequest(string CapabilityId, string CustomerId, string Purpose);
public sealed record CustomerConsentRequest(string TenantId, string ActorId, string CustomerId, string CapabilityId, string Purpose);
public sealed record CustomerConsentDecision(string TenantId, string ActorId, string CustomerId, string CapabilityId,
    string Purpose, bool Allowed, string Reason, long Version, DateTimeOffset EvaluatedAt, DateTimeOffset? ExpiresAt);
public sealed record CustomerAccessDecision(string DecisionId, string Outcome, string TenantId, string ActorId,
    string CustomerId, string CapabilityId, string Purpose, string ReasonCode, string? AccessBasis,
    long PolicyVersion, long SubjectVersion, long CustomerVersion, long ConsentVersion, DateTimeOffset EvaluatedAt);

public interface ICustomerContextClient
{
    Task<CustomerContext?> ReadAsync(string tenantId, string customerId, CancellationToken cancellationToken);
}
public interface ICustomerConsentClient
{
    Task<CustomerConsentDecision> EvaluateAsync(CustomerConsentRequest request, CancellationToken cancellationToken);
}
public interface ICustomerRelationshipStore
{
    ValueTask<bool> IsAssignedAsync(string tenantId, string actorId, string customerId, CancellationToken cancellationToken);
}

public static class CustomerAccessPolicy
{
    public const string AdvicePurpose = "customer-advice";
    public static bool Supports(CapabilityDefinition capability) =>
        capability.ResourceType is "customer" or "document" or "report" || capability.CapabilityId == "search.query.execute";
}

// This is a customer-scope precondition, never proof that a document/report belongs
// to that customer. Every owner must validate its persisted resource boundary.
public sealed class CustomerAccessEvaluator(IAccessPolicyStore policies, ICustomerRelationshipStore relationships,
    ICustomerContextClient customers, ICustomerConsentClient consents, CapabilityCatalog catalog, TimeProvider clock)
{
    public async Task<CustomerAccessDecision> EvaluateAsync(TrustedActorContext actor, CustomerAccessRequest request, CancellationToken ct)
    {
        SubjectPolicyState? state = null;
        CustomerContext? customer = null;
        long consentVersion = 0;
        try
        {
            if (!policies.IsReady) return Decision("DependencyUnavailable");
            if (request.Purpose != CustomerAccessPolicy.AdvicePurpose || !catalog.TryGet(request.CapabilityId, out var capability) ||
                capability is not { Lifecycle: CapabilityLifecycle.Active } || !CustomerAccessPolicy.Supports(capability))
                return Decision("CustomerCapabilityUnsupported");
            state = await policies.GetSubjectPolicyStateAsync(actor.TenantId, actor.ActorId, ct).ConfigureAwait(false);
            if (state is null || state.Subject.ActorId != actor.ActorId || state.Subject.SubjectVersion < 1 || state.PolicyVersion < 1 || state.Subject.Status != MembershipStatus.Active) return Decision("SubjectInactive");
            var roleId = state.Subject.BusinessRoleId;
            if (string.IsNullOrWhiteSpace(roleId)) return Decision("NoBusinessRole");
            var role = await policies.GetRolePolicyAsync(actor.TenantId, roleId, state.PolicyVersion, ct).ConfigureAwait(false);
            if (role is null || role.RoleId != roleId || role.PolicyVersion != state.PolicyVersion) return Decision("PolicyUnavailable");
            if (!role.Grants.Any(grant => grant.CapabilityId == request.CapabilityId && Enum.IsDefined(grant.Scope)))
                return Decision("CapabilityNotGranted");
            customer = await customers.ReadAsync(actor.TenantId, request.CustomerId, ct).ConfigureAwait(false);
            if (!Valid(customer)) return Decision("CustomerUnavailable");
            var self = customer!.OwnerActorId == actor.ActorId;
            if (!self && !await relationships.IsAssignedAsync(actor.TenantId, actor.ActorId, request.CustomerId, ct).ConfigureAwait(false))
                return Decision("CustomerRelationshipRequired");
            if (!self)
            {
                var consent = await consents.EvaluateAsync(new(actor.TenantId, actor.ActorId, request.CustomerId,
                    request.CapabilityId, request.Purpose), ct).ConfigureAwait(false);
                if (consent.TenantId != actor.TenantId || consent.ActorId != actor.ActorId || consent.CustomerId != request.CustomerId ||
                    consent.CapabilityId != request.CapabilityId || consent.Purpose != request.Purpose || consent.Version < 1 ||
                    consent.EvaluatedAt < clock.GetUtcNow().AddSeconds(-10) || consent.EvaluatedAt > clock.GetUtcNow().AddSeconds(5))
                    return Decision("DependencyUnavailable");
                consentVersion = consent.Version;
                if (!consent.Allowed || consent.ExpiresAt is null || consent.ExpiresAt <= clock.GetUtcNow()) return Decision("ConsentRequired");
            }
            var confirmed = await customers.ReadAsync(actor.TenantId, request.CustomerId, ct).ConfigureAwait(false);
            if (!Valid(confirmed) || confirmed != customer || !await policies.IsDecisionStateCurrentAsync(
                actor.TenantId, actor.ActorId, roleId, state.PolicyVersion, state.Subject.SubjectVersion, ct).ConfigureAwait(false))
                return Decision("DecisionStateChanged");
            return Decision("Granted", self ? "Self" : "Advisor");
        }
        catch (AccessStoreUnavailableException) { return Decision("DependencyUnavailable"); }

        bool Valid(CustomerContext? value) => value is { Active: true, Version: > 0 } &&
            value.TenantId == actor.TenantId && value.CustomerId == request.CustomerId;
        CustomerAccessDecision Decision(string reason, string? basis = null) => new(Guid.NewGuid().ToString("N"),
            reason == "Granted" ? "Allow" : "Deny", actor.TenantId, actor.ActorId, request.CustomerId, request.CapabilityId,
            request.Purpose, reason, basis, state?.PolicyVersion ?? 0, state?.Subject.SubjectVersion ?? 0,
            customer?.Version ?? 0, consentVersion, clock.GetUtcNow());
    }
}

public sealed class HttpCustomerContextClient(HttpClient client, ReferenceDeliveryRoute route) : ICustomerContextClient, IDisposable
{
    private readonly OwnerCustomerTransport transport = new(client, route);
    public Task<CustomerContext?> ReadAsync(string tenantId, string customerId, CancellationToken cancellationToken) =>
        transport.SendAsync<CustomerContext>(tenantId, "internal/v1/customer-context/" + Uri.EscapeDataString(customerId), null, true, cancellationToken);
    public void Dispose() => transport.Dispose();
}

public sealed class HttpCustomerConsentClient(HttpClient client, ReferenceDeliveryRoute route) : ICustomerConsentClient, IDisposable
{
    private readonly OwnerCustomerTransport transport = new(client, route);
    public async Task<CustomerConsentDecision> EvaluateAsync(CustomerConsentRequest request, CancellationToken cancellationToken) =>
        await transport.SendAsync<CustomerConsentDecision>(request.TenantId, "internal/v1/consent/evaluate", request, false, cancellationToken).ConfigureAwait(false)
            ?? throw new AccessStoreUnavailableException();
    public void Dispose() => transport.Dispose();
}

internal sealed class OwnerCustomerTransport : IDisposable
{
    private readonly HttpClient client;
    private readonly ReferenceDeliveryRoute route;
    public OwnerCustomerTransport(HttpClient client, ReferenceDeliveryRoute route)
    {
        ReferenceOwnerBoundary.Validate(route);
        this.client = client; this.route = route;
        client.Timeout = TimeSpan.FromSeconds(5); client.MaxResponseContentBufferSize = 16_384;
    }
    public async Task<T?> SendAsync<T>(string tenant, string path, object? body, bool allowMissing, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(route.Endpoint, path));
            request.Headers.Add("X-Monergy-Tenant", tenant);
            request.Headers.Add("X-Monergy-Owner-Token", route.Token);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return default;
            if (!response.IsSuccessStatusCode) throw new AccessStoreUnavailableException();
            return await response.Content.ReadFromJsonAsync<T>(AccessEventProtocol.Json, ct).ConfigureAwait(false)
                ?? throw new AccessStoreUnavailableException();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        { throw new AccessStoreUnavailableException(error); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AccessStoreUnavailableException(); }
    }
    public void Dispose() => client.Dispose();
}
