using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Platform;

namespace Monergy.Services.IntegrationGateway.GoogleDrive;

public sealed record DriveImportReceipt(string ConnectionId, string FileId, string FileVersion, string Name, string DocumentId,
    string DocumentVersionId, string Sha256, DateTimeOffset ImportedAt);
public sealed class DriveConnection
{
    public string TenantId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public long OwnerVersion { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ConfigurationRevision { get; set; } = "";
    public long Version { get; set; } = 1;
    public long ConsentVersion { get; set; }
    public string? StateHash { get; set; }
    public string? PendingSession { get; set; }
    public DateTimeOffset? PendingUntil { get; set; }
    public DriveTokens? Tokens { get; set; }
    public string? AccountHash { get; set; }
    public string? AccountLabel { get; set; }
    public string? FolderId { get; set; }
    public string? FolderName { get; set; }
    public string Status { get; set; } = "Waiting permission";
    public DateTimeOffset? RefreshedAt { get; set; }
    public Dictionary<string, DateTimeOffset> ImportTimes { get; set; } = new();
    public List<DriveImportReceipt> Imports { get; set; } = [];
}

// LOCAL/CI single-writer owner storage. A process semaphore plus an exclusive
// file lease prevents split writes. Every persisted byte (including OAuth state,
// tokens and provenance) is authenticated encryption under an operator key.
public sealed class DriveConnectionStore : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly string directory;
    private readonly byte[] key;
    private List<DriveConnection>? activeRecords;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public DriveConnectionStore(IConfiguration configuration)
    {
        directory = Path.GetFullPath(TenantAccessIntegration.Required(configuration, "Monergy:GoogleDrive:StoreDirectory"));
        key = Convert.FromBase64String(TenantAccessIntegration.Required(configuration, "Monergy:GoogleDrive:EncryptionKey"));
        if (key.Length != 32) throw new InvalidOperationException("Google Drive requires a 256-bit storage key.");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public async Task<T> ExecuteAsync<T>(Func<List<DriveConnection>, Task<T>> operation, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var lease = new FileStream(Path.Combine(directory, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var path = Path.Combine(directory, "connections.enc");
            var records = File.Exists(path) ? Decode(await File.ReadAllBytesAsync(path, ct)) : [];
            activeRecords = records;
            try { return await operation(records); }
            finally { try { await CheckpointAsync(records); } finally { activeRecords = null; } }

        }
        catch (IOException) { throw new TenantBoundaryException("DRIVE_STORE_UNAVAILABLE", 503); }
        catch (CryptographicException) { throw new TenantBoundaryException("DRIVE_STORE_UNAVAILABLE", 503); }
        finally { gate.Release(); }
    }
    public async Task CheckpointAsync(List<DriveConnection> records)
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
            File.Move(temporary, Path.Combine(directory, "connections.enc"), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private byte[] Encode(List<DriveConnection> records)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(records, Json);
        var output = new byte[28 + plain.Length]; RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(28), output.AsSpan(12, 16), "monergy-google-drive-v1"u8);
        CryptographicOperations.ZeroMemory(plain); return output;
    }
    private List<DriveConnection> Decode(byte[] encrypted)
    {
        if (encrypted.Length < 28 || encrypted.Length > 32 * 1024 * 1024) throw new CryptographicException();
        var plain = new byte[encrypted.Length - 28]; using var aes = new AesGcm(key, 16);
        aes.Decrypt(encrypted.AsSpan(0, 12), encrypted.AsSpan(28), encrypted.AsSpan(12, 16), plain, "monergy-google-drive-v1"u8);
        try { return JsonSerializer.Deserialize<List<DriveConnection>>(plain, Json) ?? throw new CryptographicException(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Dispose() { gate.Dispose(); CryptographicOperations.ZeroMemory(key); }
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
