using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Application;

public sealed record RegistrationRequest(string TenantId, string Email);
public sealed record RegistrationConfirmation(string TenantId, string ChallengeId, string Code,
    string DisplayName, string Password, string AgreementRevision,
    bool AcceptTerms, bool AcknowledgePrivacy, bool ConsentToProcessing);
public sealed record PasswordSignIn(string TenantId, string Email, string Password);
public sealed record PasswordRecoveryConfirmation(string TenantId, string ChallengeId, string Code, string NewPassword);

// Owner-side password handling. Whitespace is significant and is never trimmed.
// Passwords are hashed with unique salts; no reversible credential is persisted.
public sealed class CustomerPasswordPolicy
{
    private readonly PasswordHasher<string> hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = 600_000,
    }));
    private readonly string dummy;

    public CustomerPasswordPolicy()
    {
        dummy = hasher.HashPassword("unavailable", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public static void Validate(string password)
    {
        if (password is null || password.Length is < 15 or > 128 || password.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(password))
            throw new TenantAccessException("PASSWORD_REQUIREMENTS_NOT_MET", 400);
    }

    public string Hash(string password)
    {
        Validate(password);
        return hasher.HashPassword("customer", password);
    }

    public bool Verify(string? hash, string? password)
    {
        // Unknown accounts perform the same expensive verifier work. HTTP limits
        // and durable recipient counters remain required at the caller boundary.
        if (password is null || password.Length > 128) return false;
        try
        {
            var result = hasher.VerifyHashedPassword("customer", hash ?? dummy, password);
            return hash is not null && result != PasswordVerificationResult.Failed;
        }
        catch (FormatException) { return false; }
    }
}

public static class CustomerRegistrationPolicy
{
    public static void Validate(RegistrationConfirmation request)
    {
        TenantAccessProtocol.Identifier(request.TenantId, 64);
        ValidateChallenge(request.ChallengeId, request.Code);
        CustomerPasswordPolicy.Validate(request.Password);
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName != request.DisplayName.Trim() ||
            request.DisplayName.Length > 120 || request.DisplayName.Any(char.IsControl))
            throw new TenantAccessException("PROFILE_NAME_INVALID", 400);
        if (!request.AcceptTerms || !request.AcknowledgePrivacy || !request.ConsentToProcessing ||
            request.AgreementRevision is null || request.AgreementRevision.Length != 64 ||
            request.AgreementRevision.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new TenantAccessException("AGREEMENTS_EXPLICIT_ACCEPTANCE_REQUIRED", 400);
    }

    public static void ValidateChallenge(string challenge, string code)
    {
        if (challenge is null || challenge.Length != 64 || challenge.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            code is null || code.Length != 6 || code.Any(c => !char.IsAsciiDigit(c)))
            throw new TenantAccessException("EMAIL_CODE_INVALID", 401);
    }
}
