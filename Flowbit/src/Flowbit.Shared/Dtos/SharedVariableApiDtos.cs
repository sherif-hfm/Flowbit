using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flowbit.Shared.Dtos;

/// <summary>
/// Catalog and lifecycle metadata for a deployment-wide shared variable. The
/// current value is intentionally available only from the explicit value route.
/// </summary>
public sealed record SharedVariableMetadataDto(
    long Id,
    string Key,
    string DataType,
    bool IsArray,
    bool Nullable,
    string? Validation,
    string? Description,
    bool HasValue,
    string Status,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long ValueRevision = 0,
    DateTimeOffset? HistoryPrunedAt = null);

/// <summary>
/// Current-value projection. HasValue distinguishes an explicitly stored JSON
/// null from a variable for which no value has been set.
/// </summary>
public sealed record SharedVariableValueDto(
    string Key,
    bool HasValue,
    [property: JsonConverter(typeof(NullableJsonElementPreservingNullConverter))]
    JsonElement? Value,
    long Revision,
    DateTimeOffset UpdatedAt,
    long ValueRevision = 0);

/// <summary>
/// Preserves an explicit JSON null as a present <see cref="JsonElement"/> with
/// <see cref="JsonValueKind.Null"/> instead of collapsing it to a null nullable
/// wrapper. <c>HasValue</c> remains the authoritative unset/value discriminator.
/// </summary>
public sealed class NullableJsonElementPreservingNullConverter
    : JsonConverter<JsonElement?>
{
    public override bool HandleNull => true;

    public override JsonElement? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return JsonSerializer.SerializeToElement<object?>(null);
        }

        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }

    public override void Write(
        Utf8JsonWriter writer,
        JsonElement? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        value.Value.WriteTo(writer);
    }
}

public sealed record UpdateSharedVariableValueRequest(
    JsonElement Value,
    long ExpectedRevision,
    string? RequestId = null,
    string? Reason = null);

public sealed record UpdateSharedVariableDescriptionRequest(
    string? Description,
    long ExpectedRevision,
    string? RequestId = null,
    string? Reason = null);

public sealed record UpdateSharedVariableClientRequest(
    string DisplayName,
    IReadOnlyCollection<string> Scopes,
    DateTimeOffset? ExpiresAt,
    long ExpectedRevision);
