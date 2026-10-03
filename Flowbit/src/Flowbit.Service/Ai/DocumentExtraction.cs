using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Ai;

public interface IAiDocumentExtractor
{
    Task<AiDocumentExtractionDto> ExtractAsync(Stream input, string fileName, bool forceOcr,
        string languages, CancellationToken cancellationToken);
}

public sealed class AiDocumentOptions
{
    public const string SectionName = "WorkflowAiDocuments";
    public int MaxBytes { get; set; } = 20 * 1024 * 1024;
    public int MaxPages { get; set; } = 100;
    public int MaxCharacters { get; set; } = 200_000;
    public int TimeoutSeconds { get; set; } = 300;
    public int ProcessTimeoutSeconds { get; set; } = 45;
    public int MaxRasterDimension { get; set; } = 4096;
    public int MaxConcurrentExtractions { get; set; } = 2;
    public string PdfToPpmPath { get; set; } = "pdftoppm";
    public string TesseractPath { get; set; } = "tesseract";

    public void Validate()
    {
        if (MaxBytes is < 1024 or > 100 * 1024 * 1024 || MaxPages is < 1 or > 500 ||
            MaxCharacters is < 1000 or > 2_000_000 || TimeoutSeconds is < 1 or > 1800 ||
            ProcessTimeoutSeconds is < 1 or > 300 || MaxRasterDimension is < 512 or > 8192 ||
            MaxConcurrentExtractions is < 1 or > 16 || string.IsNullOrWhiteSpace(PdfToPpmPath) ||
            string.IsNullOrWhiteSpace(TesseractPath))
            throw new InvalidOperationException("WorkflowAiDocuments limits or executable paths are invalid.");
    }
}

/// <summary>Safe client-visible extraction failure; never includes document contents or process output.</summary>
public sealed class AiDocumentException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
