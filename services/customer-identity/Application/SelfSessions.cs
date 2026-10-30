using System.Security.Cryptography;
using System.Text;
using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Application;

public sealed record SelfSessionEntry(string ActorId, string SessionReference, DateTimeOffset EstablishedAt,
    DateTimeOffset ExpiresAt, string Status, bool IsCurrent, string DeviceClass = "Unknown", string ClientFamily = "Unknown");
public sealed record SelfSessionPage(string TenantId, string ActorId, IReadOnlyList<SelfSessionEntry> Items, string? NextCursor);
public sealed record SelfSessionRevocation(string OperationId, string SessionReference);
public sealed record SelfSessionReceipt(string OperationId, string SessionReference, bool Revoked);
public interface ISelfSessionRepository
{
    Task<SelfSessionPage> ListAsync(TenantSessionContext current, string? after, CancellationToken ct);
    Task<SelfSessionReceipt> RevokeAsync(TenantSessionContext current, SelfSessionRevocation change, CancellationToken ct);
}
public static class SelfSessionReferences
{
    // One-way database reference, never the bearer session credential.
    public static string Hash(string session) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session)));
    public static bool Valid(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

// Intrinsic control of one's own sessions. No administrator/business grant.
public sealed class SelfSessionsApplication(ISelfSessionRepository repository, TenantSessionAuthority sessions)
{
    public async Task<SelfSessionPage> ListAsync(TenantSessionLookup lookup, string? after, CancellationToken ct)
    {
        if (after is not null && !SelfSessionReferences.Valid(after)) throw new TenantAccessException("SESSION_CURSOR_INVALID", 400);
        var current = await sessions.ValidateAsync(lookup, ct).ConfigureAwait(false);
        var page = await repository.ListAsync(current, after, ct).ConfigureAwait(false);
        var currentReference = SelfSessionReferences.Hash(current.AuthenticationContextId);
        if (page.TenantId != current.TenantId || page.ActorId != current.ActorId || page.Items.Count > 100 ||
            page.NextCursor is not null && (!SelfSessionReferences.Valid(page.NextCursor) || page.Items.Count != 100 || page.NextCursor != page.Items[^1].SessionReference) ||
            page.Items.Select(x => x.SessionReference).Distinct(StringComparer.Ordinal).Count() != page.Items.Count ||
            page.Items.Any(x => x.ActorId != current.ActorId || !SelfSessionReferences.Valid(x.SessionReference) || x.ExpiresAt <= x.EstablishedAt ||
                x.Status is not ("Active" or "Ended" or "Expired" or "AccessChanged") ||
                x.DeviceClass is not ("Desktop" or "Tablet" or "Mobile" or "Unknown") ||
                x.ClientFamily is not ("Chrome" or "Edge" or "Firefox" or "Safari" or "Other" or "Unknown") ||
                x.IsCurrent != (x.SessionReference == currentReference) || x.IsCurrent && x.Status != "Active"))
            throw new TenantAccessException("SESSION_LIST_INVALID", 503);
        await ConfirmAsync(lookup, current, ct).ConfigureAwait(false);
        return page;
    }
    public async Task<SelfSessionReceipt> RevokeAsync(TenantSessionLookup lookup, SelfSessionRevocation change, CancellationToken ct)
    {
        if (!Guid.TryParseExact(change.OperationId, "N", out _) || !SelfSessionReferences.Valid(change.SessionReference))
            throw new TenantAccessException("SESSION_CHANGE_INVALID", 400);
        var current = await sessions.ValidateAsync(lookup, ct).ConfigureAwait(false);
        if (change.SessionReference == SelfSessionReferences.Hash(current.AuthenticationContextId))
            throw new TenantAccessException("USE_CURRENT_SESSION_SIGN_OUT", 409);
        var receipt = await repository.RevokeAsync(current, change, ct).ConfigureAwait(false);
        if (receipt.OperationId != change.OperationId || receipt.SessionReference != change.SessionReference || !receipt.Revoked)
            throw new TenantAccessException("SESSION_RECEIPT_INVALID", 503);
        await ConfirmAsync(lookup, current, ct).ConfigureAwait(false);
        return receipt;
    }
    private async Task ConfirmAsync(TenantSessionLookup lookup, TenantSessionContext original, CancellationToken ct)
    {
        var current = await sessions.ValidateAsync(lookup, ct).ConfigureAwait(false);
        if (current.TenantId != original.TenantId || current.ActorId != original.ActorId ||
            current.SubjectVersion != original.SubjectVersion || current.AuthenticationContextId != original.AuthenticationContextId)
            throw new TenantAccessException("SESSION_CHANGED", 409);
    }
}
