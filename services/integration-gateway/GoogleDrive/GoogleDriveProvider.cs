using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Platform;

namespace Monergy.Services.IntegrationGateway.GoogleDrive;

public sealed record DriveConfiguration(string ClientId, string ClientSecret, string RedirectUri)
{
    public string Revision => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { ClientId, ClientSecret, RedirectUri }))));
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 256 || string.IsNullOrWhiteSpace(ClientSecret) ||
            ClientSecret.Length > 512 || !Uri.TryCreate(RedirectUri, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/connections-api/local/v1/google-drive/callback")
            throw new TenantBoundaryException("DRIVE_CONFIGURATION_INVALID", 503);
    }
}
public sealed record DriveAccount(string Id, string Label);
public sealed record DriveTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
public sealed record DriveFile(string Id, string Name, string MimeType, string Version, long Size,
    string? Md5Checksum, string[] Parents, bool Trashed, bool CanDownload);
public sealed record DrivePage(DriveFile[] Items, string? NextPageToken);
public interface IGoogleDriveProvider
{
    Task<DriveAccount> AccountAsync(DriveTokens tokens, CancellationToken ct);
    Task<DriveTokens> ExchangeAsync(DriveConfiguration config, string code, CancellationToken ct);
    Task<DriveTokens> RefreshAsync(DriveConfiguration config, DriveTokens tokens, CancellationToken ct);
    Task<DrivePage> ListAsync(DriveTokens tokens, string? folder, string? page, CancellationToken ct);
    Task<DriveFile> FileAsync(DriveTokens tokens, string id, CancellationToken ct);
    Task<byte[]> DownloadAsync(DriveTokens tokens, DriveFile file, CancellationToken ct);
    Task RevokeAsync(DriveTokens tokens, CancellationToken ct);
}

