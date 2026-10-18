using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Application;

// LOCAL-only AES-GCM record protection; configured keys never leave C&I.
public sealed class CustomerPanProtection
{
    private readonly Dictionary<string, byte[]> keys = new(StringComparer.Ordinal);
    public bool Enabled { get; }
    public string CurrentKeyId { get; } = "";
    public CustomerPanProtection(IConfiguration config)
    {
        Enabled = config.GetValue<bool>("Monergy:CustomerPan:Enabled");
        if (!Enabled) return;
        CurrentKeyId = config["Monergy:CustomerPan:CurrentKeyId"] ?? "";
        foreach (var entry in config.GetSection("Monergy:CustomerPan:Keys").GetChildren())
        {
            if (entry.Key.Length is < 1 or > 32 || entry.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new InvalidOperationException("Invalid PAN protection key configuration.");
            byte[] key;
            try { key = Convert.FromBase64String(entry.Value ?? ""); }
            catch (FormatException) { throw new InvalidOperationException("Invalid PAN protection key configuration."); }
            if (key.Length != 32) throw new InvalidOperationException("Invalid PAN protection key configuration.");
            keys.Add(entry.Key, key);
        }
        if (keys.Count is < 1 or > 4 || !keys.ContainsKey(CurrentKeyId))
            throw new InvalidOperationException("PAN protection requires a current configured key.");
    }
    public static string Normalize(string value)
    {
        if (value.Length > 32 || value.Any(c => c != ' ' && !char.IsAsciiLetterOrDigit(c))) throw new TenantAccessException("PAN_INVALID", 400);
        var pan = value.Trim();
        if (pan.Length != 10 || pan.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new TenantAccessException("PAN_INVALID", 400);
        pan = pan.ToUpperInvariant();
        if (pan[..5].Any(c => c is < 'A' or > 'Z') || pan[5..9].Any(c => !char.IsAsciiDigit(c)) || pan[9] is < 'A' or > 'Z')
            throw new TenantAccessException("PAN_INVALID", 400);
        return pan;
    }
    private byte[] Key(string id, string purpose)
    {
        if (!Enabled || !keys.TryGetValue(id, out var key)) throw new TenantAccessException("PAN_PROTECTION_UNAVAILABLE", 503);
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(purpose + "\n" + id));
    }
    private static byte[] Binding(string tenant, string actor, string customer, long version, string reference) =>
        JsonSerializer.SerializeToUtf8Bytes(new { Tenant = tenant, Actor = actor, Customer = customer, Version = version, Reference = reference });
    public byte[] Protect(string tenant, string actor, string customer, long version, string reference, string pan)
    {
        if (Normalize(pan) != pan) throw new TenantAccessException("PAN_INVALID", 400);
        var key = Key(CurrentKeyId, "monergy.pan.encryption.v1"); var plain = Encoding.ASCII.GetBytes(pan);
        try
        {
            var encrypted = new byte[38]; RandomNumberGenerator.Fill(encrypted.AsSpan(0, 12));
            using var cipher = new AesGcm(key, 16);
            cipher.Encrypt(encrypted.AsSpan(0, 12), plain, encrypted.AsSpan(28), encrypted.AsSpan(12, 16), Binding(tenant, actor, customer, version, reference));
            return encrypted;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    public string Mask(string tenant, string actor, string customer, PanState state)
    {
        var key = Key(state.KeyId ?? "", "monergy.pan.encryption.v1"); var plain = new byte[10];
        try
        {
            if (state.Ciphertext is not { Length: 38 }) throw new CryptographicException();
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(state.Ciphertext.AsSpan(0, 12), state.Ciphertext.AsSpan(28), state.Ciphertext.AsSpan(12, 16), plain,
                Binding(tenant, actor, customer, state.Version, state.VerificationReference ?? ""));
            var pan = Encoding.ASCII.GetString(plain);
            try { if (Normalize(pan) != pan) throw new CryptographicException(); }
            catch (TenantAccessException) { throw new CryptographicException(); }
            return "******" + pan[^4..];
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    public string Fingerprint(string keyId, string tenant, string actor, string customer, CustomerPanChange change)
    {
        var key = Key(keyId, "monergy.pan.operation.v1");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Tenant = tenant, Actor = actor, Customer = customer, change.ExpectedVersion, change.Pan, change.AgreementRevision });
        try { return Convert.ToHexStringLower(HMACSHA256.HashData(key, bytes)); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(bytes); }
    }
}
