using System.Security.Cryptography;
using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Application;

public sealed record PersonaTesterWelcomeRequest(string TenantId, string Email, string ReturnPath);
public sealed record PersonaTesterActivationConfirm(string TenantId, string Ticket, string Password);

public sealed class PersonaTesterActivationOptions
{
    private readonly byte[] accessKey;

    private PersonaTesterActivationOptions(byte[] accessKey) => this.accessKey = accessKey;

    public static PersonaTesterActivationOptions Read(IConfiguration configuration, bool isDevelopment)
    {
        if (!isDevelopment)
            throw new InvalidOperationException("Tester activation is Development-only.");
        var encoded = configuration["Monergy:PersonaCredentials:TesterActivation:AccessSecret"] ?? "";
        byte[] key;
        try { key = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new InvalidOperationException("Tester activation requires a base64 32-byte access secret."); }
        if (key.Length != 32)
            throw new InvalidOperationException("Tester activation requires a base64 32-byte access secret.");
        return new(key);
    }

    public bool Authorize(string supplied)
    {
        byte[] candidate;
        try { candidate = Convert.FromBase64String(supplied); }
        catch (FormatException) { return false; }
        try { return candidate.Length == accessKey.Length && CryptographicOperations.FixedTimeEquals(candidate, accessKey); }
        finally { CryptographicOperations.ZeroMemory(candidate); }
    }
}

// This owner is registered only in Development when the explicit tester switch
// is enabled. It activates a pre-provisioned C&I identity; it cannot create a
// principal, select a role or tenant, or issue a signed-in session.
public sealed class PersonaTesterActivation(
    IPersonaCredentialRepository repository,
    CustomerEmailCodeProtector protector,
    CustomerPasswordPolicy passwords,
    TimeProvider clock)
{
    private static readonly HashSet<string> ReturnPaths = ["/", "/access", "/advisor"];

    public async Task<object> CreateWelcomeLinkAsync(PersonaTesterWelcomeRequest request, CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(request.TenantId, 64);
        var email = CustomerEmailAccess.NormalizeEmail(request.Email);
        if (!ReturnPaths.Contains(request.ReturnPath))
            throw new TenantAccessException("WELCOME_DESTINATION_INVALID", 400);
        var ticket = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var hash = CustomerEmailCodeProtector.ChallengeHash(ticket);
        var now = clock.GetUtcNow();
        var queued = await repository.QueueTesterAsync(request.TenantId, new(
            "persona-activation", email, hash,
            protector.Verifier(request.TenantId + ":persona-activation", ticket, ticket),
            [], now), ct).ConfigureAwait(false);
        if (!queued) throw new TenantAccessException("WELCOME_LINK_NOT_AVAILABLE", 409);
        var path = "/welcome#tenant=" + Uri.EscapeDataString(request.TenantId) +
            "&ticket=" + ticket + "&return=" + Uri.EscapeDataString(request.ReturnPath);
        return new { welcomePath = path, expiresAt = now.AddMinutes(5) };
    }

    public async Task<object> ActivateAsync(PersonaTesterActivationConfirm request, CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(request.TenantId, 64);
        if (request.Ticket.Length != 64 || request.Ticket.Any(static value => !Uri.IsHexDigit(value)))
            throw new TenantAccessException("WELCOME_LINK_INVALID", 401);
        var passwordHash = passwords.Hash(request.Password);
        var actor = await repository.ActivateAsync(request.TenantId,
            CustomerEmailCodeProtector.ChallengeHash(request.Ticket),
            protector.Verifier(request.TenantId + ":persona-activation", request.Ticket, request.Ticket),
            passwordHash, ct).ConfigureAwait(false);
        if (actor is null) throw new TenantAccessException("WELCOME_LINK_INVALID", 401);
        return new { activated = true };
    }
}
