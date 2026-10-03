using System.Text;
using Flowbit.Infrastructure.Ai;
using Flowbit.Service.Ai;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Flowbit.Tests;

public sealed class AiDocumentExtractionTests
{
    [Fact]
    public async Task TextPdfRetainsPagesWithoutInvokingOcr()
    {
        using var extractor = new PdfDocumentExtractor(new() { PdfToPpmPath = "deliberately-missing", TesseractPath = "deliberately-missing" });
        using var input = new MemoryStream(Pdf("Purchase request requires manager approval.", "Finance approves the payment."));
        var result = await extractor.ExtractAsync(input, "../private/requirements.pdf", false, "eng+ara", default);
        Assert.Equal("requirements.pdf", result.FileName);
        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(1, result.Pages[0].Page);
        Assert.Contains("manager approval", result.Pages[0].Text);
        Assert.Contains("Finance", result.Pages[1].Text);
        Assert.All(result.Pages, page => Assert.False(page.UsedOcr));
    }

    [Fact]
    public async Task MalformedInputReturnsSafeError()
    {
        using var extractor = new PdfDocumentExtractor(new());
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("private customer information"));
        var error = await Assert.ThrowsAsync<AiDocumentException>(() => extractor.ExtractAsync(input, "secret.pdf", false, "eng", default));
        Assert.Equal("invalid_pdf", error.Code);
        Assert.DoesNotContain("customer", error.Message);
    }

    [Fact]
    public async Task ForceOcrReportsMissingDependencyInsteadOfPretendingItSucceeded()
    {
        using var extractor = new PdfDocumentExtractor(new() { PdfToPpmPath = "flowbit-nonexistent-poppler-command" });
        using var input = new MemoryStream(Pdf("A normal printed English document."));
        var before = Directory.GetDirectories(Path.GetTempPath(), "flowbit-ocr-*").ToHashSet();
        var error = await Assert.ThrowsAsync<AiDocumentException>(() => extractor.ExtractAsync(input, "scan.pdf", true, "eng+ara", default));
        Assert.Equal("ocr_unavailable", error.Code);
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "flowbit-ocr-*").Except(before));
    }

    [Fact]
    public async Task PageLimitIsCheckedBeforePageExtraction()
    {
        using var extractor = new PdfDocumentExtractor(new() { MaxPages = 1 });
        using var input = new MemoryStream(Pdf("First page", "Second page"));
        var error = await Assert.ThrowsAsync<AiDocumentException>(() => extractor.ExtractAsync(input, "long.pdf", false, "eng", default));
        Assert.Equal("too_many_pages", error.Code);
        Assert.Equal(413, error.StatusCode);
    }

    [Fact]
    public async Task ByteLimitAlsoAppliesToNonSeekableInput()
    {
        using var extractor = new PdfDocumentExtractor(new() { MaxBytes = 1024 });
        using var input = new NonSeekableStream(new byte[2048]);
        var error = await Assert.ThrowsAsync<AiDocumentException>(() => extractor.ExtractAsync(input, "long.pdf", false, "eng", default));
        Assert.Equal("document_too_large", error.Code);
    }

    [Fact]
    public async Task TextLimitDoesNotSilentlyTruncate()
    {
        using var extractor = new PdfDocumentExtractor(new() { MaxCharacters = 1000 });
        using var input = new MemoryStream(Pdf(string.Join(" ", Enumerable.Repeat("Approval", 150))));
        var error = await Assert.ThrowsAsync<AiDocumentException>(() => extractor.ExtractAsync(input, "long.pdf", false, "eng", default));
        Assert.Equal("too_much_text", error.Code);
    }

    [Theory]
    [InlineData("eng;curl example.com")]
    [InlineData("../../eng")]
    [InlineData("fra")]
    public async Task OcrLanguageCannotInjectArguments(string language)
    {
        using var extractor = new PdfDocumentExtractor(new());
        using var input = new MemoryStream(Pdf("Text"));
        var error = await Assert.ThrowsAsync<AiDocumentException>(() => extractor.ExtractAsync(input, "input.pdf", true, language, default));
        Assert.Equal("invalid_language", error.Code);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsSuccess()
    {
        using var extractor = new PdfDocumentExtractor(new());
        using var input = new MemoryStream(Pdf("Text"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync(input, "input.pdf", false, "eng", cancellation.Token));
    }

    internal static byte[] Pdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(text, 12, new PdfPoint(30, 750), font);
        }
        return builder.Build();
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
