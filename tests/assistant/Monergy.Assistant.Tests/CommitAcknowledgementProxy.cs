using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace Monergy.Assistant.Tests;

// Disposable test-only PostgreSQL wire proxy. It never logs or decodes credentials/query bodies.
// It forwards COMMIT to PostgreSQL, observes CommandComplete, and drops only that acknowledgement.
internal sealed class CommitAcknowledgementProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentBag<Task> sessions = [];
    private readonly Task accepting;
    private readonly string targetHost;
    private readonly int targetPort;
    private int dropped;
    public TaskCompletionSource<bool> CommitDropped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string ConnectionString { get; }

    public CommitAcknowledgementProxy(string target)
    {
        var parsed = new NpgsqlConnectionStringBuilder(target);
        targetHost = parsed.Host!; targetPort = parsed.Port;
        listener.Start();
        parsed.Host = "127.0.0.1"; parsed.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        parsed.SslMode = SslMode.Disable; parsed.Pooling = false;
        ConnectionString = parsed.ConnectionString;
        accepting = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                sessions.Add(ProxyAsync(client));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
    private async Task ProxyAsync(TcpClient client)
    {
        using (client)
        using (var server = new TcpClient())
        {
            await server.ConnectAsync(targetHost, targetPort, cancellation.Token);
            await using var input = client.GetStream(); await using var upstream = server.GetStream();
            var commitRequested = 0;
            async Task Frontend()
            {
                var startupLength = new byte[4]; await input.ReadExactlyAsync(startupLength, cancellation.Token);
                var size = BinaryPrimitives.ReadInt32BigEndian(startupLength);
                if (size is < 8 or > 65536) throw new IOException("Invalid disposable wire fixture startup.");
                var startup = new byte[size - 4]; await input.ReadExactlyAsync(startup, cancellation.Token);
                await upstream.WriteAsync(startupLength, cancellation.Token); await upstream.WriteAsync(startup, cancellation.Token);
                while (!cancellation.IsCancellationRequested)
                {
                    var frame = await ReadFrameAsync(input, cancellation.Token);
                    if (frame[0] is (byte)'Q' or (byte)'P' && frame.AsSpan(5).IndexOf("COMMIT"u8) >= 0) Interlocked.Exchange(ref commitRequested, 1);
                    await upstream.WriteAsync(frame, cancellation.Token);
                }
            }
            async Task Backend()
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var frame = await ReadFrameAsync(upstream, cancellation.Token);
                    if (frame[0] == 'C' && Volatile.Read(ref commitRequested) == 1 && frame.AsSpan(5).StartsWith("COMMIT\0"u8) &&
                        Interlocked.CompareExchange(ref dropped, 1, 0) == 0)
                    {
                        CommitDropped.TrySetResult(true); client.Close(); server.Close(); return;
                    }
                    await input.WriteAsync(frame, cancellation.Token);
                }
            }
            var front = Frontend(); var back = Backend();
            try { await Task.WhenAny(front, back); client.Close(); server.Close(); await Task.WhenAll(front, back); }
            catch (Exception failure) when (failure is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
    }
    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[5]; await stream.ReadExactlyAsync(header, ct);
        var size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (size is < 4 or > 1048576) throw new IOException("Disposable wire fixture frame exceeds bound.");
        var frame = new byte[size + 1]; header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(5), ct); return frame;
    }
    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync(); listener.Stop();
        await accepting; await Task.WhenAll(sessions); cancellation.Dispose();
    }
}
