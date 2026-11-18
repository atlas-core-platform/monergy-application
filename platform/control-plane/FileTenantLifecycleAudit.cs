using System.Diagnostics;
using System.Text.Json;

namespace Monergy.Platform.ControlPlane;

public sealed record TenantLifecycleAuditRecord(
    string EventId,
    string TenantId,
    string ActorId,
    string Action,
    string Result,
    long LifecycleRevision,
    DateTimeOffset RecordedAt);

/// <summary>
/// Retained, idempotent control-plane evidence. The schema deliberately has no
/// payload field where a password, database binding, welcome ticket or session
/// credential could be recorded.
/// </summary>
public sealed class FileTenantLifecycleAudit(string filePath) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true };
    private readonly string path = Path.GetFullPath(
        !string.IsNullOrWhiteSpace(filePath) ? filePath : throw new ArgumentException("Audit path is required.", nameof(filePath)));
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task AppendAsync(string eventId, string tenantId, string actorId, string action,
        string result, long revision, DateTimeOffset recordedAt, CancellationToken ct)
    {
        Identifier(eventId, 200);
        Identifier(tenantId, 80);
        Identifier(actorId, 128);
        Identifier(action, 100);
        Identifier(result, 100);
        if (revision < 1 || recordedAt == default)
            throw new InvalidOperationException("TENANT_LIFECYCLE_AUDIT_INVALID");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var lease = await AcquireFileLeaseAsync(ct).ConfigureAwait(false);
            var document = await ReadAsync(ct).ConfigureAwait(false);
            var existing = document.Items.SingleOrDefault(item => item.EventId == eventId);
            var next = new TenantLifecycleAuditRecord(eventId, tenantId, actorId, action, result,
                revision, recordedAt.ToUniversalTime());
            if (existing is not null)
            {
                if (existing != next) throw new InvalidOperationException("TENANT_LIFECYCLE_AUDIT_CONFLICT");
                return;
            }
            document.Items.Add(next);
            await WriteAsync(document, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<TenantLifecycleAuditRecord>> ListAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var lease = await AcquireFileLeaseAsync(ct).ConfigureAwait(false);
            return (await ReadAsync(ct).ConfigureAwait(false)).Items
                .OrderByDescending(item => item.RecordedAt).ToArray();
        }
        finally { gate.Release(); }
    }

    private async Task<FileStream> AcquireFileLeaseAsync(CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            { await Task.Delay(25, ct).ConfigureAwait(false); }
        }
    }

    private async Task<Document> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return new();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<Document>(stream, Json, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("TENANT_LIFECYCLE_AUDIT_INVALID");
    }

    private async Task WriteAsync(Document document, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, Json, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Identifier(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
            throw new InvalidOperationException("TENANT_LIFECYCLE_AUDIT_INVALID");
    }

    public void Dispose() => gate.Dispose();
    private sealed class Document
    {
        public int SchemaVersion { get; set; } = 1;
        public List<TenantLifecycleAuditRecord> Items { get; set; } = [];
    }
}
