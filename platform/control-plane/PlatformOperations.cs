using System.Text.Json;

namespace Monergy.Platform.ControlPlane;

public enum PlatformOperationalPosture
{
    Normal,
    Investigating,
    Maintenance,
}

public sealed record PlatformServicePosture(
    string ServiceId,
    PlatformOperationalPosture Posture,
    string Note,
    string ChangedBy,
    DateTimeOffset ChangedAt,
    long Version);

public sealed record PlatformOperationReceipt(
    string OperationId,
    string ServiceId,
    PlatformOperationalPosture Posture,
    string Note,
    string ActorId,
    DateTimeOffset RecordedAt,
    long Version);

public sealed record PlatformOperationsSnapshot(
    long Version,
    IReadOnlyList<PlatformServicePosture> Services,
    IReadOnlyList<PlatformOperationReceipt> Receipts);

public sealed record PlatformPostureChange(
    string OperationId,
    string ServiceId,
    PlatformOperationalPosture Posture,
    string Note,
    string ActorId,
    long ExpectedVersion);

public sealed class FilePlatformOperationsStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly string path;
    private readonly HashSet<string> serviceIds;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);

    public FilePlatformOperationsStore(
        string path,
        IEnumerable<string> serviceIds,
        TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Operations path is required.", nameof(path));
        this.path = path;
        this.serviceIds = serviceIds.ToHashSet(StringComparer.Ordinal);
        if (this.serviceIds.Count == 0) throw new ArgumentException("At least one service is required.", nameof(serviceIds));
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<PlatformOperationsSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Snapshot(await ReadStateAsync(cancellationToken).ConfigureAwait(false)); }
        finally { gate.Release(); }
    }

    public async Task<PlatformOperationsSnapshot> ChangeAsync(
        PlatformPostureChange change,
        CancellationToken cancellationToken)
    {
        Validate(change);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            var replay = state.Receipts.SingleOrDefault(item => item.OperationId == change.OperationId);
            if (replay is not null)
            {
                if (replay.ServiceId != change.ServiceId || replay.Posture != change.Posture ||
                    replay.Note != change.Note || replay.ActorId != change.ActorId)
                    throw new InvalidOperationException("PLATFORM_OPERATION_REPLAY_CONFLICT");
                return Snapshot(state);
            }
            if (state.Version != change.ExpectedVersion)
                throw new InvalidOperationException("PLATFORM_CONFIGURATION_CONFLICT");

            var version = checked(state.Version + 1);
            var recordedAt = clock.GetUtcNow();
            var posture = new PlatformServicePosture(
                change.ServiceId, change.Posture, change.Note, change.ActorId, recordedAt, version);
            state.Services.RemoveAll(item => item.ServiceId == change.ServiceId);
            state.Services.Add(posture);
            state.Receipts.Add(new PlatformOperationReceipt(
                change.OperationId, change.ServiceId, change.Posture, change.Note,
                change.ActorId, recordedAt, version));
            state.Version = version;
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return Snapshot(state);
        }
        finally { gate.Release(); }
    }

    private void Validate(PlatformPostureChange change)
    {
        if (!Guid.TryParseExact(change.OperationId, "N", out _))
            throw new InvalidOperationException("PLATFORM_OPERATION_INVALID");
        if (!serviceIds.Contains(change.ServiceId))
            throw new InvalidOperationException("PLATFORM_SERVICE_NOT_FOUND");
        if (!Enum.IsDefined(change.Posture) || change.ExpectedVersion < 0 ||
            change.ActorId != "A950" || string.IsNullOrWhiteSpace(change.Note) || change.Note.Length > 500)
            throw new InvalidOperationException("PLATFORM_OPERATION_INVALID");
    }

    private async Task<State> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new State();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var state = await JsonSerializer.DeserializeAsync<State>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return state ?? throw new InvalidOperationException("PLATFORM_OPERATIONS_STATE_INVALID");
    }

    private async Task SaveAsync(State state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static PlatformOperationsSnapshot Snapshot(State state) => new(
        state.Version,
        state.Services.OrderBy(item => item.ServiceId, StringComparer.Ordinal).ToArray(),
        state.Receipts.OrderByDescending(item => item.RecordedAt).ToArray());

    private sealed class State
    {
        public long Version { get; set; }
        public List<PlatformServicePosture> Services { get; init; } = [];
        public List<PlatformOperationReceipt> Receipts { get; init; } = [];
    }

    public void Dispose() => gate.Dispose();
}
