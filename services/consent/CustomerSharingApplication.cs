using System.Net.Mail;
using Monergy.Platform;

namespace Monergy.Services.Consent;

public sealed record CustomerSharingChange(string RequestId, long ExpectedVersion, string RecipientEmail,
    string[] CapabilityIds, DateTimeOffset ExpiresAt);
public sealed record CustomerSharingScope(string Id, string Label, string Description);
public sealed record CustomerSharingOptions(string TenantId, string ActorId, string CustomerId, long Version,
    string Purpose, int MaximumDays, IReadOnlyList<CustomerSharingScope> Scopes);
public sealed record CustomerSharingReceipt(string RequestId, string RecipientEmail, ConsentReceipt Receipt);
public interface ICustomerSharingStore
{
    Task<ConsentOverview> ListAsync(string tenant, string customer, CancellationToken ct);
    Task<ConsentReceipt> GrantAsync(string tenant, CustomerOwnerContext owner, string ownerActor, ConsentGrantWrite request, CancellationToken ct);
}

public static class CustomerSharingPolicy
{
    // Only implemented customer-facing read/search/basic-report operations.
    // This records Consent; AM permissions and assignments remain independent.
    public static IReadOnlyList<CustomerSharingScope> Scopes { get; } = Array.AsReadOnly(new[] {
        new CustomerSharingScope("financial-profile.profile.read", "Read financial information", "Read your recorded financial information."),
        new CustomerSharingScope("evidence.document.read", "Read documents", "Read documents and their recorded versions."),
        new CustomerSharingScope("search.query.execute", "Search permitted information", "Search information that their current access permits."),
        new CustomerSharingScope("reporting.report.read", "Read basic reports", "Read supported financial summary reports."),
        new CustomerSharingScope("reporting.report.generate", "Generate basic reports", "Generate supported financial summary reports.")
    });
    public static string Email(string? value)
    {
        if (value is null || value.Length > 320 || value.Any(char.IsControl) ||
            !MailAddress.TryCreate(value, out var mail) || mail.Address != value.Trim() || !string.IsNullOrEmpty(mail.DisplayName))
            throw new TenantBoundaryException("RECIPIENT_EMAIL_INVALID", 400);
        return value.Trim().ToLowerInvariant();
    }
    public static CustomerSharingChange Validate(CustomerSharingChange change, DateTimeOffset now)
    {
        if (!Guid.TryParseExact(change.RequestId, "N", out _) || change.ExpectedVersion is < 1 or long.MaxValue ||
            change.CapabilityIds is not { Length: > 0 and <= 5 } ||
            change.CapabilityIds.Any(id => !Scopes.Any(scope => scope.Id == id)) ||
            change.CapabilityIds.Distinct(StringComparer.Ordinal).Count() != change.CapabilityIds.Length ||
            change.ExpiresAt <= now || change.ExpiresAt > now.AddDays(90))
            throw new TenantBoundaryException("SHARING_CHANGE_INVALID", 400);
        return change with { RecipientEmail = Email(change.RecipientEmail), CapabilityIds = change.CapabilityIds.Order(StringComparer.Ordinal).ToArray(), ExpiresAt = change.ExpiresAt.ToUniversalTime() };
    }
}

public sealed class CustomerSharingApplication(CustomerOwnerClient owners, ICustomerSharingStore store, TimeProvider clock)
{
    public async Task<CustomerSharingOptions> OptionsAsync(string customer, HttpContext http)
    {
        var original = await RequireOwnerAsync(customer, http).ConfigureAwait(false);
        var recorded = await store.ListAsync(original.Session.TenantId, customer, http.RequestAborted).ConfigureAwait(false);
        if (recorded.Version < 1) throw new TenantBoundaryException("CONSENT_VERSION_INVALID", 503);
        await ConfirmAsync(customer, http, original).ConfigureAwait(false);
        return new(original.Session.TenantId, original.Session.ActorId, customer, recorded.Version,
            CustomerConsentPolicy.Purpose, 90, CustomerSharingPolicy.Scopes);
    }
    public async Task<CustomerSharingReceipt> GrantAsync(string customer, CustomerSharingChange change, HttpContext http)
    {
        change = CustomerSharingPolicy.Validate(change, clock.GetUtcNow());
        var original = await RequireOwnerAsync(customer, http).ConfigureAwait(false);
        var tenant = original.Session.TenantId;
        var recipient = await owners.ResolveConsentRecipientAsync(tenant, change.RecipientEmail, http.RequestAborted).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("SHARING_RECIPIENT_UNAVAILABLE", 409);
        if (recipient.ActorId == original.Session.ActorId) throw new TenantBoundaryException("SELF_CONSENT_NOT_REQUIRED", 409);
        await RequireRecipientAsync(tenant, recipient.ActorId, http.RequestAborted).ConfigureAwait(false);
        await ConfirmAsync(customer, http, original).ConfigureAwait(false);
        var write = new ConsentGrantWrite(change.RequestId, change.ExpectedVersion, customer, recipient.ActorId,
            CustomerConsentPolicy.Purpose, change.CapabilityIds, change.ExpiresAt);
        var receipt = await store.GrantAsync(tenant, original.Owner, original.Session.ActorId, write, http.RequestAborted).ConfigureAwait(false);
        if (receipt.CustomerId != customer || receipt.ActorId != recipient.ActorId || receipt.Operation != "Granted" ||
            receipt.Version != change.ExpectedVersion + 1 || !TenantAccessClient.Identifier(receipt.Id, 128) || string.IsNullOrWhiteSpace(receipt.EvidenceReference))
            throw new TenantBoundaryException("SHARING_RECEIPT_INVALID", 503);
        // A failure here is an unconfirmed post-commit outcome, never a rollback claim.
        await ConfirmAsync(customer, http, original).ConfigureAwait(false);
        await RequireRecipientAsync(tenant, recipient.ActorId, http.RequestAborted).ConfigureAwait(false);
        return new(change.RequestId, recipient.RecipientLabel, receipt);
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
