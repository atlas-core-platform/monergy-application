using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Monergy.Platform.ControlPlane;

public sealed record PlatformCredentialStatus(bool Activated, long CredentialVersion, DateTimeOffset? LockedUntil);
public sealed record PlatformAuthentication(string ActorId, long CredentialVersion);
public sealed record PlatformCredentialEvent(string EventType, string Outcome, long CredentialVersion, DateTimeOffset RecordedAt);

public sealed class FilePlatformCredentialStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const int Iterations = 210_000;
    private readonly string path;
    private readonly string administratorEmail;
    private readonly byte[] testerCredentialHash;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);

    public FilePlatformCredentialStore(string path, string administratorEmail, string testerCredential, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Credential path is required.", nameof(path));
        if (!ValidEmail(administratorEmail)) throw new ArgumentException("Administrator email is invalid.", nameof(administratorEmail));
        if (string.IsNullOrWhiteSpace(testerCredential) || testerCredential.Length < 32)
            throw new ArgumentException("A protected tester credential is required.", nameof(testerCredential));
        this.path = path;
        this.administratorEmail = administratorEmail.Trim().ToLowerInvariant();
        testerCredentialHash = SHA256.HashData(Encoding.UTF8.GetBytes(testerCredential));
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<PlatformCredentialStatus> StatusAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return new(state.Activated, state.CredentialVersion, state.LockedUntil);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<PlatformCredentialEvent>> AuditAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await ReadAsync(cancellationToken).ConfigureAwait(false)).Events.OrderByDescending(item => item.RecordedAt).ToArray(); }
        finally { gate.Release(); }
    }

    public async Task<(string Ticket, DateTimeOffset ExpiresAt)> IssueActivationAsync(
        string email, string testerCredential, CancellationToken cancellationToken)
    {
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(testerCredential ?? string.Empty));
        if (!CryptographicOperations.FixedTimeEquals(testerCredentialHash, supplied))
            throw new InvalidOperationException("PLATFORM_TESTER_AUTHORIZATION_REQUIRED");
        if (!string.Equals(email?.Trim(), administratorEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PLATFORM_IDENTITY_UNAVAILABLE");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            state.ActivationTicketHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
            state.ActivationExpiresAt = clock.GetUtcNow().AddMinutes(20);
            state.ActivationConsumedAt = null;
            state.Events.Add(new("ActivationIssued", "Succeeded", state.CredentialVersion, clock.GetUtcNow()));
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return (ticket, state.ActivationExpiresAt.Value);
        }
        finally { gate.Release(); }
    }

    public async Task ActivateAsync(string ticket, string password, CancellationToken cancellationToken)
    {
        ValidatePassword(password);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var supplied = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket ?? string.Empty)));
            if (state.ActivationTicketHash is null || state.ActivationExpiresAt <= clock.GetUtcNow() ||
                state.ActivationConsumedAt is not null ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(state.ActivationTicketHash), Encoding.ASCII.GetBytes(supplied)))
                throw new InvalidOperationException("PLATFORM_ACTIVATION_TICKET_INVALID");
            var salt = RandomNumberGenerator.GetBytes(32);
            state.PasswordSalt = Convert.ToBase64String(salt);
            state.PasswordHash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(
                password, salt, Iterations, HashAlgorithmName.SHA512, 64));
            state.Activated = true;
            state.CredentialVersion = checked(state.CredentialVersion + 1);
            state.FailedAttempts = 0;
            state.LockedUntil = null;
            state.ActivationConsumedAt = clock.GetUtcNow();
            state.Events.Add(new("ActivationConsumed", "Succeeded", state.CredentialVersion, clock.GetUtcNow()));
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<PlatformAuthentication> AuthenticateAsync(string email, string password, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            if (state.LockedUntil > now) throw new InvalidOperationException("PLATFORM_CREDENTIAL_LOCKED");
            var valid = state.Activated && string.Equals(email?.Trim(), administratorEmail, StringComparison.OrdinalIgnoreCase) &&
                state.PasswordSalt is not null && state.PasswordHash is not null &&
                Verify(password ?? string.Empty, state.PasswordSalt, state.PasswordHash);
            if (!valid)
            {
                state.FailedAttempts++;
                if (state.FailedAttempts >= 5)
                {
                    state.FailedAttempts = 0;
                    state.LockedUntil = now.AddMinutes(15);
                }
                state.Events.Add(new("PasswordSignIn", state.LockedUntil > now ? "Locked" : "Denied", state.CredentialVersion, now));
                await SaveAsync(state, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("PLATFORM_CREDENTIAL_INVALID");
            }
            state.FailedAttempts = 0;
            state.LockedUntil = null;
            state.Events.Add(new("PasswordSignIn", "Succeeded", state.CredentialVersion, now));
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new("A950", state.CredentialVersion);
        }
        finally { gate.Release(); }
    }

    private static bool Verify(string password, string saltValue, string hashValue)
    {
        try
        {
            var salt = Convert.FromBase64String(saltValue);
            var expected = Convert.FromBase64String(hashValue);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }

    private static void ValidatePassword(string password)
    {
        if (password is null || password.Length is < 15 or > 128 ||
            !password.Any(char.IsUpper) || !password.Any(char.IsLower) ||
            !password.Any(char.IsDigit) || !password.Any(c => !char.IsLetterOrDigit(c)))
            throw new InvalidOperationException("PLATFORM_PASSWORD_INVALID");
    }

    private static bool ValidEmail(string value) => value.Length <= 254 && value.Contains('@') &&
        value.All(c => !char.IsControl(c) && !char.IsWhiteSpace(c));

    private async Task<State> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new State { Email = administratorEmail };
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var state = await JsonSerializer.DeserializeAsync<State>(stream, Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("PLATFORM_CREDENTIAL_STATE_INVALID");
        if (!string.Equals(state.Email, administratorEmail, StringComparison.Ordinal))
            throw new InvalidOperationException("PLATFORM_CREDENTIAL_STATE_INVALID");
        return state;
    }

    private async Task SaveAsync(State state, CancellationToken cancellationToken) =>
        await AtomicJson.SaveAsync(path, state, Json, cancellationToken).ConfigureAwait(false);

    private sealed class State
    {
        public string Email { get; init; } = string.Empty;
        public bool Activated { get; set; }
        public long CredentialVersion { get; set; }
        public string? PasswordSalt { get; set; }
        public string? PasswordHash { get; set; }
        public int FailedAttempts { get; set; }
        public DateTimeOffset? LockedUntil { get; set; }
        public string? ActivationTicketHash { get; set; }
        public DateTimeOffset? ActivationExpiresAt { get; set; }
        public DateTimeOffset? ActivationConsumedAt { get; set; }
        public List<PlatformCredentialEvent> Events { get; init; } = [];
    }

    public void Dispose() => gate.Dispose();
}

