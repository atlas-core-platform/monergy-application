using System.Globalization;
using System.Security.Cryptography;
using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Application;

// Email ownership activates an already-provisioned principal. It cannot create
// a C&I actor, choose its role or tenant, or grant an AM membership.
public sealed record PersonaActivationRequest(string TenantId, string Email);
public sealed record PersonaActivationConfirm(string TenantId, string ChallengeId, string Code, string Password);
public sealed record PersonaRecoveryConfirm(string TenantId, string ChallengeId, string Code, string NewPassword);
public sealed record PersonaCredentialIdentity(string TenantId, string Email, string ActorId,
    string PasswordHash, long CredentialVersion);

public interface IPersonaCredentialRepository
{
    Task QueueAsync(string tenant, RegistrationCode challenge, CancellationToken ct);
    Task<DateTimeOffset?> QueueTesterAsync(string tenant, RegistrationCode challenge, CancellationToken ct);
    Task<string?> ActivateAsync(string tenant, string hash, string verifier, string passwordHash, CancellationToken ct);
    Task<PersonaCredentialIdentity?> ReadAsync(string tenant, string email, CancellationToken ct);
    Task<bool> RecordAttemptAsync(PersonaCredentialIdentity identity, bool valid, CancellationToken ct);
    Task<bool> IsCurrentAsync(PersonaCredentialIdentity identity, CancellationToken ct);
    Task<bool> RecoverAsync(string tenant, string hash, string verifier, string newHash, CancellationToken ct);
}

public sealed class PersonaCredentialAccess(
    IPersonaCredentialRepository repository,
    CustomerEmailCodeProtector protector,
    CustomerPasswordPolicy passwords,
    TenantSessionAuthority sessions,
    TimeProvider clock)
{
    public async Task<CustomerEmailChallenge> RequestAsync(PersonaActivationRequest request, bool recovery, CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(request.TenantId, 64);
        var email = CustomerEmailAccess.NormalizeEmail(request.Email);
        var purpose = recovery ? "persona-recovery" : "persona-activation";
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var challengeHash = CustomerEmailCodeProtector.ChallengeHash(id);
        var now = clock.GetUtcNow();
        // Unknown/activated/disabled recipients get the same HTTP envelope.
        await repository.QueueAsync(request.TenantId, new(
            purpose, email, challengeHash,
            protector.Verifier(request.TenantId + ":" + purpose, id, code),
            protector.Protect(request.TenantId + ":" + purpose, challengeHash, code), now), ct)
            .ConfigureAwait(false);
        return new(id, now.AddMinutes(5), 60);
    }

    public async Task<object> ActivateAsync(PersonaActivationConfirm request, CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(request.TenantId, 64);
        CustomerRegistrationPolicy.ValidateChallenge(request.ChallengeId, request.Code);
        var hash = passwords.Hash(request.Password);
        var actor = await repository.ActivateAsync(request.TenantId,
            CustomerEmailCodeProtector.ChallengeHash(request.ChallengeId),
            protector.Verifier(request.TenantId + ":persona-activation", request.ChallengeId, request.Code),
            hash, ct).ConfigureAwait(false);
        if (actor is null) throw new TenantAccessException("EMAIL_CODE_INVALID", 401);
        // No bearer session is issued on activation: sign in separately.
        return new { activated = true };
    }

    public async Task<TenantSessionContext> SignInAsync(PasswordSignIn request, CancellationToken ct)
        => await SignInAsync(request, SessionClientDescriptor.Unknown, ct).ConfigureAwait(false);

    public async Task<TenantSessionContext> SignInAsync(PasswordSignIn request, SessionClientDescriptor client, CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(request.TenantId, 64);
        var email = CustomerEmailAccess.NormalizeEmail(request.Email);
        var identity = await repository.ReadAsync(request.TenantId, email, ct).ConfigureAwait(false);
        var valid = passwords.Verify(identity?.PasswordHash, request.Password);
        if (identity is null || !await repository.RecordAttemptAsync(identity, valid, ct).ConfigureAwait(false) || !valid)
            throw new TenantAccessException("AUTHENTICATION_REQUIRED", 401);
        // This owner reads the current AM active membership and subject version
        // before AND after issuing the C&I session. Never trust a posted role.
        var session = await sessions.EstablishVerifiedAsync(identity.TenantId, identity.ActorId, client, ct).ConfigureAwait(false);
        try
        {
            if (!await repository.IsCurrentAsync(identity, ct).ConfigureAwait(false))
                throw new TenantAccessException("AUTHENTICATION_REQUIRED", 401);
            return session;
        }
        catch
        {
            await sessions.EndAsync(new(identity.TenantId, session.AuthenticationContextId), CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task<object> RecoverAsync(PersonaRecoveryConfirm request, CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(request.TenantId, 64);
        CustomerRegistrationPolicy.ValidateChallenge(request.ChallengeId, request.Code);
        var hash = passwords.Hash(request.NewPassword);
        if (!await repository.RecoverAsync(request.TenantId,
            CustomerEmailCodeProtector.ChallengeHash(request.ChallengeId),
            protector.Verifier(request.TenantId + ":persona-recovery", request.ChallengeId, request.Code),
            hash, ct).ConfigureAwait(false))
            throw new TenantAccessException("EMAIL_CODE_INVALID", 401);
        return new { recovered = true };
    }
}
