using System.ComponentModel;
using System.Security.Cryptography;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

// LOCAL adapter: operator paths, no shell or network OCR provider. Pin engine and traineddata independently.
public sealed class TesseractImageTextRecognizer(IConfiguration configuration) : IImageTextRecognizer, IDisposable
{
    public const string EngineVersion = "5.3.4";
    public const string EnglishDataSha256 = "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2";
    private readonly SemaphoreSlim admission = new(1, 1);

    public async Task<string> RecognizeAsync(byte[] image, CancellationToken cancellationToken)
    {
        var jpeg = image.AsSpan().StartsWith(new byte[] { 0xff, 0xd8 });
        if (jpeg) SalarySlipJpegExtractor.ValidateJpeg(image);
        else SalarySlipPngExtractor.ValidatePng(image);
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
            var version = await NativeDocumentProcess.RunAsync(executable, ["--version"], libraries, 1024, deadline.Token).ConfigureAwait(false);
            if (version.ExitCode != 0 || version.Text.Split('\n')[0].Trim() != "tesseract " + EngineVersion)
                throw new OcrEngineUnavailableException();
            directory = Path.Combine(Path.GetTempPath(), "monergy-ocr-" + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var input = Path.Combine(directory, jpeg ? "source.jpg" : "source.png");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(input, options))
                await file.WriteAsync(image, deadline.Token).ConfigureAwait(false);
            var result = await NativeDocumentProcess.RunAsync(executable, [input, "stdout", "--tessdata-dir", data, "-l", "eng", "--psm", "6", "-c", "tessedit_create_tsv=1"],
                libraries, 1_048_576, deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (jpeg && (result.ExitCode != 0 || result.Error.Length != 0)) throw SalarySlipJpegExtractor.Unsupported();
            if (result.ExitCode != 0) throw new DocumentRecognitionException(DocumentRecognitionFailure.Incomplete);
            return result.Text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new OcrEngineUnavailableException(); }
        catch (NativeDocumentOutputLimitException) { throw new OcrEngineUnavailableException(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        { throw new OcrEngineUnavailableException(); }
        finally
        {
            try { if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true); }
            finally { if (admitted) admission.Release(); }
        }
    }

    public void Dispose() => admission.Dispose();
}
