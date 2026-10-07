using System.Net.Mail;

namespace Monergy.Contracts;

// Internal AM-to-C&I onboarding port. Provisioning creates an identity record;
// it does not authenticate a user or grant tenant membership or business access.
public sealed record TenantIdentityProvisioningRequest(string TenantId, string ImportId, int RowNumber,
    string NormalizedEmail, string IdempotencyKey);
public sealed record TenantIdentityProvisioningReceipt(string TenantId, string NormalizedEmail, string ActorId,
    string IdempotencyKey);

public static class TenantIdentityProvisioningProtocol
{
    public static void Validate(TenantIdentityProvisioningRequest request, string trustedTenant)
    {
        ArgumentNullException.ThrowIfNull(request);
        TenantAccessProtocol.Identifier(request.TenantId, 64);
        TenantAccessProtocol.Identifier(request.ImportId, 80);
        var email = request.NormalizedEmail;
        if (request.TenantId != trustedTenant || request.RowNumber is < 1 or > 500 ||
            request.IdempotencyKey is not { Length: 64 } ||
            request.IdempotencyKey.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')) ||
            string.IsNullOrEmpty(email) || email.Length > 320 ||
            email.Any(c => !char.IsAscii(c) || char.IsAsciiLetterUpper(c) || char.IsWhiteSpace(c) || char.IsControl(c)) ||
            !MailAddress.TryCreate(email, out var address) || address.DisplayName.Length != 0 ||
            address.Address != email || address.User.Length > 64 || address.User.StartsWith('"') || !address.Host.Contains('.'))
            throw new TenantAccessException("INVALID_IDENTITY_PROVISIONING_REQUEST", 400);
    }
}
