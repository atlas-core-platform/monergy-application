using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Monergy.Platform;

namespace Monergy.Services.IntegrationGateway.OneDrive;

public sealed record DriveConfiguration(string ClientId, string ClientSecret, string RedirectUri, string? CodeVerifier = null)
{
    public string Revision => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { ClientId, ClientSecret, RedirectUri }))));
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 256 || string.IsNullOrWhiteSpace(ClientSecret) || ClientSecret.Length > 512 ||
            !Uri.TryCreate(RedirectUri, UriKind.Absolute, out var uri) || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/connections-api/local/v1/onedrive/callback")
            throw new TenantBoundaryException("DRIVE_CONFIGURATION_INVALID", 503);
    }
}
public sealed record DriveAccount(string Id, string Label);
public sealed record DriveTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
public sealed record DriveFile(string Id, string Name, string MimeType, string Version, long Size,
    string? ETag, string[] Parents, bool Trashed, bool CanDownload);
public sealed record DrivePage(DriveFile[] Items, string? NextPageToken);
public interface IOneDriveProvider
{
    Task<DriveAccount> AccountAsync(DriveTokens tokens, CancellationToken ct);
    Task<DriveTokens> ExchangeAsync(DriveConfiguration config, string code, CancellationToken ct);
    Task<DriveTokens> RefreshAsync(DriveConfiguration config, DriveTokens tokens, CancellationToken ct);
    Task<DrivePage> ListAsync(DriveTokens tokens, string? folder, string? page, CancellationToken ct);
    Task<DriveFile> FileAsync(DriveTokens tokens, string id, CancellationToken ct);
    Task<byte[]> DownloadAsync(DriveTokens tokens, DriveFile file, CancellationToken ct);
    Task RevokeAsync(DriveTokens tokens, CancellationToken ct);
}

