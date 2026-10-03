using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Flowbit.Service.Ai;
using Flowbit.Shared.Dtos;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Flowbit.Infrastructure.Ai;

/// <summary>Bounded local PDF extraction. The uploaded document is never sent to an OCR cloud service.</summary>
public sealed class PdfDocumentExtractor : IAiDocumentExtractor, IDisposable
{
    private readonly AiDocumentOptions options;
    private readonly SemaphoreSlim slots;

    public PdfDocumentExtractor(AiDocumentOptions options)
    {
        options.Validate();
        this.options = options;
        slots = new(options.MaxConcurrentExtractions);
    }

    public async Task<AiDocumentExtractionDto> ExtractAsync(Stream input, string fileName, bool forceOcr,
        string languages, CancellationToken cancellationToken)
    {
        if (languages is not ("eng" or "ara" or "eng+ara"))
            throw new AiDocumentException("invalid_language", "Select English, Arabic, or English and Arabic for OCR.");
        if (!await slots.WaitAsync(0, cancellationToken))
            throw new AiDocumentException("extraction_busy", "Document processing is busy. Try again shortly.", 429);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var ct = timeout.Token;
        string? workspace = null;
        try
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            {
                if (bytes.Length + read > options.MaxBytes)
                    throw new AiDocumentException("document_too_large", $"PDF exceeds the {options.MaxBytes / 1024 / 1024} MiB limit.", 413);
                await bytes.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            var data = bytes.ToArray();
            if (data.Length < 5 || data.AsSpan(0, Math.Min(1024, data.Length)).IndexOf("%PDF-"u8) < 0)
                throw new AiDocumentException("invalid_pdf", "Select a valid PDF document.");

            using var document = PdfDocument.Open(data);
            if (document.NumberOfPages > options.MaxPages)
                throw new AiDocumentException("too_many_pages", $"PDF exceeds the {options.MaxPages}-page limit.", 413);
            if (document.NumberOfPages == 0)
                throw new AiDocumentException("empty_pdf", "The PDF has no pages.");

            var pages = new List<AiDocumentPageDto>();
            var totalCharacters = 0;
            var hadOcr = false;
            for (var index = 1; index <= document.NumberOfPages; index++)
            {
                ct.ThrowIfCancellationRequested();
                var page = document.GetPage(index);
                var text = ContentOrderTextExtractor.GetText(page).Trim();
                var warnings = new List<string>();
                // Sparse text plus an image commonly means a scanned page with a digital page number.
                var useOcr = forceOcr || string.IsNullOrWhiteSpace(text) ||
                    (text.Count(char.IsLetterOrDigit) < 40 && page.NumberOfImages > 0);
                if (useOcr)
                {
                    hadOcr = true;
                    if (workspace is null)
                    {
                        workspace = Directory.CreateTempSubdirectory("flowbit-ocr-").FullName;
                        if (!OperatingSystem.IsWindows())
                            File.SetUnixFileMode(workspace, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                        await File.WriteAllBytesAsync(Path.Combine(workspace, "document.pdf"), data, ct);
                    }
                    var imageRoot = Path.Combine(workspace, "page");
                    await RunAsync(options.PdfToPpmPath,
                        ["-f", index.ToString(CultureInfo.InvariantCulture), "-l", index.ToString(CultureInfo.InvariantCulture),
                         "-singlefile", "-r", "200", "-scale-to", options.MaxRasterDimension.ToString(CultureInfo.InvariantCulture),
                         "-png", Path.Combine(workspace, "document.pdf"), imageRoot], 4096, ct);
                    var imagePath = imageRoot + ".png";
                    if (!File.Exists(imagePath) || new FileInfo(imagePath).Length > 80 * 1024 * 1024)
                        throw new AiDocumentException("raster_failed", "The PDF page could not be rendered within the image limit.");
                    text = (await RunAsync(options.TesseractPath,
                        [imagePath, "stdout", "-l", languages, "--psm", "3"], options.MaxCharacters + 1, ct)).Trim();
                    File.Delete(imagePath);
                    warnings.Add("OCR text may contain errors. Review names, numbers, tables, and reading order before generation.");
                }
                if (string.IsNullOrWhiteSpace(text)) warnings.Add("No readable text was found on this page.");
                if (text.Length + totalCharacters > options.MaxCharacters)
                    throw new AiDocumentException("too_much_text", $"Extracted text exceeds the {options.MaxCharacters:N0}-character limit. Split the document.", 413);
                totalCharacters += text.Length;
                pages.Add(new(index, text, useOcr, warnings));
            }
            var notices = new List<string> { "Review extracted text before sending it to the selected AI provider. Diagrams and handwriting may not be represented." };
            if (hadOcr) notices.Add("Scanned pages were processed locally using OCR.");
            ct.ThrowIfCancellationRequested();
            return new(Path.GetFileName(fileName.Replace('\\', '/')), pages, notices);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiDocumentException("extraction_timeout", "Document processing timed out. Try a smaller document.", 504);
        }
        catch (AiDocumentException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Parser diagnostics may contain source text, paths, or passwords: do not expose them.
            throw new AiDocumentException("unreadable_pdf", "The PDF could not be read. Check that it is not damaged or password protected.");
        }
        finally
        {
            if (workspace is not null)
            {
                var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
                var target = Path.GetFullPath(workspace);
                if (Path.GetDirectoryName(target)?.TrimEnd(Path.DirectorySeparatorChar) == temporaryRoot.TrimEnd(Path.DirectorySeparatorChar)
                    && Path.GetFileName(target).StartsWith("flowbit-ocr-", StringComparison.Ordinal))
                {
                    // This path was created by this invocation and is never supplied by the caller.
                    try { Directory.Delete(target, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            slots.Release();
        }
    }

    private async Task<string> RunAsync(string executable, string[] arguments, int outputLimit, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.ProcessTimeoutSeconds));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["OMP_THREAD_LIMIT"] = "1";
        try { process.Start(); }
        catch (Win32Exception)
        {
            throw new AiDocumentException("ocr_unavailable", "OCR requires Poppler and Tesseract with English/Arabic language data on the API host. Configure WorkflowAiDocuments executable paths.", 503);
        }
        using var cancellation = timeout.Token.Register(() => Kill(process));
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, outputLimit, process, timeout.Token);
            var errors = ReadBoundedAsync(process.StandardError, 16_384, process, timeout.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0)
                throw new AiDocumentException("ocr_failed", "OCR failed. Verify the configured OCR tools/language data or retry with another document.");
            return await output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiDocumentException("ocr_timeout", "An OCR page exceeded its processing time limit. Try a smaller or clearer scan.", 504);
        }
        finally
        {
            Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, Process process, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, ct)) != 0)
        {
            if (result.Length + count > limit)
            {
                Kill(process);
                throw new AiDocumentException("ocr_output_limit", "OCR output exceeded its processing limit. Split the document.", 413);
            }
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    public void Dispose() => slots.Dispose();
}
