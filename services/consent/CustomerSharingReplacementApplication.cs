using Monergy.Platform;

namespace Monergy.Services.Consent;

public sealed record CustomerSharingReplacement(string RequestId, long ExpectedVersion, string[] CapabilityIds, DateTimeOffset ExpiresAt);
public sealed record ConsentReplacementWrite(string RequestId, long ExpectedVersion, string CustomerId, string GrantId,
    string ActorId, string[] CapabilityIds, DateTimeOffset ExpiresAt);
public sealed record ConsentReplacementReceipt(string RequestId, ConsentReceipt Revocation, ConsentReceipt Receipt);
public sealed record CustomerSharingReplacementOptions(CustomerSharingOptions Sharing, ConsentGrant Grant);
public interface ICustomerSharingReplacementStore
{
    Task<ConsentOverview> ReadReplacementAsync(string tenant, string customer, string grant, CancellationToken ct);
    Task<ConsentReplacementReceipt> ReplaceAsync(string tenant, CustomerOwnerContext owner, string ownerActor, ConsentReplacementWrite request, CancellationToken ct);
}

public static class CustomerSharingReplacementPolicy
{
    public static CustomerSharingReplacement Validate(CustomerSharingReplacement request, DateTimeOffset? now)
    {
        if (!Guid.TryParseExact(request.RequestId, "N", out _) || request.ExpectedVersion < 1 || request.ExpectedVersion > long.MaxValue - 2 ||
            request.CapabilityIds is not { Length: > 0 and <= 5 } ||
            request.CapabilityIds.Any(id => !CustomerSharingPolicy.Scopes.Any(scope => scope.Id == id)) ||
            request.CapabilityIds.Distinct(StringComparer.Ordinal).Count() != request.CapabilityIds.Length ||
            now is { } instant && (request.ExpiresAt <= instant || request.ExpiresAt > instant.AddDays(90)))
            throw new TenantBoundaryException("SHARING_CHANGE_INVALID", 400);
        return request with { CapabilityIds = request.CapabilityIds.Order(StringComparer.Ordinal).ToArray(), ExpiresAt = request.ExpiresAt.ToUniversalTime() };
    }
}

public sealed class CustomerSharingReplacementApplication(CustomerOwnerClient owners, ICustomerSharingReplacementStore store, TimeProvider clock)
{
    public async Task<CustomerSharingReplacementOptions> OptionsAsync(string customer, string grant, HttpContext http)
    {
        var original = await RequireOwnerAsync(customer, http).ConfigureAwait(false);
        var record = await ReadAsync(original.Session.TenantId, customer, grant, http).ConfigureAwait(false);
        var target = record.Items.Single();
        if (target.RevokedAt is not null) throw new TenantBoundaryException("CONSENT_NOT_CURRENT", 409);
        var label = await owners.RecipientLabelAsync(original.Session.TenantId, target.ActorId, http.RequestAborted).ConfigureAwait(false);
        await ConfirmAsync(customer, http, original).ConfigureAwait(false);
        return new(new(original.Session.TenantId, original.Session.ActorId, customer, record.Version,
            CustomerConsentPolicy.Purpose, 90, CustomerSharingPolicy.Scopes), target with { RecipientLabel = label });
    }

