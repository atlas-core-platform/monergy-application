using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Monergy.Platform;

// Shared password mechanics only. Identity ownership, membership and sessions
// remain with their respective tenant or platform owners.
public sealed class StrongPasswordPolicy(string purpose)
{
    public const int Iterations = 600_000;
    public const int MinimumLength = 15;
    public const int MaximumLength = 128;

    private readonly PasswordHasher<string> hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = Iterations,
    }));
    private readonly string subject = string.IsNullOrWhiteSpace(purpose)
        ? throw new ArgumentException("A password-hash purpose is required.", nameof(purpose))
        : purpose;
    private readonly string dummy = CreateDummy(purpose);

    public static bool IsValid(string? password) => password is not null &&
        password.Length is >= MinimumLength and <= MaximumLength &&
        !password.Any(char.IsControl) && !string.IsNullOrWhiteSpace(password);

    public string Hash(string password)
    {
        if (!IsValid(password)) throw new ArgumentException("Password requirements are not met.", nameof(password));
        return hasher.HashPassword(subject, password);
    }

    public bool Verify(string? hash, string? password)
    {
        if (password is null || password.Length > MaximumLength) return false;
        try
        {
            var result = hasher.VerifyHashedPassword(subject, hash ?? dummy, password);
            return hash is not null && result != PasswordVerificationResult.Failed;
        }
        catch (FormatException) { return false; }
    }

    private static string CreateDummy(string purpose)
    {
        var hasher = new PasswordHasher<string>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = Iterations,
        }));
        return hasher.HashPassword(purpose, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }
}