// Global-cloud delegated read access only. All Graph requests target the signed-in
// user's default drive. No application permissions, shares, remote items or writes.
public sealed class OneDriveProvider(HttpClient client) : IOneDriveProvider, IDisposable
{
    public static readonly HttpRequestOptionsKey<bool> SensitiveRequest = new("Monergy.OneDrive.Sensitive");
    public const string Scope = "https://graph.microsoft.com/Files.Read offline_access";
    public const string FolderType = "application/vnd.monergy.onedrive-folder";
    private const string Root = "https://graph.microsoft.com/v1.0/me/drive";
    private const string Fields = "id,name,eTag,size,file,folder,parentReference,deleted,remoteItem,package";
    public static string AuthorizationUrl(DriveConfiguration config, string state, string verifier)
    {
        config.Validate();
        return "https://login.microsoftonline.com/common/oauth2/v2.0/authorize?" + Query(new()
        { ["client_id"] = config.ClientId, ["redirect_uri"] = config.RedirectUri, ["response_type"] = "code", ["response_mode"] = "query",
          ["scope"] = Scope, ["prompt"] = "select_account", ["state"] = state,
          ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256" });
    }
    public async Task<DriveAccount> AccountAsync(DriveTokens tokens, CancellationToken ct)
    {
        using var request = Request(tokens, Root + "?$select=id,name,driveType,owner");
        using var json = await JsonAsync(request, ct); var root = json.RootElement;
        var id = Text(root, "id");
        var label = Text(root, "name");
        if (root.TryGetProperty("owner", out var owner) && owner.TryGetProperty("user", out var user)) label = Text(user, "displayName", false) ?? label;
        if (id.Length > 200 || label.Length > 320 || Text(root, "driveType") is not ("personal" or "business"))
            throw new TenantBoundaryException("DRIVE_ACCOUNT_UNSUPPORTED", 422);
        return new(id, label);
    }
    public Task<DriveTokens> ExchangeAsync(DriveConfiguration config, string code, CancellationToken ct)
    {
        if (config.CodeVerifier is not { Length: 64 }) throw new TenantBoundaryException("DRIVE_CALLBACK_INVALID", 400);
        return TokenAsync(config, new() { ["code"] = code, ["redirect_uri"] = config.RedirectUri,
            ["code_verifier"] = config.CodeVerifier, ["grant_type"] = "authorization_code" }, null, ct);
    }
    public Task<DriveTokens> RefreshAsync(DriveConfiguration config, DriveTokens tokens, CancellationToken ct) =>
        TokenAsync(config, new() { ["refresh_token"] = tokens.RefreshToken, ["grant_type"] = "refresh_token" }, tokens.RefreshToken, ct);
    private async Task<DriveTokens> TokenAsync(DriveConfiguration config, Dictionary<string,string> fields, string? refresh, CancellationToken ct)
    {
        config.Validate(); fields["client_id"] = config.ClientId; fields["client_secret"] = config.ClientSecret; fields["scope"] = Scope;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/common/oauth2/v2.0/token") { Content = new FormUrlEncodedContent(fields) };
        request.Options.Set(SensitiveRequest, true);
        using var json = await JsonAsync(request, ct); var root = json.RootElement;
        var access = Text(root, "access_token"); var renewed = Text(root, "refresh_token", false); var seconds = root.GetProperty("expires_in").GetInt32();
        var scopes = Text(root, "scope").Split(' ');
        if (!string.Equals(Text(root, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase) || seconds is < 1 or > 86400 || access.Length > 8192 ||
            (renewed ?? refresh) is not { Length: > 0 and <= 8192 } || !scopes.Any(s => s is "Files.Read" or "https://graph.microsoft.com/Files.Read"))
            throw new TenantBoundaryException("DRIVE_PERMISSION_INCOMPLETE", 409);
        return new(access, renewed ?? refresh!, DateTimeOffset.UtcNow.AddSeconds(seconds));
    }
    public async Task<DrivePage> ListAsync(DriveTokens tokens, string? folder, string? page, CancellationToken ct)
    {
        if (folder is not null) Id(folder);
        var browse = folder;
        if (folder is null && page?.StartsWith("folder:", StringComparison.Ordinal) == true) { browse = page[7..]; Id(browse); page = null; }
        var path = browse is null ? "/v1.0/me/drive/root/children" : "/v1.0/me/drive/items/" + Uri.EscapeDataString(browse) + "/children";
        var url = "https://graph.microsoft.com" + path + "?" + Query(new() { ["$select"] = Fields, ["$top"] = "50", ["$orderby"] = "name" });
        if (page is not null) url = PageUrl(page, folder);
        using var request = Request(tokens, url); using var json = await JsonAsync(request, ct);
        var items = json.RootElement.GetProperty("value").EnumerateArray().Select(ParseFile).ToArray();
        if (items.Length > 50 || folder is not null && items.Any(item => !item.Parents.Contains(folder)))
            throw new TenantBoundaryException("DRIVE_RESPONSE_INVALID", 503);
        var next = Text(json.RootElement, "@odata.nextLink", false);
        if (next is not null) { _ = PageUrl(next, folder); if (new Uri(next).AbsolutePath != new Uri(url).AbsolutePath) throw new TenantBoundaryException("DRIVE_PAGE_INVALID", 503); }
        return new(items.Where(file => !file.Trashed && (folder is null ? file.MimeType == FolderType : file.MimeType != FolderType)).ToArray(), next);
    }
    private static string PageUrl(string value, string? folder)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "graph.microsoft.com" ||
            uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new TenantBoundaryException("DRIVE_PAGE_INVALID",400);
        var path = uri.AbsolutePath;
        const string prefix = "/v1.0/me/drive/items/";
        if (folder is not null)
        { if (path != prefix + Uri.EscapeDataString(folder) + "/children") throw new TenantBoundaryException("DRIVE_PAGE_INVALID",400); }
        else if (path != "/v1.0/me/drive/root/children")
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith("/children", StringComparison.Ordinal)) throw new TenantBoundaryException("DRIVE_PAGE_INVALID",400);
            Id(Uri.UnescapeDataString(path[prefix.Length..^9]));
        }
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (query.Count == 0 || query.Any(pair => pair.Value.Count != 1 || pair.Key is not ("$select" or "$top" or "$orderby" or "$skiptoken" or "$skip")) ||
            query.TryGetValue("$top", out var top) && top != "50" || query.TryGetValue("$select", out var select) && select != Fields)
            throw new TenantBoundaryException("DRIVE_PAGE_INVALID",400);
        return value;
    }
    public async Task<DriveFile> FileAsync(DriveTokens tokens, string id, CancellationToken ct)
    {
        Id(id); using var request = Request(tokens, Root + "/items/" + Uri.EscapeDataString(id) + "?$select=" + Uri.EscapeDataString(Fields));
        using var json = await JsonAsync(request, ct); var file = ParseFile(json.RootElement);
        if (file.Id != id) throw new TenantBoundaryException("DRIVE_RESPONSE_INVALID",503); return file;
    }
    public async Task<byte[]> DownloadAsync(DriveTokens tokens, DriveFile file, CancellationToken ct)
    {
        RequireImportable(file);
        using var request = Request(tokens, Root + "/items/" + Uri.EscapeDataString(file.Id) + "/content");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        byte[] bytes;
        if (response.StatusCode == HttpStatusCode.Found)
        {
            var location = response.Headers.Location;
            if (!SafeDownload(location)) throw new TenantBoundaryException("DRIVE_DOWNLOAD_HOST_UNSUPPORTED",422);
            // This URL is a temporary credential. Never expose/log it, never send
            // the Graph bearer token to it, and never follow a second redirect.
            using var download = new HttpRequestMessage(HttpMethod.Get, location);
            download.Options.Set(SensitiveRequest, true);
            using var content = await client.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, ct);
            RequireSuccess(content); bytes = await ReadBoundedAsync(content.Content, 1_048_576, ct);
        }
        else { RequireSuccess(response); bytes = await ReadBoundedAsync(response.Content, 1_048_576, ct); }
        if (bytes.LongLength != file.Size) throw new TenantBoundaryException("DRIVE_FILE_CHANGED",409);
        return bytes;
    }
    private static readonly string[] DownloadSuffixes = [".sharepoint.com", ".1drv.com", ".storage.live.com", ".onedrive.live.com"];
    private static bool SafeDownload(Uri? uri) => uri is { IsAbsoluteUri: true, Scheme: "https", Port: 443 } && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        DownloadSuffixes.Any(suffix => uri.IdnHost.EndsWith(suffix,StringComparison.Ordinal));
    // Microsoft has no per-token equivalent of Google's revoke endpoint. Do not
    // revoke all account sessions or request privileged directory permissions.
    public Task RevokeAsync(DriveTokens tokens, CancellationToken ct) => Task.FromException(new TenantBoundaryException("DRIVE_MANUAL_REVOCATION_REQUIRED",409));
    public static bool ValidVersion(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    public static void Id(string value)
    { if (value.Length is < 1 or > 200 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '!')) throw new TenantBoundaryException("DRIVE_IDENTIFIER_INVALID",400); }
    public static void RequireImportable(DriveFile file)
    {
        Id(file.Id);
        if (file.Trashed || !file.CanDownload || file.Size is < 1 or > 1_048_576 || string.IsNullOrEmpty(file.ETag) || !ValidVersion(file.Version) ||
            file.MimeType is not ("application/pdf" or "image/png" or "image/jpeg" or "text/plain" or "text/csv" or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet") ||
            string.IsNullOrWhiteSpace(file.Name) || file.Name.Length > 180 || file.Name.Any(c => char.IsControl(c) || c is '/' or '\\'))
            throw new TenantBoundaryException("DRIVE_FILE_UNSUPPORTED",422);
    }
    private static DriveFile ParseFile(JsonElement item)
    {
        var id = Text(item,"id"); Id(id); var tag = Text(item,"eTag");
        if (tag.Length > 512 || tag.Any(char.IsControl)) throw new TenantBoundaryException("DRIVE_RESPONSE_INVALID",503);
        var folder = item.TryGetProperty("folder",out _); var isFile = item.TryGetProperty("file",out var file);
        var blocked = item.TryGetProperty("deleted",out _) || item.TryGetProperty("remoteItem",out _) || item.TryGetProperty("package",out _);
        var parent = item.TryGetProperty("parentReference",out var reference) ? Text(reference,"id",false) : null;
        if (parent is not null) Id(parent);
        return new(id,Text(item,"name"), folder && !blocked ? FolderType : isFile ? Text(file,"mimeType",false) ?? "application/octet-stream" : "application/octet-stream",
            DriveConnectionStore.Hash(tag),item.GetProperty("size").GetInt64(),tag,parent is null ? [] : [parent],blocked,isFile && !blocked);
    }
    private static HttpRequestMessage Request(DriveTokens tokens, string url)
    { var request = new HttpRequestMessage(HttpMethod.Get,url); request.Options.Set(SensitiveRequest,true); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",tokens.AccessToken); return request; }
    private async Task<JsonDocument> JsonAsync(HttpRequestMessage request,CancellationToken ct)
    { using var response = await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct); RequireSuccess(response); return JsonDocument.Parse(await ReadBoundedAsync(response.Content,131072,ct)); }
    private static void RequireSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new TenantBoundaryException(response.StatusCode switch {
            HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest => "DRIVE_RECONNECT_REQUIRED", HttpStatusCode.Forbidden => "DRIVE_PERMISSION_REQUIRED",
            HttpStatusCode.NotFound => "DRIVE_FILE_UNAVAILABLE", HttpStatusCode.TooManyRequests => "DRIVE_RATE_LIMITED", _ => "DRIVE_UNAVAILABLE" },
            response.StatusCode == HttpStatusCode.TooManyRequests ? 429 : 503);
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content,int maximum,CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw new TenantBoundaryException("DRIVE_RESPONSE_TOO_LARGE",422);
        await using var input = await content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer,ct)) != 0) { if (output.Length + read > maximum) throw new TenantBoundaryException("DRIVE_RESPONSE_TOO_LARGE",422); output.Write(buffer,0,read); }
        return output.ToArray();
    }
    private static string Text(JsonElement item,string key) => Text(item,key,true)!;
    private static string? Text(JsonElement item,string key,bool required) => item.TryGetProperty(key,out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
        ? text : required ? throw new TenantBoundaryException("DRIVE_RESPONSE_INVALID",503) : null;
    private static string Query(Dictionary<string,string> values) => string.Join('&',values.Select(pair => Uri.EscapeDataString(pair.Key)+"="+Uri.EscapeDataString(pair.Value)));
    public void Dispose() => client.Dispose();
}
