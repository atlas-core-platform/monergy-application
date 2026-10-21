using System.Security.Cryptography;
using Monergy.Platform;

namespace Monergy.Services.IntegrationGateway.GoogleDrive;

public sealed record DriveView(string TenantId, string CustomerId, string Provider, string Status,
    string? ConnectionId, long Version, string? FolderName, DateTimeOffset? RefreshedAt,
    IReadOnlyList<DriveImportReceipt> Imports, string DataDurability = "Connection saved; imported documents use temporary local evidence storage", string? AccountLabel = null);
public sealed record DriveBegin(bool AcceptReadAccess);
public sealed record DriveSelection(long Version, string Id, string? FileVersion, bool AcceptFolderImport = false);
public sealed record DriveRevision(long Version);
public sealed record DriveBeginResult(string AuthorizationUrl);

public sealed class DriveConnectionApplication(DriveConnectionStore store, IDriveOwners owners,
    IGoogleDriveProvider provider, IConfiguration configuration)
{
    public async Task<DriveView> StatusAsync(HttpContext http)
    {
        var customer = await owners.AuthorizeAsync(http);
        return await store.ExecuteAsync(async records =>
        {
            var connection = Find(records, customer);
            if (connection is null) return View(customer, null, Config(customer.Session.TenantId) is null ? "Not configured" : "Configured");
            RequireOwner(connection, customer);
            var status = connection.Status;
            if (status is not ("Disconnected" or "Disconnected; Google revocation unconfirmed"))
            {
                if (Config(connection.TenantId)?.Revision != connection.ConfigurationRevision) status = "Needs attention";
                else if (connection.PendingUntil < DateTimeOffset.UtcNow && connection.Tokens is null) status = "Permission expired";
                else if (!Allowed(await owners.ConsentAsync(http, connection, null, null), connection)) status = "Permission expired";
                else if (connection.Tokens is not null)
                {
                    try
                    {
                        _ = await CurrentAsync(http, connection);
                        var account = await provider.AccountAsync(connection.Tokens!, http.RequestAborted);
                        if (DriveConnectionStore.Hash(account.Id) != connection.AccountHash) status = "Needs attention";
                        else { connection.AccountLabel = account.Label; connection.RefreshedAt = DateTimeOffset.UtcNow; }
                    }
                    catch (Exception error) when (error is TenantBoundaryException or HttpRequestException or TaskCanceledException)
                    { status = "Needs attention"; }
                }
            }
            return View(customer, connection, status);
        }, http.RequestAborted);
    }
    public async Task<DriveBeginResult> BeginAsync(HttpContext http, DriveBegin input)
    {
        if (!input.AcceptReadAccess) throw new TenantBoundaryException("DRIVE_EXPLICIT_PERMISSION_REQUIRED", 400);
        var customer = await owners.AuthorizeAsync(http);
        return await store.ExecuteAsync(async records =>
        {
            var config = Config(customer.Session.TenantId) ?? throw new TenantBoundaryException("DRIVE_NOT_CONFIGURED", 409);
            var previous = Find(records, customer);
            if (previous is not null && !previous.Status.StartsWith("Disconnected", StringComparison.Ordinal))
                throw new TenantBoundaryException("DRIVE_DISCONNECT_FIRST", 409);
            if (records.Count >= 10000 && previous is null) throw new TenantBoundaryException("DRIVE_CAPACITY_REACHED", 503);
            var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var connection = new DriveConnection
            {
                TenantId = customer.Session.TenantId, CustomerId = customer.Workspace.CustomerId,
                ActorId = customer.Session.ActorId, OwnerVersion = customer.Workspace.Version,
                ConfigurationRevision = config.Revision, StateHash = DriveConnectionStore.Hash(state),
                PendingSession = customer.Session.AuthenticationContextId, PendingUntil = DateTimeOffset.UtcNow.AddMinutes(10)
            };
            var consent = await owners.ConsentAsync(http, connection, "Granted", null);
            if (!consent.Allowed || consent.Version != 1) throw new TenantBoundaryException("CONSENT_REQUIRED", 403);
            connection.ConsentVersion = consent.Version;
            if (previous is not null) connection.Imports.AddRange(previous.Imports);
            if (previous is not null) records.Remove(previous);
            records.Add(connection);
            return new DriveBeginResult(GoogleDriveProvider.AuthorizationUrl(config, state));
        }, http.RequestAborted);
    }
    public Task<bool> CallbackAsync(string state, string? code, bool denied, CancellationToken ct)
    {
        if (state.Length != 64 || !state.All(char.IsAsciiHexDigit) || code?.Length > 4096)
            throw new TenantBoundaryException("DRIVE_CALLBACK_INVALID", 400);
        return store.ExecuteAsync(async records =>
        {
            var connection = records.SingleOrDefault(item => item.StateHash == DriveConnectionStore.Hash(state))
                ?? throw new TenantBoundaryException("DRIVE_CALLBACK_INVALID", 400);
            var session = connection.PendingSession;
            connection.StateHash = null; connection.PendingSession = null; connection.Status = "Needs attention"; connection.Version++;
            await store.CheckpointAsync(records);
            if (connection.PendingUntil <= DateTimeOffset.UtcNow || session is null || denied || string.IsNullOrWhiteSpace(code)) return false;
            var http = new DefaultHttpContext { RequestAborted = ct };
            http.Request.Headers["X-Monergy-Tenant"] = connection.TenantId;
            http.Request.Headers["X-Monergy-Session"] = session;
            var customer = await owners.AuthorizeAsync(http); RequireOwner(connection, customer);
            var config = await CurrentAsync(http, connection, false);
            var tokens = await provider.ExchangeAsync(config, code, ct);
            // OAuth has no application session in its callback. The encrypted,
            // one-use state binds it to the initiating owner session instead.
            try
            {
                _ = await CurrentAsync(http, connection, false);
                var account = await provider.AccountAsync(tokens, ct);
                connection.AccountHash = DriveConnectionStore.Hash(account.Id); connection.AccountLabel = account.Label;
                connection.Tokens = tokens; connection.PendingUntil = null; connection.Status = "Choose folder";
                return true;
            }
            catch
            {
                try { await provider.RevokeAsync(tokens, CancellationToken.None); } catch (Exception) { /* no tokens retained or activated */ }
                throw;
            }
        }, ct);
    }
    public async Task<DrivePage> ListAsync(HttpContext http, bool folders, string? page)
    {
        var customer = await owners.AuthorizeAsync(http);
        return await store.ExecuteAsync(async records =>
        {
            var connection = Required(records, customer);
            _ = await CurrentAsync(http, connection);
            if (!folders) await RequireFolderAsync(connection, http.RequestAborted);
            var result = await provider.ListAsync(connection.Tokens!, folders ? null : connection.FolderId, page, http.RequestAborted);
            _ = await CurrentAsync(http, connection);
            connection.RefreshedAt = DateTimeOffset.UtcNow;
            return result;
        }, http.RequestAborted);
    }
    public async Task<DriveView> BindAsync(HttpContext http, DriveSelection input)
    {
        if (!input.AcceptFolderImport || input.FileVersion is not null) throw new TenantBoundaryException("DRIVE_FOLDER_PERMISSION_REQUIRED", 400);
        GoogleDriveProvider.Id(input.Id);
        var customer = await owners.AuthorizeAsync(http);
        return await store.ExecuteAsync(async records =>
        {
            var connection = Required(records, customer); Revision(connection, input.Version);
            _ = await CurrentAsync(http, connection);
            var folder = await provider.FileAsync(connection.Tokens!, input.Id, http.RequestAborted);
            if (folder.Trashed || folder.MimeType != GoogleDriveProvider.FolderType) throw new TenantBoundaryException("DRIVE_FOLDER_UNAVAILABLE", 409);
            _ = await CurrentAsync(http, connection);
            var consent = await owners.ConsentAsync(http, connection, "FolderBound", folder.Id);
            if (!consent.Allowed || consent.FolderId != folder.Id || consent.Version != connection.ConsentVersion + 1)
                throw new TenantBoundaryException("CONSENT_REQUIRED", 403);
            connection.FolderId = folder.Id; connection.FolderName = folder.Name;
            connection.ConsentVersion = consent.Version; connection.Version++; connection.Status = "Connected";
            return View(customer, connection, connection.Status);
        }, http.RequestAborted);
    }
    public async Task<DriveImportReceipt> ImportAsync(HttpContext http, DriveSelection input)
    {
        GoogleDriveProvider.Id(input.Id);
        if (input.FileVersion is null || !ulong.TryParse(input.FileVersion, out _)) throw new TenantBoundaryException("DRIVE_VERSION_REQUIRED", 400);
        var customer = await owners.AuthorizeAsync(http);
        return await store.ExecuteAsync(async records =>
        {
            var connection = Required(records, customer); Revision(connection, input.Version);
            _ = await CurrentAsync(http, connection);
            await RequireFolderAsync(connection, http.RequestAborted);
            var file = await provider.FileAsync(connection.Tokens!, input.Id, http.RequestAborted);
            ValidateFile(file, connection, input.FileVersion);
            // Replay only after current permission, folder membership and exact
            // provider version checks. Reconnection never inherits old consent.
            var receipt = connection.Imports.SingleOrDefault(item => item.ConnectionId == connection.Id && item.FileId == file.Id && item.FileVersion == file.Version);
            if (receipt is not null) return receipt;
            if (connection.Imports.Count >= 1000) throw new TenantBoundaryException("DRIVE_IMPORT_LIMIT_REACHED", 409);
            var content = await provider.DownloadAsync(connection.Tokens!, file, http.RequestAborted);
            var after = await provider.FileAsync(connection.Tokens!, input.Id, http.RequestAborted);
            ValidateFile(after, connection, input.FileVersion);
            if (after.Md5Checksum != file.Md5Checksum || after.Name != file.Name || after.MimeType != file.MimeType || after.Size != file.Size)
                throw new TenantBoundaryException("DRIVE_FILE_CHANGED", 409);
            await Revalidate();
            connection.ImportTimes.TryAdd(file.Id + ":" + file.Version, DateTimeOffset.UtcNow);
            await store.CheckpointAsync(records);
            receipt = await owners.ImportAsync(http, customer, connection, file, content, Revalidate);
            connection.Imports.Add(receipt); connection.RefreshedAt = DateTimeOffset.UtcNow;
            return receipt;
            async Task Revalidate()
            {
                _ = await CurrentAsync(http, connection);
                var currentFile = await provider.FileAsync(connection.Tokens!, input.Id, http.RequestAborted);
                ValidateFile(currentFile, connection, input.FileVersion);
                await RequireFolderAsync(connection, http.RequestAborted);
            }
        }, http.RequestAborted);
    }
    public async Task<DriveView> DisconnectAsync(HttpContext http, DriveRevision input)
    {
        var customer = await owners.AuthorizeAsync(http);
        return await store.ExecuteAsync(async records =>
        {
            var connection = Required(records, customer); Revision(connection, input.Version);
            var tokens = connection.Tokens;
            // Local access is removed even if the consent/provider owner is down.
            // Tokens are erased, and subsequent operations require reconnect.
            connection.Tokens = null; connection.StateHash = null; connection.PendingSession = null; connection.PendingUntil = null;
            connection.Status = "Disconnected; Google revocation unconfirmed"; connection.Version++;
            await store.CheckpointAsync(records);
            try
            {
                var permission = await owners.ConsentAsync(http, connection, null, null);
                if (permission.Allowed)
                {
                    connection.ConsentVersion = permission.Version;
                    var revoked = await owners.ConsentAsync(http, connection, "Revoked", null);
                    connection.ConsentVersion = revoked.Version;
                }
                if (tokens is not null) await provider.RevokeAsync(tokens, http.RequestAborted);
                connection.Status = "Disconnected";
            }
            catch (Exception error) when (error is TenantBoundaryException or HttpRequestException or TaskCanceledException) { }
            return View(customer, connection, connection.Status);
        }, http.RequestAborted);
    }
    private async Task<DriveConfiguration> CurrentAsync(HttpContext http, DriveConnection connection, bool requireTokens = true)
    {
        var current = await owners.AuthorizeAsync(http); RequireOwner(connection, current);
        var config = Config(connection.TenantId);
        if (config is null || config.Revision != connection.ConfigurationRevision) throw new TenantBoundaryException("DRIVE_CONFIGURATION_CHANGED", 409);
        if (!Allowed(await owners.ConsentAsync(http, connection, null, null), connection)) throw new TenantBoundaryException("DRIVE_PERMISSION_REQUIRED", 403);
        if (requireTokens)
        {
            if (connection.Tokens is null || connection.Status.StartsWith("Disconnected", StringComparison.Ordinal))
                throw new TenantBoundaryException("DRIVE_RECONNECT_REQUIRED", 409);
            if (connection.Tokens.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(1))
                connection.Tokens = await provider.RefreshAsync(config, connection.Tokens, http.RequestAborted);
        }
        return config;
    }
    private async Task RequireFolderAsync(DriveConnection connection, CancellationToken ct)
    {
        if (connection.FolderId is null) throw new TenantBoundaryException("DRIVE_FOLDER_REQUIRED", 409);
        var folder = await provider.FileAsync(connection.Tokens!, connection.FolderId, ct);
        if (folder.Trashed || folder.MimeType != GoogleDriveProvider.FolderType) throw new TenantBoundaryException("DRIVE_FOLDER_UNAVAILABLE", 409);
    }
    private DriveConfiguration? Config(string tenant)
    {
        var section = configuration.GetSection("Monergy:GoogleDrive:Tenants:" + tenant);
        if (section["Enabled"] != "true") return null;
        var result = new DriveConfiguration(section["ClientId"] ?? "", section["ClientSecret"] ?? "", section["RedirectUri"] ?? "");
        result.Validate(); return result;
    }
    private static bool Allowed(DriveConsentDecision permission, DriveConnection connection) => permission.Allowed &&
        permission.Version == connection.ConsentVersion && permission.FolderId == connection.FolderId && permission.ExpiresAt > DateTimeOffset.UtcNow;
    private static void ValidateFile(DriveFile file, DriveConnection connection, string version)
    {
        GoogleDriveProvider.RequireImportable(file);
        if (file.Version != version || connection.FolderId is null || !file.Parents.Contains(connection.FolderId))
            throw new TenantBoundaryException("DRIVE_FILE_CHANGED", 409);
    }
    private static void Revision(DriveConnection connection, long version)
    { if (connection.Version != version) throw new TenantBoundaryException("DRIVE_CONNECTION_CHANGED", 409); }
    private static DriveConnection? Find(List<DriveConnection> records, DriveCustomer customer) =>
        records.SingleOrDefault(item => item.TenantId == customer.Session.TenantId && item.CustomerId == customer.Workspace.CustomerId);
    private static DriveConnection Required(List<DriveConnection> records, DriveCustomer customer)
    { var connection = Find(records, customer) ?? throw new TenantBoundaryException("DRIVE_CONNECTION_REQUIRED", 409); RequireOwner(connection, customer); return connection; }
    private static void RequireOwner(DriveConnection connection, DriveCustomer customer)
    {
        if (connection.TenantId != customer.Session.TenantId || connection.CustomerId != customer.Workspace.CustomerId ||
            connection.ActorId != customer.Session.ActorId || connection.OwnerVersion != customer.Workspace.Version)
            throw new TenantBoundaryException("DRIVE_OWNER_CHANGED", 403);
    }
    private static DriveView View(DriveCustomer customer, DriveConnection? connection, string status) =>
        new(customer.Session.TenantId, customer.Workspace.CustomerId, "Google Drive", status, connection?.Id,
            connection?.Version ?? 0, connection?.FolderName, connection?.RefreshedAt, connection?.Imports.ToArray() ?? [], AccountLabel: connection?.AccountLabel);
}
