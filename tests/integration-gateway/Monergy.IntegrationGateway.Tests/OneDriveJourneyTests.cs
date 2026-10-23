using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Monergy.Platform;
using Monergy.Services.IntegrationGateway.OneDrive;
using Xunit;

namespace Monergy.IntegrationGateway.Tests;

public sealed class OneDriveJourneyTests
{
    [Fact]
    public async Task AuthorizeBindImportReplayAndDisconnectPreserveExactProvenance()
    {
        using var fixture = new Fixture();
        var view = await fixture.Connect();
        var input = new DriveSelection(view.Version, "file-1", new string('a',64));
        var first = await fixture.App.ImportAsync(fixture.Http, input);
        var replay = await fixture.App.ImportAsync(fixture.Http, input);
        Assert.Equal(first, replay); Assert.Equal(1, fixture.Owners.Imports);
        Assert.Equal("file-1", first.FileId); Assert.Equal(new string('a',64), first.FileVersion);
        var disconnected = await fixture.App.DisconnectAsync(fixture.Http, new(view.Version));
        Assert.Equal("Disconnected", disconnected.Status); Assert.Single(disconnected.Imports);
        Assert.True(fixture.Provider.Revoked);
        await Assert.ThrowsAsync<TenantBoundaryException>(() => fixture.App.ImportAsync(fixture.Http, input));
        var reconnect = await fixture.App.BeginAsync(fixture.Http, new(true));
        Assert.NotNull(reconnect.AuthorizationUrl);
        Assert.Single((await fixture.App.StatusAsync(fixture.Http)).Imports);
    }
    [Fact]
    public async Task CallbackIsOneUseAndBoundToOriginalOwnerSession()
    {
        using var fixture = new Fixture(); var state = await fixture.Begin();
        fixture.Owners.Actor = "other";
        await Assert.ThrowsAsync<TenantBoundaryException>(() => fixture.App.CallbackAsync(state, "code", false, default));
        fixture.Owners.Actor = "A100";
        await Assert.ThrowsAsync<TenantBoundaryException>(() => fixture.App.CallbackAsync(state, "code", false, default));
        Assert.Equal(0, fixture.Provider.Exchanges);
    }
    [Fact]
    public async Task DenialConsumesCallbackWithoutExchangingCode()
    {
        using var fixture = new Fixture(); var state = await fixture.Begin();
        Assert.False(await fixture.App.CallbackAsync(state, null, true, default));
        await Assert.ThrowsAsync<TenantBoundaryException>(() => fixture.App.CallbackAsync(state, "code", false, default));
        Assert.Equal(0, fixture.Provider.Exchanges);
    }
    [Fact]
    public async Task ExpiredCallbackAndForgedStateAreRejected()
    {
        using var fixture = new Fixture(); var state = await fixture.Begin();
        await fixture.Store.ExecuteAsync(records => { records[0].PendingUntil = DateTimeOffset.UtcNow.AddSeconds(-1); return Task.FromResult(true); }, default);
        Assert.False(await fixture.App.CallbackAsync(state, "code", false, default));
        await Assert.ThrowsAsync<TenantBoundaryException>(() => fixture.App.CallbackAsync(new string('a',64), "code", false, default));
        Assert.Equal(0, fixture.Provider.Exchanges);
    }
    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("owner-version")]
    [InlineData("consent")]
    [InlineData("configuration")]
    [InlineData("file-version")]
    [InlineData("folder")]
    public async Task ChangedBoundariesPreventImport(string change)
    {
        using var fixture = new Fixture(); var view = await fixture.Connect();
        switch (change)
        {
            case "tenant": fixture.Owners.Tenant = "T002"; break;
            case "actor": fixture.Owners.Actor = "A200"; break;
            case "owner-version": fixture.Owners.OwnerVersion++; break;
            case "consent": fixture.Owners.Allowed = false; break;
            case "configuration": fixture.Configuration["Monergy:OneDrive:Tenants:T001:ClientSecret"] = "rotated"; break;
            case "file-version": fixture.Provider.File = fixture.Provider.File with { Version = new string('b',64) }; break;
            case "folder": fixture.Provider.File = fixture.Provider.File with { Parents = ["another-folder"] }; break;
        }
        await Assert.ThrowsAsync<TenantBoundaryException>(() => fixture.App.ImportAsync(fixture.Http, new(view.Version, "file-1", new string('a',64))));
        Assert.Equal(0, fixture.Owners.Imports);
    }
    [Fact]
    public async Task ConsentRevokedDuringDownloadBlocksEvidenceIntake()
    {
        using var fixture = new Fixture(); var view = await fixture.Connect();
        fixture.Provider.AfterDownload = () => fixture.Owners.Allowed = false;
        await Assert.ThrowsAsync<TenantBoundaryException>(() => fixture.App.ImportAsync(fixture.Http, new(view.Version, "file-1", new string('a',64))));
        Assert.Equal(0, fixture.Owners.Imports);
    }
    [Fact]
    public async Task ConfigurationChangedDuringScanBlocksRegistration()
    {
        using var fixture = new Fixture(); var view = await fixture.Connect();
        fixture.Owners.DuringScan = () => fixture.Configuration["Monergy:OneDrive:Tenants:T001:Enabled"] = "false";
        await Assert.ThrowsAsync<TenantBoundaryException>(() => fixture.App.ImportAsync(fixture.Http, new(view.Version, "file-1", new string('a',64))));
        Assert.Equal(0, fixture.Owners.Imports);
    }
    [Fact]
    public async Task FileChangedDuringDownloadIsNotImported()
    {
        using var fixture = new Fixture(); var view = await fixture.Connect();
        fixture.Provider.AfterDownload = () => fixture.Provider.File = fixture.Provider.File with { Version = new string('b',64) };
        await Assert.ThrowsAsync<TenantBoundaryException>(() => fixture.App.ImportAsync(fixture.Http, new(view.Version, "file-1", new string('a',64))));
        Assert.Equal(0, fixture.Owners.Imports);
    }
    [Fact]
    public async Task ProviderOutageDuringDisconnectErasesTokensAndBlocksImport()
    {
        using var fixture = new Fixture(); var view = await fixture.Connect(); fixture.Provider.RevokeFails = true;
        var disconnected = await fixture.App.DisconnectAsync(fixture.Http, new(view.Version));
        Assert.Equal("Disconnected; Microsoft revocation unconfirmed", disconnected.Status);
        var secret = await fixture.Store.ExecuteAsync(records => Task.FromResult(records[0].Tokens), default);
        Assert.Null(secret);
        await Assert.ThrowsAsync<TenantBoundaryException>(() => fixture.App.ListAsync(fixture.Http, false, null));
    }
    [Fact]
    public async Task ReopenedStoreRetainsEncryptedStateWithoutPlaintextCredentials()
    {
        using var fixture = new Fixture(); await fixture.Connect();
        var bytes = await System.IO.File.ReadAllBytesAsync(Path.Combine(fixture.Directory, "connections.enc"));
        Assert.DoesNotContain("refresh-secret", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        using var reopened = new DriveConnectionStore(fixture.Configuration);
        var app = new DriveConnectionApplication(reopened, fixture.Owners, fixture.Provider, fixture.Configuration);
        Assert.Equal("Connected", (await app.StatusAsync(fixture.Http)).Status);
        bytes[^1] ^= 1; await System.IO.File.WriteAllBytesAsync(Path.Combine(fixture.Directory, "connections.enc"), bytes);
        await Assert.ThrowsAsync<TenantBoundaryException>(() => app.StatusAsync(fixture.Http));
    }
    [Fact]
    public async Task UnconfiguredProviderAndMissingExplicitConsentCannotStartAuthorization()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<TenantBoundaryException>(() => fixture.App.BeginAsync(fixture.Http, new(false)));
        fixture.Configuration["Monergy:OneDrive:Tenants:T001:Enabled"] = "false";
        Assert.Equal("Not configured", (await fixture.App.StatusAsync(fixture.Http)).Status);
        await Assert.ThrowsAsync<TenantBoundaryException>(() => fixture.App.BeginAsync(fixture.Http, new(true)));
    }
    [Fact]
    public async Task RefreshTokenIsUsedServerSideAndOldRevisionCannotChangeFolder()
    {
        using var fixture = new Fixture(); var view = await fixture.Connect();
        await fixture.Store.ExecuteAsync(records => { records[0].Tokens = records[0].Tokens! with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }; return Task.FromResult(true); }, default);
        await fixture.App.ListAsync(fixture.Http, false, null);
        Assert.Equal(1, fixture.Provider.Refreshes);
        await Assert.ThrowsAsync<TenantBoundaryException>(() => fixture.App.SelectFolderAsync(fixture.Http, new(view.Version - 1, "folder-1", null, true)));
    }
    [Theory]
    [InlineData("application/vnd.google-apps.document", 25)]
    [InlineData("application/vnd.google-apps.shortcut", 25)]
    [InlineData("text/plain", 1048577)]
    [InlineData("text/plain", 0)]
    public void UnsupportedProviderContentFailsClosed(string mime, long size)
    { Assert.Throws<TenantBoundaryException>(() => OneDriveProvider.RequireImportable(Fixture.Sample with { MimeType = mime, Size = size })); }

    private sealed class Fixture : IDisposable
    {
        public static DriveFile Sample => new("file-1", "statement.txt", "text/plain", new string('a',64), 3, "900150983cd24fb0d6963f7d28e17f72", ["folder-1"], false, true);
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "monergy-drive-test-" + Guid.NewGuid().ToString("N"));
        public IConfigurationRoot Configuration { get; }
        public DriveConnectionStore Store { get; }
        public FakeOwners Owners { get; } = new();
        public FakeProvider Provider { get; } = new();
        public DriveConnectionApplication App { get; }
        public DefaultHttpContext Http { get; } = new();
        public Fixture()
        {
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["Monergy:OneDrive:StoreDirectory"] = Directory, ["Monergy:OneDrive:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
                ["Monergy:OneDrive:Tenants:T001:Enabled"] = "true", ["Monergy:OneDrive:Tenants:T001:ClientId"] = "client",
                ["Monergy:OneDrive:Tenants:T001:ClientSecret"] = "secret", ["Monergy:OneDrive:Tenants:T001:RedirectUri"] = "http://localhost:4173/connections-api/local/v1/onedrive/callback"
            }).Build();
            Store = new(Configuration); App = new(Store, Owners, Provider, Configuration);
        }
        public async Task<string> Begin()
        {
            var begin = await App.BeginAsync(Http, new(true));
            return Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(begin.AuthorizationUrl).Query)["state"].ToString();
        }
        public async Task<DriveView> Connect()
        {
            var state = await Begin(); Assert.True(await App.CallbackAsync(state, "code", false, default));
            var view = await App.StatusAsync(Http);
            return await App.SelectFolderAsync(Http, new(view.Version, "folder-1", null, true));
        }
        public void Dispose() { Store.Dispose(); System.IO.Directory.Delete(Directory, true); (Configuration as IDisposable)?.Dispose(); }
    }
    private sealed class FakeOwners : IDriveOwners
    {
        public string Tenant { get; set; } = "T001";
        public string Actor { get; set; } = "A100";
        public long OwnerVersion { get; set; } = 1;
        public bool Allowed { get; set; } = true;
        public int Imports { get; private set; }
        public Action? DuringScan { get; set; }
        private readonly Dictionary<string, DriveConsentDecision> permissions = new();
        public Task<DriveCustomer> AuthorizeAsync(HttpContext http) => Task.FromResult(new DriveCustomer(
            new(Tenant, Actor, "session-1", 1, DateTimeOffset.UtcNow.AddHours(1)), new(Tenant, "C001", "Customer", null, Actor, OwnerVersion, true)));
        public Task<DriveConsentDecision> ConsentAsync(HttpContext http, DriveConnection connection, string? operation, string? folder)
        {
            if (operation is not null) permissions[connection.Id] = new(connection.Id, connection.CustomerId, "onedrive", "document-import",
                operation == "FolderBound" ? folder : connection.FolderId, connection.ConsentVersion + 1, operation != "Revoked", DateTimeOffset.UtcNow.AddDays(90));
            return Task.FromResult(permissions[connection.Id] with { Allowed = Allowed && permissions[connection.Id].Allowed });
        }
        public async Task<DriveImportReceipt> ImportAsync(HttpContext http, DriveCustomer customer, DriveConnection connection, DriveFile file, byte[] content, Func<Task> revalidate)
        {
            DuringScan?.Invoke(); await revalidate(); Imports++;
            return new(connection.Id, file.Id, file.Version, file.Name, "document-1", "document-version-1", new string('a',64), DateTimeOffset.UtcNow);
        }
    }
    private sealed class FakeProvider : IOneDriveProvider
    {
        public DriveFile File { get; set; } = Fixture.Sample;
        public int Exchanges { get; private set; }
        public int Refreshes { get; private set; }
        public bool Revoked { get; private set; }
        public bool RevokeFails { get; set; }
        public Action? AfterDownload { get; set; }
        public Task<DriveAccount> AccountAsync(DriveTokens tokens, CancellationToken ct) => Task.FromResult(new DriveAccount("account-1", "Microsoft account"));
        public Task<DriveTokens> ExchangeAsync(DriveConfiguration config, string code, CancellationToken ct)
        { Exchanges++; return Task.FromResult(new DriveTokens("access-secret", "refresh-secret", DateTimeOffset.UtcNow.AddHours(1))); }
        public Task<DriveTokens> RefreshAsync(DriveConfiguration config, DriveTokens tokens, CancellationToken ct)
        { Refreshes++; return Task.FromResult(tokens with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) }); }
        public Task<DrivePage> ListAsync(DriveTokens tokens, string? folder, string? page, CancellationToken ct) => Task.FromResult(new DrivePage([File], null));
        public Task<DriveFile> FileAsync(DriveTokens tokens, string id, CancellationToken ct) => Task.FromResult(id == "folder-1"
            ? new DriveFile(id, "Monergy", OneDriveProvider.FolderType, "1", 0, null, [], false, false) : File);
        public Task<byte[]> DownloadAsync(DriveTokens tokens, DriveFile file, CancellationToken ct) { AfterDownload?.Invoke(); return Task.FromResult("abc"u8.ToArray()); }
        public Task RevokeAsync(DriveTokens tokens, CancellationToken ct)
        { if (RevokeFails) throw new TenantBoundaryException("DRIVE_UNAVAILABLE",503); Revoked = true; return Task.CompletedTask; }
    }
}
