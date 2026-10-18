using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

public interface IPdfPageRenderer
{
    Task<byte[]> RenderAsync(byte[] pdf, CancellationToken cancellationToken);
}

public sealed class SalarySlipPdfExtractor(IPdfPageRenderer renderer, IImageTextRecognizer recognizer) : IDocumentExtractor
{
    public const string ExtractionVersion = "salary-slip-pdf-ocr/1.0.0";
    public async Task<IReadOnlyList<ValidatedSourceFact>> ExtractAsync(
        EvidenceContent content, string processingId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = content.BinaryContent;
        if (content.Reference.ContentType != "application/pdf" || content.Content.Length != 0 || bytes is null ||
            !string.Equals(content.Reference.ContentSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
            throw Unsupported();
        ValidatePdf(bytes);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var png = await renderer.RenderAsync(bytes, deadline.Token).ConfigureAwait(false);
            SalarySlipPngExtractor.ValidatePng(png);
            var text = await recognizer.RecognizeAsync(png, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            // The raster is transient. Facts retain the original PDF reference, hash-bound version and PDF extraction version.
            return SalarySlipPngExtractor.CreateCandidates(content, processingId, text, ExtractionVersion);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new OcrEngineUnavailableException(); }
    }
    public static void ValidatePdf(byte[] pdf)
    {
        if (pdf.Length is < 8 or > 1_048_576 || !pdf.AsSpan().StartsWith("%PDF-"u8)) throw Unsupported();
    }
    internal static DocumentRecognitionException Unsupported() => new(DocumentRecognitionFailure.UnsupportedPdf);
}

// LOCAL-only Poppler adapter: one unlocked page, bounded raster, no PDF passwords or remote resource fetches supplied.
public sealed class PopplerPdfPageRenderer(IConfiguration configuration) : IPdfPageRenderer, IDisposable
{
    public const string EngineVersion = "24.02.0";
    private readonly SemaphoreSlim admission = new(1, 1);
    private static readonly Regex PageSize = new(@"^([0-9]+(?:\.[0-9]+)?) x ([0-9]+(?:\.[0-9]+)?) pts(?: \([^\r\n]*\))?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    public async Task<byte[]> RenderAsync(byte[] pdf, CancellationToken cancellationToken)
    {
        SalarySlipPdfExtractor.ValidatePdf(pdf);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        string? directory = null;
        var admitted = false;
        try
        {
            admitted = await admission.WaitAsync(TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(false);
            if (!admitted) throw new OcrEngineUnavailableException();
            var info = Executable("PdfInfoExecutable");
            var render = Executable("PdfRenderExecutable");
            var libraries = configuration["Monergy:DocumentProcessing:OcrLibraryDirectory"];
            if (libraries is not null && !Path.IsPathFullyQualified(libraries)) throw new OcrEngineUnavailableException();
            await RequireVersionAsync(info, "pdfinfo", libraries, deadline.Token).ConfigureAwait(false);
            await RequireVersionAsync(render, "pdftoppm", libraries, deadline.Token).ConfigureAwait(false);
            directory = Path.Combine(Path.GetTempPath(), "monergy-pdf-" + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var input = Path.Combine(directory, "source.pdf");
            var options = new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(input, options))
                await file.WriteAsync(pdf, deadline.Token).ConfigureAwait(false);
            var metadata = await NativeDocumentProcess.RunAsync(info, [input], libraries, 65_536, deadline.Token).ConfigureAwait(false);
            if (metadata.ExitCode != 0 || metadata.Error.Length != 0) throw SalarySlipPdfExtractor.Unsupported();
            RequireSinglePage(metadata.Text);
            var raster = await NativeDocumentProcess.RunAsync(render,
                ["-png", "-singlefile", "-scale-to", "2500", "-r", "144", input], libraries, 1_048_576, deadline.Token).ConfigureAwait(false);
            if (raster.ExitCode != 0 || raster.Error.Length != 0) throw SalarySlipPdfExtractor.Unsupported();
            SalarySlipPngExtractor.ValidatePng(raster.Output);
            cancellationToken.ThrowIfCancellationRequested();
            return raster.Output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new OcrEngineUnavailableException(); }
        catch (NativeDocumentOutputLimitException) { throw SalarySlipPdfExtractor.Unsupported(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        { throw new OcrEngineUnavailableException(); }
        finally
        {
            try { if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true); }
            finally { if (admitted) admission.Release(); }
        }
    }

    private string Executable(string key)
    {
        var path = configuration["Monergy:DocumentProcessing:" + key];
        return !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) && File.Exists(path)
            ? path : throw new OcrEngineUnavailableException();
    }
    private static async Task RequireVersionAsync(string executable, string name, string? libraries, CancellationToken cancellationToken)
    {
        var result = await NativeDocumentProcess.RunAsync(executable, ["-v"], libraries, 1024, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Error.Split('\n')[0].Trim() != name + " version " + EngineVersion)
            throw new OcrEngineUnavailableException();
    }
    private static void RequireSinglePage(string metadata)
    {
        var lines = metadata.Split('\n');
        string Field(string label)
        {
            var rows = lines.Where(line => line.StartsWith(label + ":", StringComparison.Ordinal)).ToArray();
            return rows.Length == 1 ? rows[0][(label.Length + 1)..].Trim() : throw SalarySlipPdfExtractor.Unsupported();
        }
        if (Field("Pages") != "1" || Field("Encrypted") != "no" || Field("JavaScript") != "no" || Field("Form") != "none")
            throw SalarySlipPdfExtractor.Unsupported();
        var size = PageSize.Match(Field("Page size"));
        if (!size.Success || !decimal.TryParse(size.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var width) ||
            !decimal.TryParse(size.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var height) ||
            width is < 36 or > 1440 || height is < 36 or > 1440) throw SalarySlipPdfExtractor.Unsupported();
    }
    public void Dispose() => admission.Dispose();
}
