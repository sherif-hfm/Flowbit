namespace Flowbit.Shared.Dtos;

/// <summary>Extracted text and extraction diagnostics for one numbered PDF page.</summary>
public sealed record AiDocumentPageDto(int Page, string Text, bool UsedOcr, IReadOnlyList<string> Warnings);

/// <summary>Temporary, editable document text. No document is retained by the extraction service.</summary>
public sealed record AiDocumentExtractionDto(string FileName, IReadOnlyList<AiDocumentPageDto> Pages, IReadOnlyList<string> Warnings);