// Provider URLs are fixed. No browser-selected URL, redirect following or bearer
// token in a query string. Responses and downloads have explicit size bounds.
public sealed class GoogleDriveProvider(HttpClient client) : IGoogleDriveProvider, IDisposable
{
    public const string Scope = "https://www.googleapis.com/auth/drive.readonly";
    public const string FolderType = "application/vnd.google-apps.folder";
    private const string Fields = "id,name,mimeType,version,size,md5Checksum,parents,trashed,capabilities(canDownload)";
    public static string AuthorizationUrl(DriveConfiguration config, string state) =>
        "https://accounts.google.com/o/oauth2/v2/auth?" + Query(new()
        {
            ["client_id"] = config.ClientId, ["redirect_uri"] = config.RedirectUri,
            ["response_type"] = "code", ["scope"] = Scope, ["access_type"] = "offline",
            ["prompt"] = "consent select_account", ["state"] = state
        });
    public async Task<DriveAccount> AccountAsync(DriveTokens tokens, CancellationToken ct)
    {
        using var request = Request(tokens, "about?fields=user(permissionId,displayName)");
        using var json = await JsonAsync(request, ct);
        var user = json.RootElement.GetProperty("user");
        var id = Text(user, "permissionId"); var label = Text(user, "displayName");
        if (id.Length > 200 || label.Length > 320) throw new TenantBoundaryException("DRIVE_RESPONSE_INVALID", 503);
        return new(id, label);
    }
    public Task<DriveTokens> ExchangeAsync(DriveConfiguration config, string code, CancellationToken ct) =>
        TokenAsync(config, new() { ["code"] = code, ["redirect_uri"] = config.RedirectUri,
            ["grant_type"] = "authorization_code" }, null, ct);
    public Task<DriveTokens> RefreshAsync(DriveConfiguration config, DriveTokens tokens, CancellationToken ct) =>
        TokenAsync(config, new() { ["refresh_token"] = tokens.RefreshToken, ["grant_type"] = "refresh_token" }, tokens.RefreshToken, ct);
    private async Task<DriveTokens> TokenAsync(DriveConfiguration config, Dictionary<string,string> fields, string? refresh, CancellationToken ct)
    {
        config.Validate(); fields["client_id"] = config.ClientId; fields["client_secret"] = config.ClientSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        { Content = new FormUrlEncodedContent(fields) };
        using var json = await JsonAsync(request, ct);
        var root = json.RootElement;
        var access = Text(root, "access_token"); var renewed = Text(root, "refresh_token", false);
        var seconds = root.GetProperty("expires_in").GetInt32();
        if (!string.Equals(Text(root, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase) ||
            seconds is < 1 or > 86400 || access.Length > 8192 || (renewed ?? refresh) is not { Length: > 0 and <= 8192 } ||
            refresh is null && !(Text(root, "scope", false)?.Split(' ').Contains(Scope) ?? false))
            throw new TenantBoundaryException("DRIVE_PERMISSION_INCOMPLETE", 409);
        return new(access, renewed ?? refresh!, DateTimeOffset.UtcNow.AddSeconds(seconds));
    }
    public async Task<DrivePage> ListAsync(DriveTokens tokens, string? folder, string? page, CancellationToken ct)
    {
        if (folder is not null) Id(folder);
        if (page?.Length > 2048) throw new TenantBoundaryException("DRIVE_PAGE_INVALID", 400);
        var query = new Dictionary<string,string> { ["q"] = folder is null
            ? $"trashed = false and mimeType = '{FolderType}'"
            : $"trashed = false and '{folder}' in parents and mimeType != '{FolderType}'",
            ["pageSize"] = "50", ["fields"] = $"nextPageToken,files({Fields})", ["orderBy"] = "name", ["spaces"] = "drive" };
        if (!string.IsNullOrEmpty(page)) query["pageToken"] = page;
        using var request = Request(tokens, "files?" + Query(query));
        using var json = await JsonAsync(request, ct);
        var files = json.RootElement.GetProperty("files").EnumerateArray().Select(ParseFile).ToArray();
        if (files.Length > 50 || files.Any(file => file.Trashed || (folder is null ? file.MimeType != FolderType : !file.Parents.Contains(folder))))
            throw new TenantBoundaryException("DRIVE_RESPONSE_INVALID", 503);
        return new(files, Text(json.RootElement, "nextPageToken", false));
    }
    public async Task<DriveFile> FileAsync(DriveTokens tokens, string id, CancellationToken ct)
    {
        Id(id); using var request = Request(tokens, "files/" + id + "?fields=" + Uri.EscapeDataString(Fields));
        using var json = await JsonAsync(request, ct); var file = ParseFile(json.RootElement);
        if (file.Id != id) throw new TenantBoundaryException("DRIVE_RESPONSE_INVALID", 503);
        return file;
    }
    public async Task<byte[]> DownloadAsync(DriveTokens tokens, DriveFile file, CancellationToken ct)
    {
        RequireImportable(file); using var request = Request(tokens, "files/" + file.Id + "?alt=media");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        RequireSuccess(response);
        var bytes = await ReadBoundedAsync(response.Content, 1_048_576, ct);
        // MD5 is a provider change detector only; Evidence independently computes SHA-256.
#pragma warning disable CA5351
        if (bytes.LongLength != file.Size || !string.Equals(Convert.ToHexStringLower(MD5.HashData(bytes)), file.Md5Checksum, StringComparison.Ordinal))
            throw new TenantBoundaryException("DRIVE_FILE_CHANGED", 409);

#pragma warning restore CA5351
        return bytes;
    }
    public async Task RevokeAsync(DriveTokens tokens, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/revoke")
        { Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["token"] = tokens.RefreshToken }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        RequireSuccess(response);
    }
    public static void Id(string value)
    {
        if (value.Length is < 1 or > 200 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new TenantBoundaryException("DRIVE_IDENTIFIER_INVALID", 400);
    }
    public static void RequireImportable(DriveFile file)
    {
        Id(file.Id);
        if (file.Trashed || !file.CanDownload || file.Size is < 1 or > 1_048_576 ||
            file.Md5Checksum?.Length != 32 || !file.Md5Checksum.All(char.IsAsciiHexDigit) ||
            file.MimeType is not ("application/pdf" or "image/png" or "image/jpeg" or "text/plain" or "text/csv" or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet") ||
            string.IsNullOrWhiteSpace(file.Name) || file.Name.Length > 180 || file.Name.Any(c => char.IsControl(c) || c is '/' or '\\'))
            throw new TenantBoundaryException("DRIVE_FILE_UNSUPPORTED", 422);
    }
    private static HttpRequestMessage Request(DriveTokens tokens, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken); return request;
    }
    private async Task<JsonDocument> JsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        RequireSuccess(response); return JsonDocument.Parse(await ReadBoundedAsync(response.Content, 131072, ct));
    }
    private static void RequireSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new TenantBoundaryException(response.StatusCode switch
        { System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.BadRequest => "DRIVE_RECONNECT_REQUIRED",
          System.Net.HttpStatusCode.Forbidden => "DRIVE_PERMISSION_REQUIRED", System.Net.HttpStatusCode.NotFound => "DRIVE_FILE_UNAVAILABLE",
          System.Net.HttpStatusCode.TooManyRequests => "DRIVE_RATE_LIMITED", _ => "DRIVE_UNAVAILABLE" },
          response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? 429 : 503);
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw new TenantBoundaryException("DRIVE_RESPONSE_TOO_LARGE", 422);
        await using var input = await content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        { if (output.Length + read > maximum) throw new TenantBoundaryException("DRIVE_RESPONSE_TOO_LARGE", 422); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    private static DriveFile ParseFile(JsonElement item)
    {
        var id = Text(item, "id"); Id(id);
        var version = Text(item, "version");
        if (!ulong.TryParse(version, out _) || version.Length > 30) throw new TenantBoundaryException("DRIVE_RESPONSE_INVALID", 503);
        return new(id, Text(item, "name"), Text(item, "mimeType"), version,
            long.TryParse(Text(item, "size", false), out var size) ? size : 0, Text(item, "md5Checksum", false),
            item.TryGetProperty("parents", out var parents) ? parents.EnumerateArray().Select(p => p.GetString()!).ToArray() : [],
            item.GetProperty("trashed").GetBoolean(), item.TryGetProperty("capabilities", out var caps) &&
            caps.TryGetProperty("canDownload", out var download) && download.GetBoolean());
    }
    private static string Text(JsonElement item, string key) => Text(item, key, true)!;
    private static string? Text(JsonElement item, string key, bool required) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
        ? text : required ? throw new TenantBoundaryException("DRIVE_RESPONSE_INVALID", 503) : null;
    public void Dispose() => client.Dispose();
    private static string Query(Dictionary<string,string> values) => string.Join('&', values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
}
