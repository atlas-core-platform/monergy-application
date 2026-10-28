using System.Globalization;
using System.Security.Cryptography;
using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Application;

public interface IPersonaInvitationDeliveryRepository
{
    Task<string?> ReadActivationEmailAsync(string tenant, string actorId, CancellationToken ct);
    Task QueueAsync(string tenant, RegistrationCode challenge, CancellationToken ct);
}

public sealed record PersonaInvitationDeliveryReceipt(string ActorId, string State);

// The administrator selects an existing tenant principal, never an email address,
// password, challenge or session. C&I resolves the recipient and reuses the same
// short-lived delivery-backed activation lifecycle as the invited person.
public sealed class PersonaInvitationAdministration(
    IPersonaInvitationDeliveryRepository repository,
    CustomerEmailCodeProtector protector,
    TimeProvider clock)
{
    public async Task<PersonaInvitationDeliveryReceipt> RequestDeliveryAsync(
        string tenant, string actorId, CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(tenant, 64);
        TenantAccessProtocol.Identifier(actorId, 128);

        var email = await repository.ReadActivationEmailAsync(tenant, actorId, ct).ConfigureAwait(false);
        if (email is not null)
        {
            var challengeId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            var challengeHash = CustomerEmailCodeProtector.ChallengeHash(challengeId);
            var now = clock.GetUtcNow();
            await repository.QueueAsync(tenant, new RegistrationCode(
                "persona-activation", email, challengeHash,
                protector.Verifier(tenant + ":persona-activation", challengeId, code),
                protector.Protect(tenant + ":persona-activation", challengeHash, code), now), ct)
                .ConfigureAwait(false);
        }

        // Preserve the public request's non-enumerating contract. Delivery-backed
        // status appears only after the configured sender confirms the recipient.
        return new(actorId, "Accepted");
    }
}
