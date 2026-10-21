using System.Net;
using System.Text;
using Monergy.Platform;
using Monergy.Services.IntegrationGateway.OneDrive;
using Xunit;

namespace Monergy.IntegrationGateway.Tests;

public sealed class OneDriveProviderTests
{
    private static readonly DriveConfiguration Config = new("client", "secret", "http://localhost:4173/connections-api/local/v1/onedrive/callback", new string('a',64));
    private static readonly DriveTokens Tokens = new("access-private", "refresh-private", DateTimeOffset.UtcNow.AddHours(1));
    private static DriveFile File => new("ABC!123", "a.txt", "text/plain", new string('a',64),3,"tag",["folder"],false,true);
    [Fact]
    public async Task ConfidentialCodeExchangeBindsPkceAndReadScope()
    {
        var url = OneDriveProvider.AuthorizationUrl(Config,"state",Config.CodeVerifier!);
        Assert.Contains("code_challenge_method=S256",url,StringComparison.Ordinal);
        Assert.DoesNotContain(Config.CodeVerifier!,url,StringComparison.Ordinal);
        using var handler = new Handler(async request =>
        {
            Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token",request.RequestUri!.AbsoluteUri);
            var form = await request.Content!.ReadAsStringAsync();
            Assert.Contains("code_verifier="+Config.CodeVerifier,form,StringComparison.Ordinal);
            Assert.Contains("client_secret=secret",form,StringComparison.Ordinal);
            return Json("""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"Files.Read"}""");
        });
        using var provider = new OneDriveProvider(new HttpClient(handler));
        Assert.Equal("refresh",(await provider.ExchangeAsync(Config,"code",default)).RefreshToken);
    }
    [Theory]
    [InlineData("User.Read",true)]
    [InlineData("Files.Read",false)]
    public async Task MissingReadOrOfflinePermissionFailsClosed(string scope,bool refresh)
    {
        using var handler = new Handler(_ => Task.FromResult(Json(System.Text.Json.JsonSerializer.Serialize(new {
            access_token="access",refresh_token=refresh?"refresh":null,expires_in=3600,token_type="Bearer",scope }))));
        using var provider = new OneDriveProvider(new HttpClient(handler));
        await Assert.ThrowsAsync<TenantBoundaryException>(()=>provider.ExchangeAsync(Config,"code",default));
    }
    [Fact]
    public async Task RefreshRotatesTokenAndRechecksGrantedScope()
    {
        using var handler = new Handler(async request => {
            Assert.Contains("refresh_token=refresh-private",await request.Content!.ReadAsStringAsync(),StringComparison.Ordinal);
            return Json("""{"access_token":"new","refresh_token":"rotated","expires_in":3600,"token_type":"Bearer","scope":"Files.Read"}""");
        });
        using var provider = new OneDriveProvider(new HttpClient(handler));
        Assert.Equal("rotated",(await provider.RefreshAsync(Config,Tokens,default)).RefreshToken);
    }
    [Fact]
    public async Task MicrosoftRedirectDownloadNeverForwardsBearerToken()
    {
        var calls=0;
        using var handler=new Handler(request=> {
            if (++calls==1) {
                Assert.Equal("graph.microsoft.com",request.RequestUri!.Host);
                Assert.Equal("access-private",request.Headers.Authorization?.Parameter);
                return Task.FromResult(Redirect("https://tenant.sharepoint.com/download?temporary=secret"));
            }
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent("abc"u8.ToArray())});
        });
        using var provider=new OneDriveProvider(new HttpClient(handler));
        Assert.Equal("abc"u8.ToArray(),await provider.DownloadAsync(Tokens,File,default));Assert.Equal(2,calls);
    }
    [Theory]
    [InlineData("http://tenant.sharepoint.com/file")]
    [InlineData("https://tenant.sharepoint.com.evil.example/file")]
    [InlineData("https://127.0.0.1/file")]
    [InlineData("https://token@tenant.sharepoint.com/file")]
    [InlineData("https://tenant.sharepoint.com:8443/file")]
    public async Task UntrustedDownloadDestinationCannotReceiveRequest(string url)
    {
        var calls=0;using var handler=new Handler(_=>{calls++;return Task.FromResult(Redirect(url));});
        using var provider=new OneDriveProvider(new HttpClient(handler));
        await Assert.ThrowsAsync<TenantBoundaryException>(()=>provider.DownloadAsync(Tokens,File,default));Assert.Equal(1,calls);
    }
    [Fact]
    public async Task SecondRedirectAndWrongSizeAreRejected()
    {
        using var handler=new Handler(_=>Task.FromResult(Redirect("https://tenant.sharepoint.com/file")));
        using var provider=new OneDriveProvider(new HttpClient(handler));
        await Assert.ThrowsAsync<TenantBoundaryException>(()=>provider.DownloadAsync(Tokens,File,default));
        using var bytes=new Handler(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent("ab"u8.ToArray())}));
        using var other=new OneDriveProvider(new HttpClient(bytes));
        await Assert.ThrowsAsync<TenantBoundaryException>(()=>other.DownloadAsync(Tokens,File,default));
    }
    [Theory]
    [InlineData("https://evil.example/v1.0/me/drive/root/children?$skiptoken=foo")]
    [InlineData("https://graph.microsoft.com/v1.0/users/other/drive/root/children?$skiptoken=foo")]
    [InlineData("https://graph.microsoft.com/v1.0/me/drive/items/other/children?$skiptoken=foo")]
    [InlineData("https://graph.microsoft.com/v1.0/me/drive/items/folder/children?$expand=permissions")]
    public async Task ForgedFilePageCannotEscapeBoundFolderOrExpandFields(string page)
    {
        using var handler=new Handler(_=>throw new InvalidOperationException("No network expected"));
        using var provider=new OneDriveProvider(new HttpClient(handler));
        await Assert.ThrowsAsync<TenantBoundaryException>(()=>provider.ListAsync(Tokens,"folder",page,default));
    }
    [Fact]
    public async Task NestedFolderBrowseAndPagingRetainFixedGraphScope()
    {
        using var handler=new Handler(request=> {
            Assert.Equal("/v1.0/me/drive/items/ABC%21123/children",request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""{"value":[{"id":"sub","name":"Subfolder","eTag":"tag","size":0,"folder":{},"parentReference":{"id":"ABC!123"}}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/drive/items/ABC%21123/children?$skiptoken=next"}"""));
        });
        using var provider=new OneDriveProvider(new HttpClient(handler));
        var first=await provider.ListAsync(Tokens,null,"folder:ABC!123",default);
        Assert.Single(first.Items);Assert.Equal(OneDriveProvider.FolderType,first.Items[0].MimeType);
        Assert.Single((await provider.ListAsync(Tokens,null,first.NextPageToken,default)).Items);
    }
    [Fact]
    public async Task RemoteAndPackageItemsCannotBeImportedAndVersionUsesExactEtag()
    {
        using var handler=new Handler(_=>Task.FromResult(Json("""{"id":"ABC!123","name":"a.txt","eTag":"tag","size":3,"file":{"mimeType":"text/plain"},"parentReference":{"id":"folder"},"remoteItem":{}}""")));
        using var provider=new OneDriveProvider(new HttpClient(handler));
        var file=await provider.FileAsync(Tokens,"ABC!123",default);
        Assert.Equal(DriveConnectionStore.Hash("tag"),file.Version);
        Assert.Throws<TenantBoundaryException>(()=>OneDriveProvider.RequireImportable(file));
    }
    [Theory]
    [InlineData(401,"DRIVE_RECONNECT_REQUIRED",503)]
    [InlineData(403,"DRIVE_PERMISSION_REQUIRED",503)]
    [InlineData(429,"DRIVE_RATE_LIMITED",429)]
    public async Task ProviderFailuresExposeNoPrivateBody(int status,string code,int expected)
    {
        using var handler=new Handler(_=>Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent("private-details")}));
        using var provider=new OneDriveProvider(new HttpClient(handler));
        var error=await Assert.ThrowsAsync<TenantBoundaryException>(()=>provider.ListAsync(Tokens,"folder",null,default));
        Assert.Equal(code,error.Code);Assert.Equal(expected,error.Status);
    }
    [Fact]
    public async Task DisconnectDoesNotRevokeUnrelatedMicrosoftSessions()
    {
        using var handler=new Handler(_=>throw new InvalidOperationException("No network expected"));
        using var provider=new OneDriveProvider(new HttpClient(handler));
        var error=await Assert.ThrowsAsync<TenantBoundaryException>(()=>provider.RevokeAsync(Tokens,default));
        Assert.Equal("DRIVE_MANUAL_REVOCATION_REQUIRED",error.Code);
    }
    private static HttpResponseMessage Redirect(string url)=>new(HttpStatusCode.Found){Headers={Location=new Uri(url)}};
    private static HttpResponseMessage Json(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> action):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>action(request);}
}
