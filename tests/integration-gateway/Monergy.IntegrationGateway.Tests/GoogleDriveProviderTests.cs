using System.Net;
using System.Text;
using Monergy.Platform;
using Monergy.Services.IntegrationGateway.GoogleDrive;
using Xunit;

namespace Monergy.IntegrationGateway.Tests;

public sealed class GoogleDriveProviderTests
{
    private static readonly DriveConfiguration Config = new("client", "secret", "http://localhost:4173/connections-api/local/v1/google-drive/callback");
    private static readonly DriveTokens Tokens = new("access-private", "refresh-private", DateTimeOffset.UtcNow.AddHours(1));
    [Fact]
    public async Task CodeExchangeUsesFixedTokenEndpointAndRequiresGrantedScopeAndRefreshToken()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal("https://oauth2.googleapis.com/token", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            var form = await request.Content!.ReadAsStringAsync();
            Assert.Contains("client_secret=secret", form, StringComparison.Ordinal);
            Assert.Contains("grant_type=authorization_code", form, StringComparison.Ordinal);
            return Json("""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"https://www.googleapis.com/auth/drive.readonly"}""");
        });
        using var provider = new GoogleDriveProvider(new HttpClient(handler));
        var result = await provider.ExchangeAsync(Config, "code", default);
        Assert.Equal("refresh", result.RefreshToken);
        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow);
    }
    [Theory]
    [InlineData("{\"access_token\":\"access\",\"expires_in\":3600,\"token_type\":\"Bearer\",\"scope\":\"https://www.googleapis.com/auth/drive.readonly\"}")]
    [InlineData("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\",\"scope\":\"openid\"}")]
    public async Task IncompletePermissionCannotProduceAConnection(string response)
    {
        using var handler = new Handler(_ => Task.FromResult(Json(response)));
        using var provider = new GoogleDriveProvider(new HttpClient(handler));
        await Assert.ThrowsAsync<TenantBoundaryException>(() => provider.ExchangeAsync(Config, "code", default));
    }
    [Fact]
    public async Task FileListUsesBoundParentAndBearerHeaderWithoutTokenInUrl()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-private", request.Headers.Authorization?.Parameter);
            Assert.DoesNotContain("private", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            Assert.Contains("'folder-1' in parents", query, StringComparison.Ordinal);
            Assert.Contains("pageSize=50", query, StringComparison.Ordinal);
            return Task.FromResult(Json("""{"files":[{"id":"file-1","name":"a.txt","mimeType":"text/plain","version":"1","size":"3","md5Checksum":"900150983cd24fb0d6963f7d28e17f72","parents":["folder-1"],"trashed":false,"capabilities":{"canDownload":true}}],"nextPageToken":"page2"}"""));
        });
        using var provider = new GoogleDriveProvider(new HttpClient(handler));
        var page = await provider.ListAsync(Tokens, "folder-1", null, default);
        Assert.Single(page.Items); Assert.Equal("page2", page.NextPageToken);
    }
    [Fact]
    public async Task ProviderCannotReturnAnUnboundParentInAFilePage()
    {
        using var handler = new Handler(_ => Task.FromResult(Json("""{"files":[{"id":"file-1","name":"a.txt","mimeType":"text/plain","version":"1","size":"3","parents":["elsewhere"],"trashed":false,"capabilities":{"canDownload":true}}]}""")));
        using var provider = new GoogleDriveProvider(new HttpClient(handler));
        await Assert.ThrowsAsync<TenantBoundaryException>(() => provider.ListAsync(Tokens, "folder-1", null, default));
    }
    [Fact]
    public async Task DownloadMustMatchProviderSizeAndChecksum()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("abc"u8.ToArray()) }));
        using var provider = new GoogleDriveProvider(new HttpClient(handler));
        var file = new DriveFile("file-1", "a.txt", "text/plain", "1", 3, "900150983cd24fb0d6963f7d28e17f72", ["folder-1"], false, true);
        Assert.Equal("abc"u8.ToArray(), await provider.DownloadAsync(Tokens, file, default));
        await Assert.ThrowsAsync<TenantBoundaryException>(() => provider.DownloadAsync(Tokens, file with { Md5Checksum = new string('a',32) }, default));
    }
    [Fact]
    public async Task RateLimitHasBoundedRetryableStatusWithoutProviderBodyDisclosure()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("provider-private-detail") }));
        using var provider = new GoogleDriveProvider(new HttpClient(handler));
        var error = await Assert.ThrowsAsync<TenantBoundaryException>(() => provider.ListAsync(Tokens, "folder-1", null, default));
        Assert.Equal(429, error.Status); Assert.Equal("DRIVE_RATE_LIMITED", error.Code);
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("folder' or 'x'='x")]
    [InlineData("../token")]
    [InlineData("https://example.test")]
    public void CallerCannotSupplyProviderQueryOrDestination(string id) => Assert.Throws<TenantBoundaryException>(() => GoogleDriveProvider.Id(id));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> operation) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => operation(request); }
}
