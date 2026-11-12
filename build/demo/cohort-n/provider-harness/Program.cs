using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Monergy.Platform;
using G = Monergy.Services.IntegrationGateway.GoogleDrive;
using O = Monergy.Services.IntegrationGateway.OneDrive;

if (args.Length != 2) throw new InvalidOperationException("Expected configuration and protected session input paths.");
var configuration = new ConfigurationBuilder().AddJsonFile(args[0], optional: false).Build();
var input = JsonSerializer.Deserialize<Input>(await File.ReadAllTextAsync(args[1]))
    ?? throw new InvalidOperationException("Protected provider harness input is invalid.");
var google = await GoogleAsync(configuration, input);
var oneDrive = await OneDriveAsync(configuration, input);
Console.WriteLine(JsonSerializer.Serialize(new { GoogleDrive = google, OneDrive = oneDrive }));

static DefaultHttpContext Context(Input input)
{
    var value = new DefaultHttpContext();
    value.Request.Headers["X-Monergy-Tenant"] = input.TenantId;
    value.Request.Headers["X-Monergy-Session"] = input.SessionId;
    return value;
}

static string State(string authorizationUrl)
{
    var query = new Uri(authorizationUrl).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]),
            part => part.Length == 2 ? Uri.UnescapeDataString(part[1]) : "", StringComparer.Ordinal);
    return query.TryGetValue("state", out var state) && state.Length == 64
        ? state : throw new InvalidOperationException("Provider state was not generated.");
}

static async Task<object> GoogleAsync(IConfiguration configuration, Input input)
{
    var context = Context(input);
    using var identity = new CustomerOwnerClient(configuration);
    using var access = new TenantAccessClient(configuration);
    using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 65_536 };
    using var owners = new G.DriveOwners(identity, access, configuration, client);
    using var store = new G.DriveConnectionStore(configuration);
    var provider = new GoogleProvider();
    var app = new G.DriveConnectionApplication(store, owners, provider, configuration);
    var begin = await app.BeginAsync(context, new(true));
    if (!await app.CallbackAsync(State(begin.AuthorizationUrl), "local-provider-code", false, default))
        throw new InvalidOperationException("Google callback did not complete.");
    var connected = await app.StatusAsync(context);
    var selected = await app.SelectFolderAsync(context, new(connected.Version, "folder-google", null, true));
    var files = await app.ListAsync(context, false, null);
    if (files.Items.Length != 1 || files.NextPageToken is not null) throw new InvalidOperationException("Google bounded file page is invalid.");
    var receipt = await app.ImportAsync(context, new(selected.Version, "google-file", "10"));
    var replay = await app.ImportAsync(context, new(selected.Version, "google-file", "10"));
    if (receipt != replay || provider.Downloads != 1) throw new InvalidOperationException("Google import replay was not exactly-once.");
    var disconnected = await app.DisconnectAsync(context, new(selected.Version));
    if (!provider.Revoked || disconnected.Imports.Count != 1) throw new InvalidOperationException("Google disconnect did not preserve import history.");
    return new { receipt.DocumentId, receipt.DocumentVersionId, receipt.Sha256, receipt.FileVersion,
        Replay = "EXACT", Disconnect = disconnected.Status, ProviderRevoke = provider.Revoked };
}

static async Task<object> OneDriveAsync(IConfiguration configuration, Input input)
{
    var context = Context(input);
    using var identity = new CustomerOwnerClient(configuration);
    using var access = new TenantAccessClient(configuration);
    using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 65_536 };
    using var owners = new O.DriveOwners(identity, access, configuration, client);
    using var store = new O.DriveConnectionStore(configuration);
    var provider = new OneDriveProvider();
    var app = new O.DriveConnectionApplication(store, owners, provider, configuration);
    var begin = await app.BeginAsync(context, new(true));
    if (!await app.CallbackAsync(State(begin.AuthorizationUrl), "local-provider-code", false, default))
        throw new InvalidOperationException("OneDrive callback did not complete.");
    var connected = await app.StatusAsync(context);
    var selected = await app.SelectFolderAsync(context, new(connected.Version, "folder-onedrive", null, true));
    var files = await app.ListAsync(context, false, null);
    if (files.Items.Length != 1 || files.NextPageToken is not null) throw new InvalidOperationException("OneDrive bounded file page is invalid.");
    var receipt = await app.ImportAsync(context, new(selected.Version, "onedrive-file", OneDriveProvider.Version));
    var replay = await app.ImportAsync(context, new(selected.Version, "onedrive-file", OneDriveProvider.Version));
    if (receipt != replay || provider.Downloads != 1) throw new InvalidOperationException("OneDrive import replay was not exactly-once.");
    var disconnected = await app.DisconnectAsync(context, new(selected.Version));
    if (!provider.Revoked || disconnected.Imports.Count != 1) throw new InvalidOperationException("OneDrive disconnect did not preserve import history.");
    return new { receipt.DocumentId, receipt.DocumentVersionId, receipt.Sha256, receipt.FileVersion,
        Replay = "EXACT", Disconnect = disconnected.Status, LocalDisconnect = true };
}

