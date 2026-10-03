using System.Text.Json;
using Flowbit.Api.Auth;
using Flowbit.Service.Ai;
using Flowbit.Service.Authoring;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace Flowbit.Api.Endpoints;

/// <summary>Non-persisting AI authoring operations inside the workflow-author authorization group.</summary>
public static class WorkflowAiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 64 };

    public static RouteGroupBuilder MapWorkflowAiEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/ai/providers", Providers).Produces<IReadOnlyList<AiProviderDto>>()
            .Produces(401).Produces(403).Produces(503);
        group.MapGet("/ai/skill", Skill).Produces(200, contentType: "application/zip")
            .Produces(401).Produces(403).Produces(503);
        group.MapPost("/ai/extract", Extract).Accepts<IFormFile>("multipart/form-data")
            .Produces<AiDocumentExtractionDto>().Produces(400).Produces(401).Produces(403).Produces(413)
            .Produces(415).Produces(429).Produces(503).Produces(504);
        group.MapPost("/ai/turn", Turn).Accepts<AiTurnRequestDto>("application/json")
            .Produces<AiTurnResultDto>().Produces(400).Produces(401).Produces(403).Produces(409).Produces(413)
            .Produces(415).Produces(429).Produces(502).Produces(503).Produces(504);
        group.MapPost("/ai/turn/stream", TurnStream).Accepts<AiTurnRequestDto>("application/json")
            .Produces<AiRunEventDto>(200, contentType: "application/x-ndjson").Produces(400).Produces(401).Produces(403).Produces(409).Produces(413)
            .Produces(415).Produces(429).Produces(502).Produces(503).Produces(504);
        group.MapPost("/validate", Validate).Accepts<JsonElement>("application/json")
            .Produces<AiValidationResultDto>().Produces(400).Produces(401).Produces(403).Produces(413).Produces(415);
        return group;
    }

    public static async Task<IResult> Providers([FromServices] IWorkflowAiAuthoringService service, CancellationToken ct)
    {
        try { return Results.Ok(await service.GetProvidersAsync(ct)); }
        catch (WorkflowAiException ex) { return Failure(ex); }
    }

    public static IResult Skill([FromServices] IAuthoringKnowledge knowledge)
    {
        // The concrete immutable package is loaded once at startup and shared with the context assembler.
        return Results.File(knowledge.GetPackageZip(), "application/zip", "flowbit-authoring.zip");
    }

    public static async Task<IResult> Turn(HttpContext http, [FromServices] IWorkflowAiAuthoringService service,
        [FromServices] IAuthorizationService authorization,
        [FromHeader(Name = "X-Flowbit-AI-Key")] string? apiKey, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 4096 || http.Request.Headers["X-Flowbit-AI-Key"].Count != 1)
                return Results.Json(new { code = "invalid_api_key", error = "Supply one AI provider key for this request." }, statusCode: 400);
            var request = await ReadJsonAsync<AiTurnRequestDto>(http.Request, 8 * 1024 * 1024, ct);
            if (request.SharedVariableKeys is { Count: > 0 } &&
                !(await authorization.AuthorizeAsync(http.User, http, SharedVariableAuthorizationPolicies.Read)).Succeeded)
                return Results.Forbid();
            return Results.Ok(await service.TurnAsync(request, apiKey, ct));
        }
        catch (WorkflowAiException ex) { return Failure(ex); }
        catch (BadHttpRequestException ex) { return BodyFailure(ex); }
        catch (JsonException) { return InvalidJson(); }
    }

    public static async Task<IResult> TurnStream(HttpContext http, [FromServices] IWorkflowAiAuthoringService service,
        [FromServices] IAuthorizationService authorization,
        [FromHeader(Name = "X-Flowbit-AI-Key")] string? apiKey, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        string? runId = null;
        long sequence = 0;
        var eventCount = 0;
        var terminal = false;
        async Task Emit(AiRunEventDto frame, CancellationToken cancellationToken)
        {
            if (terminal) throw new InvalidOperationException("An AI run already ended.");
            // Use the runner's identity and sequence for normal and endpoint-generated terminal frames.
            runId ??= frame.RunId;
            if (string.IsNullOrWhiteSpace(runId) || frame.RunId != runId || frame.Sequence <= sequence)
                throw new InvalidOperationException("Invalid AI event sequence.");
            if (eventCount >= 255 && frame.Type is not ("result" or "paused" or "error"))
                throw new WorkflowAiException("event_limit", "The AI run exceeded its event limit. Continue from the last received checkpoint.", 502);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
            if (bytes.Length > 4 * 1024 * 1024)
                throw new WorkflowAiException("response_too_large", "The AI response exceeds its supported size.", 502);
            if (!http.Response.HasStarted)
            {
                http.Response.ContentType = "application/x-ndjson; charset=utf-8";
                http.Response.Headers["X-Accel-Buffering"] = "no";
                http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                await http.Response.StartAsync(cancellationToken);
            }
            sequence = frame.Sequence;
            eventCount++;
            await http.Response.Body.WriteAsync(bytes, cancellationToken);
            await http.Response.Body.WriteAsync("\n"u8.ToArray(), cancellationToken);
            await http.Response.Body.FlushAsync(cancellationToken);
            terminal = frame.Type is "result" or "paused" or "error";
        }
        async Task<IResult> StreamFailure(string code, string message)
        {
            if (!terminal)
                await Emit(new() { RunId = runId!, Sequence = sequence + 1, Type = "error", Code = code, Message = message }, ct);
            return Results.Empty;
        }
        try
        {
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 4096 || http.Request.Headers["X-Flowbit-AI-Key"].Count != 1)
                return Results.Json(new { code = "invalid_api_key", error = "Supply one AI provider key for this request." }, statusCode: 400);
            var request = await ReadJsonAsync<AiTurnRequestDto>(http.Request, 8 * 1024 * 1024, ct);
            if (request.SharedVariableKeys is { Count: > 0 } &&
                !(await authorization.AuthorizeAsync(http.User, http, SharedVariableAuthorizationPolicies.Read)).Succeeded)
                return Results.Forbid();
            var result = await service.RunAsync(request, apiKey, Emit, ct);
            if (!terminal)
                await Emit(new() { RunId = runId ?? Guid.NewGuid().ToString("N"), Sequence = sequence + 1, Type = "result", Result = result }, ct);
            return Results.Empty;
        }
        catch (WorkflowAiException ex)
        {
            return http.Response.HasStarted ? await StreamFailure(ex.Code, ex.Message) : Failure(ex);
        }
        catch (BadHttpRequestException ex) when (!http.Response.HasStarted) { return BodyFailure(ex); }
        catch (JsonException) when (!http.Response.HasStarted) { return InvalidJson(); }
        catch (Exception) when (http.Response.HasStarted && !ct.IsCancellationRequested)
        {
            return await StreamFailure("authoring_interrupted", "AI authoring was interrupted. Continue from the last received checkpoint.");
        }
    }

    public static async Task<IResult> Validate(HttpContext http, [FromServices] IWorkflowAiAuthoringService service, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            var definition = await ReadJsonAsync<JsonElement>(http.Request, 2 * 1024 * 1024, ct);
            return Results.Ok(await service.ValidateAsync(definition, ct));
        }
        catch (WorkflowAiException ex) { return Failure(ex); }
        catch (BadHttpRequestException ex) { return BodyFailure(ex); }
        catch (JsonException) { return InvalidJson(); }
    }

    public static async Task<IResult> Extract(HttpContext http, [FromServices] IAiDocumentExtractor extractor,
        [FromServices] AiDocumentOptions options, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            if (!http.Request.HasFormContentType || !http.Request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { code = "unsupported_media_type", error = "Upload a PDF using multipart/form-data." }, statusCode: 415);
            var bodyLimit = options.MaxBytes + 64 * 1024L;
            if (http.Request.ContentLength > bodyLimit) throw new BadHttpRequestException("Body limit exceeded.", 413);
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = bodyLimit;
            var form = await http.Request.ReadFormAsync(new FormOptions
            {
                MultipartBodyLengthLimit = options.MaxBytes, ValueCountLimit = 4,
                ValueLengthLimit = 64, MultipartHeadersLengthLimit = 4096, MemoryBufferThreshold = 64 * 1024
            }, ct);
            if (form.Files.Count != 1 || form.Files.GetFile("file") is not { } file)
                return Results.Json(new { code = "invalid_upload", error = "Upload exactly one PDF in the file field." }, statusCode: 400);
            if (file.Length > options.MaxBytes) throw new BadHttpRequestException("Body limit exceeded.", 413);
            var forceOcr = bool.TryParse(form["forceOcr"], out var force) && force;
            var languages = form["languages"].FirstOrDefault() ?? "eng+ara";
            await using var stream = file.OpenReadStream();
            return Results.Ok(await extractor.ExtractAsync(stream, file.FileName, forceOcr, languages, ct));
        }
        catch (AiDocumentException ex) { return Results.Json(new { code = ex.Code, error = ex.Message }, statusCode: ex.StatusCode); }
        catch (BadHttpRequestException ex) { return BodyFailure(ex); }
        catch (InvalidDataException) { return Results.Json(new { code = "invalid_upload", error = "The upload is malformed or exceeds the configured document limits." }, statusCode: 413); }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (!request.HasJsonContentType()) throw new BadHttpRequestException("JSON required.", 415);
        if (request.ContentLength > maxBytes) throw new BadHttpRequestException("Body limit exceeded.", 413);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int count;
        while ((count = await request.Body.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + count > maxBytes) throw new BadHttpRequestException("Body limit exceeded.", 413);
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        buffer.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(buffer, JsonOptions, ct) ?? throw new JsonException();
    }

    private static IResult Failure(WorkflowAiException ex) => Results.Json(new { code = ex.Code, error = ex.Message }, statusCode: ex.StatusCode);
    private static IResult InvalidJson() => Results.Json(new { code = "invalid_json", error = "Supply a valid JSON request within the supported depth." }, statusCode: 400);
    private static IResult BodyFailure(BadHttpRequestException ex) => Results.Json(new
    {
        code = ex.StatusCode == 413 ? "request_too_large" : "unsupported_media_type",
        error = ex.StatusCode == 413 ? "The request exceeds its size limit." : "Send this request as application/json."
    }, statusCode: ex.StatusCode);
}
