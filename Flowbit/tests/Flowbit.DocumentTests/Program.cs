using Flowbit.Infrastructure.Ai;
using Flowbit.Service.Ai;
using Flowbit.Shared.Dtos;

// Real native-tool verification, intentionally separate from the dependency-free unit test run.
var root = Path.Combine(AppContext.BaseDirectory, "Fixtures");
var initialTemp = Directory.GetDirectories(Path.GetTempPath(), "flowbit-ocr-*").ToHashSet();
using var extractor = new PdfDocumentExtractor(new());
async Task<AiDocumentExtractionDto> Read(string name, bool force = false)
{
    await using var input = File.OpenRead(Path.Combine(root, name));
    return await extractor.ExtractAsync(input, name, force, "eng+ara", default);
}
void Check(bool success, string scenario)
{
    if (!success) throw new InvalidOperationException(scenario);
    Console.WriteLine($"PASS {scenario}");
}
var text = await Read("text.pdf");
Check(text.Pages.Count == 2 && text.Pages.All(p => !p.UsedOcr), "English/Arabic digital PDF preserves pages without OCR");
Check(text.Pages[0].Text.Contains("manager approval", StringComparison.OrdinalIgnoreCase), "English digital extraction");
Check(text.Pages[1].Text.Contains("موافقة"), "Arabic digital extraction");
var scanned = await Read("scanned.pdf");
Check(scanned.Pages.Count == 2 && scanned.Pages.All(p => p.UsedOcr && p.Warnings.Count > 0), "English/Arabic scanned pages use real OCR with warnings");
Check(scanned.Pages[0].Text.Contains("manager approval", StringComparison.OrdinalIgnoreCase), "English scan recognized");
Check(scanned.Pages[1].Text.Contains("موافقة"), "Arabic scan recognized");
var mixed = await Read("mixed.pdf");
Check(mixed.Pages.Count == 2 && !mixed.Pages[0].UsedOcr && mixed.Pages[1].UsedOcr, "Mixed PDF selects extraction per page");
var forced = await Read("text.pdf", true);
Check(forced.Pages.All(p => p.UsedOcr), "Manual retry forces native OCR");
try { await Read("encrypted.pdf"); throw new InvalidOperationException("Encrypted PDF was accepted without password"); }
catch (AiDocumentException ex) { Check(ex.Code == "unreadable_pdf", "Password protected PDF returns sanitized error"); }
using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
{
    await using var input = File.OpenRead(Path.Combine(root, "scanned.pdf"));
    try
    {
        await extractor.ExtractAsync(input, "scanned.pdf", true, "eng+ara", cancellation.Token);
        throw new InvalidOperationException("Native extraction finished before the cancellation probe");
    }
    catch (OperationCanceledException) { Check(true, "Cancellation stops active native extraction"); }
}
Check(!Directory.GetDirectories(Path.GetTempPath(), "flowbit-ocr-*").Except(initialTemp).Any(), "Temporary files cleaned after native success and failure");
Console.WriteLine("All native document checks passed.");
