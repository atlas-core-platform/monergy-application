using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Platform;

using Monergy.Contracts;
namespace Monergy.Services.AiIntelligence.Qa;

public sealed record QaStoredRecord(string TenantId, string CustomerId, string ActorId, string RequestHash,
    QaAnswer Answer, IReadOnlyList<string> SearchRecordIds, IReadOnlyList<QaClaim> RetrievedClaims,
    string PlanJson, string SelectionJson, string PlanModel, bool AllowModelProcessing, string PermissionNotice, string? SourceDocumentVersionId = null, string? SourcePositionId = null);

// LOCAL/CI single-writer owner storage. A process semaphore plus an exclusive
// file lease prevents split writes. Every persisted byte (including questions,
// model metadata and provenance) is authenticated encryption under an operator key.
public sealed class QaStore : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly string directory;
    private readonly byte[] key;
    private List<QaStoredRecord>? activeRecords;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public QaStore(IConfiguration configuration)
    {
        directory = Path.GetFullPath(TenantAccessIntegration.Required(configuration, "Monergy:FinancialQa:StoreDirectory"));
        key = Convert.FromBase64String(TenantAccessIntegration.Required(configuration, "Monergy:FinancialQa:EncryptionKey"));
        if (key.Length != 32) throw new InvalidOperationException("Financial Q&A requires a 256-bit storage key.");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public async Task<T> ExecuteAsync<T>(Func<List<QaStoredRecord>, Task<T>> operation, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var lease = new FileStream(Path.Combine(directory, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var path = Path.Combine(directory, "answers.enc");
            var records = File.Exists(path) ? Decode(await File.ReadAllBytesAsync(path, ct)) : [];
            activeRecords = records;
            try { return await operation(records); }
            finally { try { await CheckpointAsync(records); } finally { activeRecords = null; } }

        }
        catch (IOException) { throw new TenantBoundaryException("AI_STORE_UNAVAILABLE", 503); }
        catch (CryptographicException) { throw new TenantBoundaryException("AI_STORE_UNAVAILABLE", 503); }
        finally { gate.Release(); }
    }
    public async Task CheckpointAsync(List<QaStoredRecord> records)
    {
        if (!ReferenceEquals(activeRecords, records)) throw new InvalidOperationException("Drive checkpoint requires the current store lease.");
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var bytes = Encode(records);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await file.WriteAsync(bytes, CancellationToken.None); file.Flush(true);
            }
            File.Move(temporary, Path.Combine(directory, "answers.enc"), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private byte[] Encode(List<QaStoredRecord> records)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(records, Json);
        if (plain.Length > 32 * 1024 * 1024 - 28) { CryptographicOperations.ZeroMemory(plain); throw new TenantBoundaryException("AI_HISTORY_CAPACITY", 409); }
        var output = new byte[28 + plain.Length]; RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(28), output.AsSpan(12, 16), "monergy-financial-qa-v1"u8);
        CryptographicOperations.ZeroMemory(plain); return output;
    }
    private List<QaStoredRecord> Decode(byte[] encrypted)
    {
        if (encrypted.Length < 28 || encrypted.Length > 32 * 1024 * 1024) throw new CryptographicException();
        var plain = new byte[encrypted.Length - 28]; using var aes = new AesGcm(key, 16);
        aes.Decrypt(encrypted.AsSpan(0, 12), encrypted.AsSpan(28), encrypted.AsSpan(12, 16), plain, "monergy-financial-qa-v1"u8);
        try { return JsonSerializer.Deserialize<List<QaStoredRecord>>(plain, Json) ?? throw new CryptographicException(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Dispose() { gate.Dispose(); CryptographicOperations.ZeroMemory(key); }
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
