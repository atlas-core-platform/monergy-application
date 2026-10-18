using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.Services.CustomerIdentity.Application;

// Admission is derived from current owner records, not a browser completion flag.
// Existing ownership, roles, Consent and customer/profile versions never change.
public sealed class CustomerWorkspaceSetupApplication
{
    private readonly ISelfCustomerProfileRepository profiles;
    private readonly ICustomerAccountAgreementRepository agreements;
    private readonly AccountAgreementCatalog catalog;
    private readonly TenantSessionAuthority sessions;
    private readonly TimeProvider clock;
    private readonly bool required;

    public CustomerWorkspaceSetupApplication(ISelfCustomerProfileRepository profiles,
        ICustomerAccountAgreementRepository agreements, AccountAgreementCatalog catalog,
        TenantSessionAuthority sessions, TimeProvider clock, IConfiguration configuration)
    {
        this.profiles = profiles; this.agreements = agreements; this.catalog = catalog;
        this.sessions = sessions; this.clock = clock;
        required = configuration.GetValue<bool>("Monergy:CustomerWorkspaceSetup:Required");
        if (required && configuration["Monergy:CustomerEmailAccess:Enabled"] != "true")
            throw new InvalidOperationException("Required customer setup needs configured email verification.");
    }
    public async Task<CustomerWorkspaceSetupStatus> ReadAsync(TenantSessionLookup lookup, CancellationToken ct)
    {
        var session = await sessions.ValidateAsync(lookup, ct).ConfigureAwait(false);
        var status = await ReadForActorAsync(session.TenantId, session.ActorId, ct).ConfigureAwait(false);
        var confirmed = await sessions.ValidateAsync(lookup, ct).ConfigureAwait(false);
        if (confirmed.ActorId != session.ActorId || confirmed.SubjectVersion != session.SubjectVersion)
            throw new TenantAccessException("SETUP_SESSION_CHANGED", 409);
        return status;
    }
    // Only the separately authenticated owner workload endpoint may select actor.
    public async Task<CustomerWorkspaceSetupStatus> ReadForActorAsync(string tenant, string actor, CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(tenant, 64); TenantAccessProtocol.Identifier(actor);
        var profile = await profiles.ReadAsync(tenant, actor, ct).ConfigureAwait(false)
            ?? throw new TenantAccessException("OWNED_CUSTOMER_REQUIRED", 403);
        if (profile.TenantId != tenant || profile.ActorId != actor || string.IsNullOrWhiteSpace(profile.CustomerId))
            throw new TenantAccessException("SETUP_BINDING_INVALID", 503);
        var now = clock.GetUtcNow();
        var emailVerified = profile.EmailVerifiedAt.HasValue && profile.EmailVerifiedAt <= now;
        var reviewed = profile.Version > 0 && profile.UpdatedAt.HasValue && profile.UpdatedAt <= now && !string.IsNullOrWhiteSpace(profile.DisplayName);
        var publication = required ? catalog.Read(tenant, now) : null;
        var acceptance = publication is null ? null : await agreements.ReadAsync(tenant, actor, profile.CustomerId, publication.Revision, ct).ConfigureAwait(false);
        if (acceptance is not null && (acceptance.Revision != publication!.Revision ||
            acceptance.AcceptedAt < publication.PublishedAt || acceptance.AcceptedAt > now ||
            !Guid.TryParseExact(acceptance.OperationId, "N", out _)))
            throw new TenantAccessException("SETUP_ACCEPTANCE_INVALID", 503);
        if (!await profiles.OwnsAsync(tenant, actor, profile.CustomerId, ct).ConfigureAwait(false))
            throw new TenantAccessException("OWNED_CUSTOMER_REQUIRED", 403);
        var ready = !required || emailVerified && reviewed && publication is not null && acceptance is not null;
        return new(tenant, actor, profile.CustomerId, required, emailVerified, reviewed,
            publication is not null, acceptance is not null, ready, publication?.Revision);
    }
}
