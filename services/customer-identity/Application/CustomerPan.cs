using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Application;

public sealed record CustomerPanChange(string OperationId, long ExpectedVersion, string? Pan, bool Confirm, string? AgreementRevision)
{
    public override string ToString() => "CustomerPanChange { sensitive value redacted }";
}
public sealed record PanState(string ActorId, long Version, string? KeyId, byte[]? Ciphertext,
    string? VerificationReference, DateTimeOffset? UpdatedAt);
public sealed record CustomerPanReceipt(string OperationId, long Version, string VerificationReference);
public sealed record CustomerPanView(string TenantId, string ActorId, string CustomerId, bool Enabled,
    bool CanSave, string? AgreementRevision, string? MaskedPan, long Version, string? VerificationReference, DateTimeOffset? UpdatedAt);
public interface ICustomerPanRepository
{
    Task<PanState?> ReadAsync(string tenant, string actor, string customer, DateTimeOffset now, CancellationToken ct);
    Task<CustomerPanReceipt> SaveAsync(string tenant, string actor, string customer, CustomerPanChange change, DateTimeOffset now, CancellationToken ct);
}
public sealed class CustomerPanApplication(ICustomerPanRepository repository, SelfCustomerProfileApplication profiles,
    ICustomerAccountAgreementRepository agreements, AccountAgreementCatalog catalog, CustomerPanProtection protection, TimeProvider clock)
{
    private async Task<(string? Revision, bool Accepted)> PublicationAsync(SelfCustomerProfile owner, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); var publication = catalog.Read(owner.TenantId, now);
        if (publication is null) return (null, false);
        var receipt = await agreements.ReadAsync(owner.TenantId, owner.ActorId, owner.CustomerId, publication.Revision, ct).ConfigureAwait(false);
        if (receipt is null) return (publication.Revision, false);
        if (receipt.Revision != publication.Revision || receipt.AcceptedAt < publication.PublishedAt || receipt.AcceptedAt > now ||
            !Guid.TryParseExact(receipt.OperationId, "N", out _)) throw new TenantAccessException("PAN_AGREEMENT_RESPONSE_INVALID", 503);
        return (publication.Revision, true);
    }
    public async Task<CustomerPanView> ReadAsync(TenantSessionLookup lookup, CancellationToken ct)
    {
        var owner = await profiles.ReadAsync(lookup, ct).ConfigureAwait(false);
        if (!protection.Enabled) return new(owner.TenantId, owner.ActorId, owner.CustomerId, false, false, null, null, 0, null, null);
        var state = await repository.ReadAsync(owner.TenantId, owner.ActorId, owner.CustomerId, clock.GetUtcNow(), ct).ConfigureAwait(false);
        if (state is not null && (state.ActorId != owner.ActorId || state.Version < 1 || !Reference(state.VerificationReference) ||
            state.UpdatedAt is null || state.UpdatedAt > clock.GetUtcNow() || (state.KeyId is null) != (state.Ciphertext is null)))
            throw new TenantAccessException("PAN_RESPONSE_INVALID", 503);
        var masked = state?.Ciphertext is null ? null : protection.Mask(owner.TenantId, owner.ActorId, owner.CustomerId, state);
        var publication = await PublicationAsync(owner, ct).ConfigureAwait(false);
        await ConfirmAsync(lookup, owner, ct).ConfigureAwait(false);
        return new(owner.TenantId, owner.ActorId, owner.CustomerId, true, publication.Accepted, publication.Revision,
            masked, state?.Version ?? 0, state?.VerificationReference, state?.UpdatedAt);
    }
    public async Task<CustomerPanReceipt> SaveAsync(TenantSessionLookup lookup, CustomerPanChange change, CancellationToken ct)
    {
        if (!protection.Enabled) throw new TenantAccessException("PAN_NOT_ENABLED", 403);
        if (!Guid.TryParseExact(change.OperationId, "N", out var op) || op.ToString("N") != change.OperationId || change.ExpectedVersion < 0 || change.ExpectedVersion == long.MaxValue || !change.Confirm)
            throw new TenantAccessException("PAN_CONFIRMATION_REQUIRED", 400);
        change = change with { Pan = change.Pan is null ? null : CustomerPanProtection.Normalize(change.Pan) };
        var owner = await profiles.ReadAsync(lookup, ct).ConfigureAwait(false);
        if (change.Pan is not null)
        {
            var publication = await PublicationAsync(owner, ct).ConfigureAwait(false);
            if (!publication.Accepted || publication.Revision != change.AgreementRevision)
                throw new TenantAccessException("PAN_CURRENT_AGREEMENTS_REQUIRED", 409);
        }
        else if (change.AgreementRevision is not null) throw new TenantAccessException("PAN_CLEAR_REVISION_INVALID", 400);
        await ConfirmAsync(lookup, owner, ct).ConfigureAwait(false);
        var instant = clock.GetUtcNow(); var now = new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);
        var receipt = await repository.SaveAsync(owner.TenantId, owner.ActorId, owner.CustomerId, change, now, ct).ConfigureAwait(false);
        if (receipt.OperationId != change.OperationId || receipt.Version != change.ExpectedVersion + 1 || !Reference(receipt.VerificationReference))
            throw new TenantAccessException("PAN_RECEIPT_INVALID", 503);
        await ConfirmAsync(lookup, owner, ct).ConfigureAwait(false);
        if (change.Pan is not null && (await PublicationAsync(owner, ct).ConfigureAwait(false)) != (change.AgreementRevision, true))
            throw new TenantAccessException("PAN_AGREEMENTS_CHANGED", 409);
        return receipt;
    }
    private async Task ConfirmAsync(TenantSessionLookup lookup, SelfCustomerProfile original, CancellationToken ct)
    {
        var current = await profiles.ReadAsync(lookup, ct).ConfigureAwait(false);
        if (current.TenantId != original.TenantId || current.ActorId != original.ActorId || current.CustomerId != original.CustomerId)
            throw new TenantAccessException("PAN_CUSTOMER_CHANGED", 409);
    }
    private static bool Reference(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
}
