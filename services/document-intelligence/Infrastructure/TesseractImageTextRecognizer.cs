using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

// LOCAL adapter: operator paths, no shell or network OCR provider. Pin engine and traineddata independently.
public sealed class TesseractImageTextRecognizer(IConfiguration configuration) : IImageTextRecognizer, IDisposable
{
    public const string EngineVersion = "5.3.4";
    public const string EnglishDataSha256 = "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2";
    private readonly SemaphoreSlim admission = new(1, 1);

    public async Task<string> RecognizeAsync(byte[] png, CancellationToken cancellationToken)
    {
        SalarySlipPngExtractor.ValidatePng(png);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        string? directory = null;
        var admitted = false;
        try
        {
            admitted = await admission.WaitAsync(TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(false);
            if (!admitted) throw new OcrEngineUnavailableException();
            var executable = configuration["Monergy:DocumentProcessing:OcrExecutable"];
            var data = configuration["Monergy:DocumentProcessing:OcrDataDirectory"];
            var libraries = configuration["Monergy:DocumentProcessing:OcrLibraryDirectory"];
            if (string.IsNullOrEmpty(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
                string.IsNullOrEmpty(data) || !Path.IsPathFullyQualified(data) ||
                libraries is not null && !Path.IsPathFullyQualified(libraries)) throw new OcrEngineUnavailableException();
            var trained = await File.ReadAllBytesAsync(Path.Combine(data, "eng.traineddata"), deadline.Token).ConfigureAwait(false);
            if (Convert.ToHexStringLower(SHA256.HashData(trained)) != EnglishDataSha256) throw new OcrEngineUnavailableException();
            var version = await RunAsync(executable, ["--version"], libraries, deadline.Token).ConfigureAwait(false);
            if (version.ExitCode != 0 || version.Output.Split('\n')[0].Trim() != "tesseract " + EngineVersion)
                throw new OcrEngineUnavailableException();
            directory = Path.Combine(Path.GetTempPath(), "monergy-ocr-" + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var input = Path.Combine(directory, "source.png");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(input, options))
                await file.WriteAsync(png, deadline.Token).ConfigureAwait(false);
            var result = await RunAsync(executable, [input, "stdout", "--tessdata-dir", data, "-l", "eng", "--psm", "6", "-c", "tessedit_create_tsv=1"],
                libraries, deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode != 0) throw new DocumentRecognitionException(DocumentRecognitionFailure.Incomplete);
            return result.Output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new OcrEngineUnavailableException(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        { throw new OcrEngineUnavailableException(); }
        finally
        {
            try { if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true); }
            finally { if (admitted) admission.Release(); }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string executable, string[] arguments,
        string? libraries, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.Environment.Clear();
        start.Environment["OMP_THREAD_LIMIT"] = "1";
        start.Environment["LANG"] = "C";
        if (libraries is not null) start.Environment["LD_LIBRARY_PATH"] = libraries;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, 1_048_576, cancellationToken);
            var error = ReadBoundedAsync(process.StandardError, 16_384, cancellationToken);
            await Task.WhenAll(output, error, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (text.Length + count > limit) throw new OcrEngineUnavailableException();
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
    public void Dispose() => admission.Dispose();
}