public sealed record IntegrationProvider(
    string Code, string DisplayName, string Category, bool Enabled, string Scope,
    string? Endpoint, string? ClientId, string? CallbackUri, string? Model,
    IReadOnlyDictionary<string, string> Options, IReadOnlyList<string> SecretReferences,
    string SubscriptionStatus, string Readiness, long Revision, string UpdatedBy, DateTimeOffset UpdatedAt);
public sealed record IntegrationConfigurationSnapshot(long Revision, IReadOnlyList<IntegrationProvider> Providers);
public sealed record IntegrationProviderChange(
    string OperationId, long ExpectedRevision, string Code, bool Enabled, string Scope,
    string? Endpoint, string? ClientId, string? CallbackUri, string? Model,
    IReadOnlyDictionary<string, string>? Options, IReadOnlyList<string>? SecretReferences,
    string SubscriptionStatus, string ActorId);
public sealed record IntegrationAuditReceipt(
    string OperationId, string ProviderCode, long Revision, string ActorId, DateTimeOffset RecordedAt);

public sealed class FileIntegrationConfigurationStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly (string Code, string Name, string Category)[] Catalog =
    [
        ("email", "Email delivery", "Communication"),
        ("google-drive", "Google Drive", "Document connection"),
        ("onedrive", "Microsoft OneDrive", "Document connection"),
        ("ai", "AI provider", "Intelligence"),
        ("identity", "Identity provider", "Identity (future)"),
        ("kyc", "KYC provider", "Identity verification"),
    ];
    private readonly string path;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);

    public FileIntegrationConfigurationStore(string path, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Configuration path is required.", nameof(path));
        this.path = path;
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<IntegrationConfigurationSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Snapshot(await StateAsync(cancellationToken).ConfigureAwait(false)); }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<IntegrationAuditReceipt>> AuditAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await StateAsync(cancellationToken).ConfigureAwait(false)).Receipts.OrderByDescending(x => x.RecordedAt).ToArray(); }
        finally { gate.Release(); }
    }

    public async Task<IntegrationConfigurationSnapshot> ChangeAsync(IntegrationProviderChange change, CancellationToken cancellationToken)
    {
        Validate(change);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await StateAsync(cancellationToken).ConfigureAwait(false);
            var replay = state.Receipts.SingleOrDefault(item => item.OperationId == change.OperationId);
            if (replay is not null)
            {
                if (replay.ProviderCode != change.Code) throw new InvalidOperationException("INTEGRATION_OPERATION_REPLAY_CONFLICT");
                return Snapshot(state);
            }
            if (state.Revision != change.ExpectedRevision) throw new InvalidOperationException("INTEGRATION_CONFIGURATION_CONFLICT");
            var template = Catalog.Single(item => item.Code == change.Code);
            var revision = checked(state.Revision + 1);
            var updatedAt = clock.GetUtcNow();
            var references = (change.SecretReferences ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var readiness = !change.Enabled ? "Disabled" : references.Length == 0 ? "Needs secret reference" : "Configured for readiness check";
            state.Providers.RemoveAll(item => item.Code == change.Code);
            state.Providers.Add(new IntegrationProvider(change.Code, template.Name, template.Category, change.Enabled,
                change.Scope, Null(change.Endpoint), Null(change.ClientId), Null(change.CallbackUri), Null(change.Model),
                new Dictionary<string, string>(change.Options ?? new Dictionary<string, string>(), StringComparer.Ordinal),
                references, change.SubscriptionStatus, readiness, revision, change.ActorId, updatedAt));
            state.Revision = revision;
            state.Receipts.Add(new(change.OperationId, change.Code, revision, change.ActorId, updatedAt));
            await AtomicJson.SaveAsync(path, state, Json, cancellationToken).ConfigureAwait(false);
            return Snapshot(state);
        }
        finally { gate.Release(); }
    }

    private static void Validate(IntegrationProviderChange change)
    {
        if (!Guid.TryParseExact(change.OperationId, "N", out _) || change.ActorId != "A950" || change.ExpectedRevision < 0 ||
            !Catalog.Any(item => item.Code == change.Code) || change.Scope is not ("Platform" or "Organization") ||
            change.SubscriptionStatus is not ("Not configured" or "Configured" or "Pending" or "Unavailable") ||
            !Safe(change.Endpoint, 500) || !Safe(change.ClientId, 200) || !Safe(change.CallbackUri, 500) || !Safe(change.Model, 200) ||
            (change.Options?.Count ?? 0) > 20 || (change.Options?.Any(item => !Identifier(item.Key, 80) || !Safe(item.Value, 500)) ?? false) ||
            (change.SecretReferences?.Count ?? 0) > 10 || (change.SecretReferences?.Any(item => !Identifier(item, 160)) ?? false))
            throw new InvalidOperationException("INTEGRATION_CONFIGURATION_INVALID");
    }

    private static bool Safe(string? value, int maximum) => value is null || value.Length <= maximum && value.All(c => !char.IsControl(c));
    private static bool Identifier(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');
    private static string? Null(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<State> StateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new State();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await JsonSerializer.DeserializeAsync<State>(stream, Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("INTEGRATION_CONFIGURATION_STATE_INVALID");
    }

    private static IntegrationConfigurationSnapshot Snapshot(State state)
    {
        var current = Catalog.Select(template => state.Providers.SingleOrDefault(x => x.Code == template.Code) ??
            new IntegrationProvider(template.Code, template.Name, template.Category, false, "Platform", null, null, null, null,
                new Dictionary<string, string>(), [], "Not configured", "Disabled", 0, "system", DateTimeOffset.UnixEpoch))
            .ToArray();
        return new(state.Revision, current);
    }

    private sealed class State
    {
        public long Revision { get; set; }
        public List<IntegrationProvider> Providers { get; init; } = [];
        public List<IntegrationAuditReceipt> Receipts { get; init; } = [];
    }

    public void Dispose() => gate.Dispose();
}

internal static class AtomicJson
{
    public static async Task SaveAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
