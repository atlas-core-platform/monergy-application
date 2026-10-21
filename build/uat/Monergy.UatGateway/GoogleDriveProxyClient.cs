namespace Monergy.UatGateway;

// Folder import performs bounded provider reads, scanning and Evidence registration.
// Its request budget is separate from lightweight workspace owner reads.
public sealed class GoogleDriveProxyClient : IDisposable
{
    public HttpClient Client { get; } = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(90), MaxResponseContentBufferSize = 1_048_576 };
    public void Dispose() => Client.Dispose();
}
