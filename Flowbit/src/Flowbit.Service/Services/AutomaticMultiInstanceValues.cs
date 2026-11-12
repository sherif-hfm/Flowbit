using System.Buffers;
using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>Bounded, deterministic body inputs and index-ordered local result envelopes.</summary>
public static class AutomaticMultiInstanceValues
{
    public const string SourceInvalid = "multi_instance_source_invalid";
    public const string ResultInvalid = "multi_instance_result_invalid";
    public const string ResultTooLarge = "multi_instance_result_too_large";

    public static IReadOnlyList<JsonElement> CopyCollection(JsonElement source, VariableModel declaration, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        if (!declaration.IsArray || source.ValueKind != JsonValueKind.Array)
            throw new AutomaticMultiInstanceValueException(SourceInvalid);
        var result = new List<JsonElement>();
        foreach (var item in source.EnumerateArray())
        {
            // Refuse the limit+1 element before copying it. Never normalize usernames,
            // remove duplicates, or let whole-variable nullable admit a null source.
            if (result.Count == maximum || !TypedOutputValueValidator.IsValid(item, declaration.DataType, false))
                throw new AutomaticMultiInstanceValueException(SourceInvalid);
            result.Add(item.Clone());
        }
        return result;
    }

    public static JsonElement CreateInputSnapshot(IReadOnlyList<JsonElement> collection,
        IReadOnlyDictionary<string, JsonElement> instanceInputs, DateTimeOffset initializedAt,
        string? sharedSourceKey = null, long? sharedSourceValueVersion = null,
        NodeExecutionActorRecord? causalActor = null, SequenceFlowInfoSnapshot? flowInfo = null)
    {
        var value = JsonSerializer.SerializeToElement(new
        {
            collection, flowInfo,
            instanceInputs,
            initializedAt,
            sharedSourceKey,
            sharedSourceValueVersion,
            causalActor
        });
        RequireSize(value, AutomaticMultiInstanceRules.MaxSnapshotBytes, SourceInvalid);
        // Every copied item must also fit its minimum successful result envelope.
        BuildAggregate(collection, Enumerable.Range(0, collection.Count)
            .Select(index => new AutomaticMultiInstanceResultRow(index, "completed", EmptyObject)).ToArray());
        return value;
    }

    public static Dictionary<string, JsonElement> CreateItemContext(JsonElement bodySnapshot, int index,
        IReadOnlyDictionary<string, JsonElement> stagedContext, string collectionAlias)
    {
        var collection = bodySnapshot.GetProperty("collection");
        if (index < 0 || index >= collection.GetArrayLength())
            throw new AutomaticMultiInstanceValueException(SourceInvalid);
        var context = stagedContext.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        foreach (var property in bodySnapshot.GetProperty("instanceInputs").EnumerateObject())
            context[property.Name] = property.Value.Clone();
        // Frozen values always win over newly captured shared/config/time values.
        context[collectionAlias] = collection.Clone();
        context["mi.item"] = collection[index].Clone();
        context["mi.index"] = JsonSerializer.SerializeToElement(index);
        context["mi.total"] = JsonSerializer.SerializeToElement(collection.GetArrayLength());
        return context;
    }

    public static JsonElement BuildAggregate(IReadOnlyList<JsonElement> collection,
        IReadOnlyList<AutomaticMultiInstanceResultRow> rows, bool partial = false)
    {
        if (rows.Count != collection.Count || rows.Select(row => row.Index).Distinct().Count() != rows.Count)
            throw new AutomaticMultiInstanceValueException(ResultInvalid);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        foreach (var row in rows.OrderBy(row => row.Index))
        {
            if (row.Index < 0 || row.Index >= collection.Count
                || row.Status != "completed" && (!partial || row.Status is not ("failed" or "cancelled"))
                || row.Status == "completed" && row.Variables.ValueKind != JsonValueKind.Object)
                throw new AutomaticMultiInstanceValueException(ResultInvalid);
            writer.WriteStartObject();
            writer.WriteString("kind", "item");
            writer.WriteNumber("index", row.Index);
            writer.WritePropertyName("item");
            collection[row.Index].WriteTo(writer);
            writer.WriteString("status", row.Status);
            writer.WritePropertyName("variables");
            (row.Status == "completed" ? row.Variables : EmptyObject).WriteTo(writer);
            writer.WriteEndObject();
            writer.Flush();
            if (buffer.WrittenCount + 1 > AutomaticMultiInstanceRules.MaxResultBytes)
                throw new AutomaticMultiInstanceValueException(ResultTooLarge);
        }
        writer.WriteEndArray();
        writer.Flush();
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    public static int RequireSize(JsonElement value, int limit, string errorCode)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value).Length;
        if (bytes > limit) throw new AutomaticMultiInstanceValueException(errorCode);
        return bytes;
    }

    private static JsonElement EmptyObject => JsonSerializer.SerializeToElement(new { });
}

public sealed record AutomaticMultiInstanceResultRow(int Index, string Status, JsonElement Variables);

/// <summary>Value-free diagnostics suitable for incidents, boundaries and terminal faults.</summary>
public sealed class AutomaticMultiInstanceValueException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}