sealed record Input(string TenantId, string SessionId);

sealed class GoogleProvider : G.IGoogleDriveProvider
{
    private static readonly byte[] Content = "Cohort N Google Drive physical import"u8.ToArray();
    private const string Md5 = "8137303c8adf32890564c0400ef22e7f";
    public int Downloads { get; private set; }
    public bool Revoked { get; private set; }
    public Task<G.DriveTokens> ExchangeAsync(G.DriveConfiguration config, string code, CancellationToken ct) =>
        Task.FromResult(new G.DriveTokens("local-access", "local-refresh", DateTimeOffset.UtcNow.AddMinutes(30)));
    public Task<G.DriveTokens> RefreshAsync(G.DriveConfiguration config, G.DriveTokens tokens, CancellationToken ct) =>
        Task.FromResult(tokens with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30) });
    public Task<G.DriveAccount> AccountAsync(G.DriveTokens tokens, CancellationToken ct) =>
        Task.FromResult(new G.DriveAccount("local-google-account", "LOCAL Google contract account"));
    public Task<G.DrivePage> ListAsync(G.DriveTokens tokens, string? folder, string? page, CancellationToken ct) =>
        Task.FromResult(new G.DrivePage([File], null));
    public Task<G.DriveFile> FileAsync(G.DriveTokens tokens, string id, CancellationToken ct) => Task.FromResult(id == "folder-google"
        ? new G.DriveFile(id, "Monergy", G.GoogleDriveProvider.FolderType, "1", 0, null, [], false, false) : File);
    public Task<byte[]> DownloadAsync(G.DriveTokens tokens, G.DriveFile file, CancellationToken ct)
    { Downloads++; return Task.FromResult(Content); }
    public Task RevokeAsync(G.DriveTokens tokens, CancellationToken ct) { Revoked = true; return Task.CompletedTask; }
    private static G.DriveFile File => new("google-file", "google-statement.txt", "text/plain", "10", Content.Length, Md5,
        ["folder-google"], false, true);
}

sealed class OneDriveProvider : O.IOneDriveProvider
{
    public const string Version = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly byte[] Content = "Cohort N OneDrive physical import"u8.ToArray();
    private static readonly string QuickXor = Convert.ToBase64String(new byte[20]);
    public int Downloads { get; private set; }
    public bool Revoked { get; private set; }
    public Task<O.DriveTokens> ExchangeAsync(O.DriveConfiguration config, string code, CancellationToken ct) =>
        Task.FromResult(new O.DriveTokens("local-access", "local-refresh", DateTimeOffset.UtcNow.AddMinutes(30)));
    public Task<O.DriveTokens> RefreshAsync(O.DriveConfiguration config, O.DriveTokens tokens, CancellationToken ct) =>
        Task.FromResult(tokens with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30) });
    public Task<O.DriveAccount> AccountAsync(O.DriveTokens tokens, CancellationToken ct) =>
        Task.FromResult(new O.DriveAccount("local-microsoft-account", "LOCAL Microsoft contract account"));
    public Task<O.DrivePage> ListAsync(O.DriveTokens tokens, string? folder, string? page, CancellationToken ct) =>
        Task.FromResult(new O.DrivePage([File], null));
    public Task<O.DriveFile> FileAsync(O.DriveTokens tokens, string id, CancellationToken ct) => Task.FromResult(id == "folder-onedrive"
        ? new O.DriveFile(id, "Monergy", O.OneDriveProvider.FolderType, "1", 0, null, [], false, false) : File);
    public Task<byte[]> DownloadAsync(O.DriveTokens tokens, O.DriveFile file, CancellationToken ct)
    { Downloads++; return Task.FromResult(Content); }
    public Task RevokeAsync(O.DriveTokens tokens, CancellationToken ct) { Revoked = true; return Task.CompletedTask; }
    private static O.DriveFile File => new("onedrive-file", "onedrive-statement.txt", "text/plain", Version, Content.Length, QuickXor,
        ["folder-onedrive"], false, true);
}
