using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Application;

public sealed record CustomerEmailRequest(string TenantId, string Email);
public sealed record CustomerEmailChallenge(string ChallengeId, DateTimeOffset ExpiresAt, int RetryAfterSeconds);
public sealed record CustomerEmailConfirmation(string TenantId, string ChallengeId, string Code);
public sealed record EmailCustomer(string ActorId, string CustomerId, long CustomerVersion);
public sealed record EmailChallengeWrite(string Email, string ChallengeHash, string CodeHash, byte[] ProtectedCode, DateTimeOffset Now);
public sealed record PendingCustomerEmail(string ChallengeHash, string Email, byte[] ProtectedCode, int DeliveryAttempt);

public interface ICustomerEmailAccessRepository
{
    Task QueueAsync(string tenant, EmailChallengeWrite challenge, CancellationToken ct);
    Task<EmailCustomer?> ConsumeAsync(string tenant, string challengeHash, string codeHash, DateTimeOffset now, CancellationToken ct);
    Task<bool> IsCurrentAsync(string tenant, EmailCustomer customer, CancellationToken ct);
    Task RecordSessionAsync(string tenant, string challengeHash, EmailCustomer customer, bool issued, DateTimeOffset now, CancellationToken ct);
    Task<PendingCustomerEmail?> LeaseAsync(string tenant, DateTimeOffset now, CancellationToken ct);
    Task CompleteDeliveryAsync(string tenant, string challengeHash, int deliveryAttempt, bool sent, DateTimeOffset now, CancellationToken ct);
}

public interface ICustomerEmailSender
{
    Task SendAsync(string email, string code, CancellationToken ct);
}

// Only a configured owner-side secret can derive code verifiers and decrypt the
// short-lived delivery payload. No code or mail credential is returned by HTTP.
public sealed class CustomerEmailCodeProtector
{
    private readonly byte[] encryptionKey;
    private readonly byte[] verifierKey;
    private readonly byte[] testerTicketKey;
    public CustomerEmailCodeProtector(string secret)
    {
        byte[] key;
        try { key = Convert.FromBase64String(secret); }
        catch (FormatException) { throw new InvalidOperationException("Email access requires a base64 32-byte secret."); }
        if (key.Length != 32) throw new InvalidOperationException("Email access requires a base64 32-byte secret.");
        encryptionKey = HMACSHA256.HashData(key, "monergy.email.delivery.v1"u8);
        verifierKey = HMACSHA256.HashData(key, "monergy.email.verifier.v1"u8);
        testerTicketKey = HMACSHA256.HashData(key, "monergy.tester.welcome.v1"u8);
        CryptographicOperations.ZeroMemory(key);
    }

    public string Verifier(string tenant, string challengeId, string code) => Convert.ToHexStringLower(
        HMACSHA256.HashData(verifierKey, Encoding.UTF8.GetBytes(tenant + "\n" + challengeId + "\n" + code)));
    public string DeriveTesterTicket(string tenant, string normalizedEmail, string requestId) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(testerTicketKey,
            Encoding.UTF8.GetBytes(tenant + "\n" + normalizedEmail + "\n" + requestId)));
    public byte[] Protect(string tenant, string challengeHash, string code)
    {
        var bytes = Encoding.UTF8.GetBytes(code);
        var result = new byte[28 + bytes.Length];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var cipher = new AesGcm(encryptionKey, 16);
        cipher.Encrypt(result.AsSpan(0, 12), bytes, result.AsSpan(28), result.AsSpan(12, 16), Encoding.UTF8.GetBytes(tenant + "\n" + challengeHash));
        CryptographicOperations.ZeroMemory(bytes);
        return result;
    }
    public string Unprotect(string tenant, string challengeHash, byte[] value)
    {
        if (value.Length != 34) throw new CryptographicException("Invalid email delivery payload.");
        var bytes = new byte[6];
        using var cipher = new AesGcm(encryptionKey, 16);
        cipher.Decrypt(value.AsSpan(0, 12), value.AsSpan(28), value.AsSpan(12, 16), bytes, Encoding.UTF8.GetBytes(tenant + "\n" + challengeHash));
        var result = Encoding.UTF8.GetString(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return result;
    }
    public static string ChallengeHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class CustomerEmailAccess(
    ICustomerEmailAccessRepository repository,
    CustomerEmailCodeProtector protector,
    TenantSessionAuthority sessions,
    TimeProvider clock)
{
    public async Task<CustomerEmailChallenge> RequestAsync(CustomerEmailRequest request, CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(request.TenantId, 64);
        var email = NormalizeEmail(request.Email);
        var challengeId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var hash = CustomerEmailCodeProtector.ChallengeHash(challengeId);
        var now = clock.GetUtcNow();
        await repository.QueueAsync(request.TenantId, new(email, hash,
            protector.Verifier(request.TenantId, challengeId, code), protector.Protect(request.TenantId, hash, code), now), ct).ConfigureAwait(false);
        // Identical response for absent, unowned and recently requested identities.
        // Delivery is asynchronous so SMTP timing cannot enumerate recipients.
        return new(challengeId, now.AddMinutes(5), 60);
    }

    public async Task<TenantSessionContext> ConfirmAsync(CustomerEmailConfirmation request, CancellationToken ct)
    {
        TenantAccessProtocol.Identifier(request.TenantId, 64);
        if (request.ChallengeId is null || request.ChallengeId.Length != 64 || request.ChallengeId.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            request.Code is null || request.Code.Length != 6 || request.Code.Any(c => !char.IsAsciiDigit(c)))
            throw new TenantAccessException("EMAIL_CODE_INVALID", 401);
        var hash = CustomerEmailCodeProtector.ChallengeHash(request.ChallengeId);
        var customer = await repository.ConsumeAsync(request.TenantId, hash,
            protector.Verifier(request.TenantId, request.ChallengeId, request.Code), clock.GetUtcNow(), ct).ConfigureAwait(false);
        if (customer is null) throw new TenantAccessException("EMAIL_CODE_INVALID", 401);
        TenantSessionContext? session = null;
        try
        {
            if (!await repository.IsCurrentAsync(request.TenantId, customer, ct).ConfigureAwait(false))
                throw new TenantAccessException("EMAIL_CODE_INVALID", 401);
            // Internal identity-owner method; there is no actor-selecting HTTP endpoint.
            session = await sessions.EstablishVerifiedAsync(request.TenantId, customer.ActorId, ct).ConfigureAwait(false);
            if (!await repository.IsCurrentAsync(request.TenantId, customer, ct).ConfigureAwait(false))
                throw new TenantAccessException("EMAIL_CODE_INVALID", 401);
            await repository.RecordSessionAsync(request.TenantId, hash, customer, true, clock.GetUtcNow(), ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            if (session is not null)
                await sessions.EndAsync(new(request.TenantId, session.AuthenticationContextId), CancellationToken.None).ConfigureAwait(false);
            await repository.RecordSessionAsync(request.TenantId, hash, customer, false, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public static string NormalizeEmail(string? value)
    {
        if (value is null || value.Length > 320 || value.Any(char.IsControl) ||
            !MailAddress.TryCreate(value, out var mail) || mail.Address != value.Trim() ||
            !string.IsNullOrEmpty(mail.DisplayName))
            throw new TenantAccessException("EMAIL_INVALID", 400);
        return value.Trim().ToLowerInvariant();
    }
}
