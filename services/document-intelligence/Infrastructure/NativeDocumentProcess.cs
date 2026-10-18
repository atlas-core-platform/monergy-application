using System.Diagnostics;
using System.Text;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

internal sealed class NativeDocumentOutputLimitException() : Exception("The native document output exceeded its bound.");
internal sealed record NativeDocumentResult(int ExitCode, byte[] Output, string Error)
{
    public string Text => Encoding.UTF8.GetString(Output);
}

// Shared bounded process transport. Neither arguments nor diagnostics reach customer responses or logs.
internal static class NativeDocumentProcess
{
    public static async Task<NativeDocumentResult> RunAsync(string executable, string[] arguments,
        string? libraries, int outputLimit, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.Environment.Clear();
        start.Environment["OMP_THREAD_LIMIT"] = "1";
        start.Environment["LANG"] = "C";
        if (libraries is not null) start.Environment["LD_LIBRARY_PATH"] = libraries;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("The document process did not start.");
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, outputLimit, stop);
            var error = ReadBoundedAsync(process.StandardError.BaseStream, 16_384, stop);
            await Task.WhenAll(output, error, process.WaitForExitAsync(stop.Token)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new NativeDocumentResult(process.ExitCode, await output.ConfigureAwait(false),
                Encoding.UTF8.GetString(await error.ConfigureAwait(false)));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationTokenSource stop)
    {
        try
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) != 0)
            {
                if (bytes.Length + count > limit) throw new NativeDocumentOutputLimitException();
                bytes.Write(buffer, 0, count);
            }
            return bytes.ToArray();
        }
        catch
        {
            // Stop the other pipe and the process wait immediately if either output exceeds its limit.
            await stop.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }
}
