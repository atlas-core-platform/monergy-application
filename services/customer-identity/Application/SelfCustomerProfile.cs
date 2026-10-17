using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Application;

public sealed record SelfCustomerProfile(string TenantId, string ActorId, string CustomerId, string DisplayName,
    string Email, string? Mobile, long Version, DateTimeOffset? UpdatedAt, DateTimeOffset? EmailVerifiedAt);
public sealed record SelfCustomerProfileChange(string OperationId, long ExpectedVersion, string DisplayName, string? Mobile);
public sealed record SelfCustomerProfileReceipt(string OperationId, long Version);
public interface ISelfCustomerProfileRepository
{
    Task<SelfCustomerProfile?> ReadAsync(string tenant, string actor, CancellationToken ct);
    Task<bool> OwnsAsync(string tenant, string actor, string customer, CancellationToken ct);
    Task<SelfCustomerProfileReceipt> SaveAsync(string tenant, string actor, SelfCustomerProfileChange change, DateTimeOffset now, CancellationToken ct);
}

// Intrinsic self-service identity operation, like ending one's own session or
// revoking one's own sharing: never a delegate/admin profile-edit permission.
public sealed class SelfCustomerProfileApplication(
    ISelfCustomerProfileRepository repository, TenantSessionAuthority sessions, TimeProvider clock)
{
    public async Task<SelfCustomerProfile> ReadAsync(TenantSessionLookup lookup, CancellationToken ct)
    {
        var current = await sessions.ValidateAsync(lookup, ct).ConfigureAwait(false);
        var profile = await repository.ReadAsync(current.TenantId, current.ActorId, ct).ConfigureAwait(false)
            ?? throw new TenantAccessException("OWNED_CUSTOMER_REQUIRED", 403);
        ValidateBinding(profile, current);
        await ConfirmAsync(lookup, current, profile.CustomerId, ct).ConfigureAwait(false);
        return profile;
    }

    public async Task<SelfCustomerProfileReceipt> SaveAsync(TenantSessionLookup lookup, SelfCustomerProfileChange change, CancellationToken ct)
    {
        if (!Guid.TryParseExact(change.OperationId, "N", out _) || change.ExpectedVersion < 0 ||
            string.IsNullOrWhiteSpace(change.DisplayName) || change.DisplayName.Trim().Length > 120 || change.DisplayName.Any(char.IsControl))
            throw new TenantAccessException("PROFILE_INVALID", 400);
        var mobile = string.IsNullOrWhiteSpace(change.Mobile) ? null : change.Mobile.Trim();
        if (mobile is not null && (mobile.Length is < 8 or > 16 || mobile[0] != '+' || mobile[1] is < '1' or > '9' || mobile[1..].Any(c => !char.IsAsciiDigit(c))))
            throw new TenantAccessException("PROFILE_MOBILE_INVALID", 400);
        var current = await sessions.ValidateAsync(lookup, ct).ConfigureAwait(false);
        var profile = await repository.ReadAsync(current.TenantId, current.ActorId, ct).ConfigureAwait(false)
            ?? throw new TenantAccessException("OWNED_CUSTOMER_REQUIRED", 403);
        ValidateBinding(profile, current);
        var receipt = await repository.SaveAsync(current.TenantId, current.ActorId,
            change with { DisplayName = change.DisplayName.Trim(), Mobile = mobile }, clock.GetUtcNow(), ct).ConfigureAwait(false);
        await ConfirmAsync(lookup, current, profile.CustomerId, ct).ConfigureAwait(false);
        return receipt;
    }

    private static void ValidateBinding(SelfCustomerProfile profile, TenantSessionContext session)
    {
        if (profile.TenantId != session.TenantId || profile.ActorId != session.ActorId || string.IsNullOrWhiteSpace(profile.CustomerId))
            throw new TenantAccessException("PROFILE_BINDING_INVALID", 503);
    }
    private async Task ConfirmAsync(TenantSessionLookup lookup, TenantSessionContext original, string customer, CancellationToken ct)
    {
        var current = await sessions.ValidateAsync(lookup, ct).ConfigureAwait(false);
        if (current.ActorId != original.ActorId || current.SubjectVersion != original.SubjectVersion)
            throw new TenantAccessException("PROFILE_SESSION_CHANGED", 409);
        if (!await repository.OwnsAsync(current.TenantId, current.ActorId, customer, ct).ConfigureAwait(false))
            throw new TenantAccessException("OWNED_CUSTOMER_REQUIRED", 403);
    }
}
