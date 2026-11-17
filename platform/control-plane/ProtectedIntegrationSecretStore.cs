using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Monergy.Platform.ControlPlane;

public sealed record ProtectedSecretMetadata(string Reference, bool Configured, DateTimeOffset? UpdatedAt, string? UpdatedBy);

public sealed class FileProtectedIntegrationSecretStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private readonly byte[] key;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);

    public FileProtectedIntegrationSecretStore(string path, string masterKey, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Secret-store path is required.", nameof(path));
        try { key = Convert.FromBase64String(masterKey); }
        catch (FormatException failure) { throw new ArgumentException("Secret-store master key is invalid.", nameof(masterKey), failure); }
        if (key.Length != 32) throw new ArgumentException("Secret-store master key must be 256 bits.", nameof(masterKey));
        this.path = path;
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<ProtectedSecretMetadata> SetAsync(string reference, string secret, string actor, CancellationToken ct)
    {
        ValidateReference(reference);
        if (string.IsNullOrEmpty(secret) || secret.Length > 4096 || secret.Any(char.IsControl))
            throw new InvalidOperationException("INTEGRATION_SECRET_INVALID");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(ct).ConfigureAwait(false);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var plaintext = Encoding.UTF8.GetBytes(secret);
            var cipher = new byte[plaintext.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, tag.Length))
                aes.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(reference));
            CryptographicOperations.ZeroMemory(plaintext);
            var now = clock.GetUtcNow();
            state.Secrets[reference] = new(Convert.ToBase64String(nonce), Convert.ToBase64String(cipher),
                Convert.ToBase64String(tag), now, actor);
            await AtomicJson.SaveAsync(path, state, Json, ct).ConfigureAwait(false);
            return new(reference, true, now, actor);
        }
        finally { gate.Release(); }
    }

    public async Task<ProtectedSecretMetadata> ClearAsync(string reference, string actor, CancellationToken ct)
    {
        ValidateReference(reference);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(ct).ConfigureAwait(false);
            state.Secrets.Remove(reference);
            await AtomicJson.SaveAsync(path, state, Json, ct).ConfigureAwait(false);
            return new(reference, false, clock.GetUtcNow(), actor);
        }
        finally { gate.Release(); }
    }

    public async Task<ProtectedSecretMetadata> MetadataAsync(string reference, CancellationToken ct)
    {
        ValidateReference(reference);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(ct).ConfigureAwait(false);
            return state.Secrets.TryGetValue(reference, out var entry)
                ? new(reference, true, entry.UpdatedAt, entry.UpdatedBy)
                : new(reference, false, null, null);
        }
        finally { gate.Release(); }
    }

    public async Task<string?> ResolveAsync(string reference, CancellationToken ct)
    {
        ValidateReference(reference);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(ct).ConfigureAwait(false);
            if (!state.Secrets.TryGetValue(reference, out var entry)) return null;
            try
            {
                var nonce = Convert.FromBase64String(entry.Nonce);
                var cipher = Convert.FromBase64String(entry.Ciphertext);
                var tag = Convert.FromBase64String(entry.Tag);
                var plaintext = new byte[cipher.Length];
                using (var aes = new AesGcm(key, tag.Length))
                    aes.Decrypt(nonce, cipher, tag, plaintext, Encoding.UTF8.GetBytes(reference));
                try { return Encoding.UTF8.GetString(plaintext); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            catch (Exception failure) when (failure is FormatException or CryptographicException)
            { throw new InvalidOperationException("INTEGRATION_SECRET_UNRESOLVED", failure); }
        }
        finally { gate.Release(); }
    }

    private async Task<State> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return new();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await JsonSerializer.DeserializeAsync<State>(stream, Json, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("INTEGRATION_SECRET_STORE_INVALID");
    }

    private static void ValidateReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or ':' or '-')))
            throw new InvalidOperationException("INTEGRATION_SECRET_REFERENCE_INVALID");
    }

    private sealed class State
    {
        public Dictionary<string, Entry> Secrets { get; init; } = new(StringComparer.Ordinal);
    }
    private sealed record Entry(string Nonce, string Ciphertext, string Tag, DateTimeOffset UpdatedAt, string UpdatedBy);
    public void Dispose() { gate.Dispose(); CryptographicOperations.ZeroMemory(key); }
}