    public async Task<ConsentReplacementReceipt> ReplaceAsync(string customer, string grant, CustomerSharingReplacement change, HttpContext http)
    {
        change = CustomerSharingReplacementPolicy.Validate(change, clock.GetUtcNow());
        var original = await RequireOwnerAsync(customer, http).ConfigureAwait(false);
        var tenant = original.Session.TenantId;
        // Exact target lookup is independent of the recent-100 overview. A revoked
        // target is left to the store's replay-before-currentness decision.
        var record = await ReadAsync(tenant, customer, grant, http).ConfigureAwait(false);
        var target = record.Items.Single();
        if (target.ActorId == original.Session.ActorId) throw new TenantBoundaryException("SELF_CONSENT_NOT_REQUIRED", 409);
        await RequireRecipientAsync(tenant, target.ActorId, http.RequestAborted).ConfigureAwait(false);
        await ConfirmAsync(customer, http, original).ConfigureAwait(false);
        var receipt = await store.ReplaceAsync(tenant, original.Owner, original.Session.ActorId,
            new(change.RequestId, change.ExpectedVersion, customer, grant, target.ActorId, change.CapabilityIds, change.ExpiresAt), http.RequestAborted).ConfigureAwait(false);
        if (receipt.RequestId != change.RequestId || receipt.Revocation.Id != grant || receipt.Receipt.Id == grant ||
            !ValidReceipt(receipt.Revocation, "Revoked", change.ExpectedVersion + 1) ||
            !ValidReceipt(receipt.Receipt, "Granted", change.ExpectedVersion + 2) ||
            receipt.Revocation.EvidenceReference == receipt.Receipt.EvidenceReference)
            throw new TenantBoundaryException("SHARING_RECEIPT_INVALID", 503);
        // This post-commit check cannot roll back an already committed owner transaction.
        await ConfirmAsync(customer, http, original).ConfigureAwait(false);
        await RequireRecipientAsync(tenant, target.ActorId, http.RequestAborted).ConfigureAwait(false);
        return receipt;

        bool ValidReceipt(ConsentReceipt item, string operation, long version) => item.CustomerId == customer &&
            item.ActorId == target.ActorId && item.Operation == operation && item.Version == version &&
            TenantAccessClient.Identifier(item.Id, 128) && !string.IsNullOrWhiteSpace(item.EvidenceReference);
    }
    private async Task<ConsentOverview> ReadAsync(string tenant, string customer, string grant, HttpContext http)
    {
        if (!TenantAccessClient.Identifier(grant, 128)) throw new TenantBoundaryException("INVALID_CONSENT", 400);
        var record = await store.ReadReplacementAsync(tenant, customer, grant, http.RequestAborted).ConfigureAwait(false);
        if (record.Version < 1 || record.Items.Count > 1) throw new TenantBoundaryException("CONSENT_VERSION_INVALID", 503);
        if (record.Items.Count == 0) throw new TenantBoundaryException("CONSENT_NOT_CURRENT", 409);
        var target = record.Items.Single();
        if (target.Id != grant || target.CustomerId != customer || target.Purpose != CustomerConsentPolicy.Purpose || !TenantAccessClient.Identifier(target.ActorId, 128))
            throw new TenantBoundaryException("CONSENT_BINDING_INVALID", 503);
        return record;
    }
    private async Task RequireRecipientAsync(string tenant, string actor, CancellationToken ct)
    {
        try { await owners.RequireActiveMemberAsync(tenant, actor, ct).ConfigureAwait(false); }
        catch (TenantBoundaryException failure) when (failure.Status == 409)
        { throw new TenantBoundaryException("SHARING_RECIPIENT_UNAVAILABLE", 409); }
    }
    private async Task<(CustomerOwnerSession Session, CustomerOwnerContext Owner)> RequireOwnerAsync(string customer, HttpContext http)
    {
        var session = await owners.SessionAsync(http).ConfigureAwait(false);
        var owner = await owners.CustomerAsync(session.TenantId, customer, http.RequestAborted).ConfigureAwait(false);
        PostgresCustomerConsent.RequireOwner(session.TenantId, owner, session.ActorId, customer);
        return (session, owner);
    }
    private async Task ConfirmAsync(string customer, HttpContext http, (CustomerOwnerSession Session, CustomerOwnerContext Owner) original)
    {
        var fresh = await RequireOwnerAsync(customer, http).ConfigureAwait(false);
        if (fresh.Session.TenantId != original.Session.TenantId || fresh.Session.ActorId != original.Session.ActorId ||
            fresh.Session.SubjectVersion != original.Session.SubjectVersion || fresh.Owner.Version != original.Owner.Version)
            throw new TenantBoundaryException("CUSTOMER_CONTEXT_CHANGED", 409);
    }
}
