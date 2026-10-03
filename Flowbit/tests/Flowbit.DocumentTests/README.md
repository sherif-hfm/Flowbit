# Native PDF verification

This executable checks the actual PdfPig/Poppler/Tesseract pipeline using synthetic
English and Arabic digital, scanned, mixed, and password-protected PDFs. It checks
page selection, recognized phrases, force-OCR retry, warnings, and temporary-file
cleanup. Ordinary unit tests separately cover malformed input, byte/page/text limits,
language validation, missing dependencies, and cancellation before work.

Run from the repository root. The following arguments work in PowerShell and Bash:

```text
docker build -f Flowbit/src/Flowbit.Api/Dockerfile -t flowbit-api:ai-verification .
dotnet publish Flowbit/tests/Flowbit.DocumentTests -c Release -o artifacts/document-check /p:UseAppHost=false
```

PowerShell:

```powershell
$documentCheck = (Resolve-Path artifacts/document-check).Path
docker run --rm --mount "type=bind,source=$documentCheck,target=/checks,readonly" --entrypoint dotnet flowbit-api:ai-verification /checks/Flowbit.DocumentTests.dll
```

Bash:

```bash
docker run --rm --mount "type=bind,source=$(pwd)/artifacts/document-check,target=/checks,readonly" --entrypoint dotnet flowbit-api:ai-verification /checks/Flowbit.DocumentTests.dll
```

The image contains the native tools and both language packs; the check makes no
database or provider calls. On a host with those tools already installed, run
`dotnet run --project Flowbit/tests/Flowbit.DocumentTests -c Release` directly.
Fixtures contain only synthetic requirements. They are printed text; passing these
checks is not evidence of reliable handwriting, table, or diagram interpretation.
