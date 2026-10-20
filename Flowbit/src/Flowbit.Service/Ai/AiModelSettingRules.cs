using System.Text.Json;
using System.Text.Json.Serialization;
using Flowbit.Service.Services;

namespace Flowbit.Service.Ai;

/// <summary>
/// The editor assistant's AI models, stored as one JSON array in engine setting <see cref="LogicalKey"/>. The same rules check
/// administrator input and the stored list, so the runtime never uses an endpoint or key the AI models page would refuse.
/// </summary>
public static class AiModelSettingRules
{
    public const string SettingNamespace = "AiAuthoring";
    public const string SettingKey = "Models";
    public const string LogicalKey = SettingNamespace + "." + SettingKey;
    public const string SettingDescription =
        "AI models of the editor assistant, including their provider API keys. Managed on the AI models page.";
    public const int MaxModels = 100;
    public const int MaxNameLength = 100;
    public const int MaxEndpointLength = 2000;
    public const int MaxModelLength = 200;
    /// <summary>
    /// Keys are matched as substrings to keep them out of conversation state, workflows and replies, so a short value such as
    /// "none" would match ordinary text. A keyless local endpoint takes a unique placeholder of this length instead.
    /// </summary>
    public const int MinApiKeyLength = 16;
    public const int MaxApiKeyLength = 4096;
    private const int MaxIdLength = 64;

    private static readonly JsonSerializerOptions StoredOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16
    };

    /// <summary>True for the reserved setting, compared case-insensitively, including a legacy row with a blank namespace.</summary>
    public static bool IsReservedEngineSetting(string? settingNamespace, string key)
    {
        var name = settingNamespace?.Trim();
        return IsReservedEngineSetting(string.IsNullOrEmpty(name) ? key : name + "." + key.Trim());
    }

    /// <summary>True when a dotted engine-setting key names the reserved setting.</summary>
    public static bool IsReservedEngineSetting(string? logicalKey) =>
        string.Equals(logicalKey?.Trim(), LogicalKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The base URL of an OpenAI-compatible chat-completions API, normalized with a trailing slash. HTTPS is required, except plain
    /// HTTP on a loopback host for a local server; user information, a query or a fragment are refused.
    /// </summary>
    public static bool TryNormalizeEndpoint(string? value, out string endpoint, out string error)
    {
        endpoint = "";
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "endpoint is required.";
            return false;
        }
        if (text.Length > MaxEndpointLength)
        {
            error = $"endpoint must not exceed {MaxEndpointLength} characters.";
            return false;
        }
        if (!Uri.TryCreate(text.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            error = "endpoint must be an absolute HTTPS URL, or HTTP on a loopback host such as http://127.0.0.1:11434/v1/.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "endpoint must not contain user information, a query, or a fragment.";
            return false;
        }
        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            error = "endpoint must be the API base URL, such as https://opencode.ai/zen/v1/, without chat/completions.";
            return false;
        }
        endpoint = uri.AbsoluteUri;
        error = "";
        return true;
    }

    /// <summary>Null, blank or "none" sends no reasoning effort; otherwise low, medium or high, in any letter case.</summary>
    public static bool TryNormalizeReasoningEffort(string? value, out string? effort)
    {
        var text = value?.Trim().ToLowerInvariant();
        effort = string.IsNullOrEmpty(text) || text == "none" ? null : text;
        return effort is null or "low" or "medium" or "high";
    }

    /// <summary>Checks and normalizes an administrator's model settings. Messages name the field, never its value.</summary>
    internal static AiModelDefinition NormalizeDefinition(string? name, string? endpoint, string? model, string? reasoningEffort)
    {
        var normalizedName = RequireText(name, "name", MaxNameLength);
        if (!TryNormalizeEndpoint(endpoint, out var normalizedEndpoint, out var error))
            throw new WorkflowDomainException(error);
        var normalizedModel = RequireText(model, "model", MaxModelLength);
        if (!TryNormalizeReasoningEffort(reasoningEffort, out var effort))
            throw new WorkflowDomainException("reasoningEffort must be low, medium, high, or none.");
        return new(normalizedName, normalizedEndpoint, normalizedModel, effort);
    }

    /// <summary>The trimmed key, or null when no key was supplied and none is required, which keeps the stored key.</summary>
    internal static string? NormalizeApiKey(string? value, bool required)
    {
        var key = value?.Trim();
        if (string.IsNullOrEmpty(key))
            return required ? throw new WorkflowDomainException("apiKey is required.") : null;
        if (!IsValidKey(key))
            throw new WorkflowDomainException(
                $"apiKey must contain {MinApiKeyLength} to {MaxApiKeyLength} characters and no control characters. " +
                $"For an endpoint without keys, enter a unique placeholder of at least {MinApiKeyLength} characters.");
        return key;
    }

    /// <summary>
    /// Reads the stored array; a missing or blank value is an empty list. False when the value is not a valid model list. Unknown
    /// fields are kept so that a later write preserves them.
    /// </summary>
    internal static bool TryParse(string? value, out List<StoredAiModel> models)
    {
        models = [];
        if (string.IsNullOrWhiteSpace(value)) return true;
        List<StoredAiModel?>? stored;
        try { stored = JsonSerializer.Deserialize<List<StoredAiModel?>>(value, StoredOptions); }
        catch (JsonException) { return false; }
        if (stored is null || stored.Count > MaxModels) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<StoredAiModel>(stored.Count);
        foreach (var model in stored)
        {
            if (model is null || !IsValidText(model.Id, MaxIdLength) || !ids.Add(model.Id)
                || !IsValidText(model.Name?.Trim(), MaxNameLength) || !names.Add(model.Name!.Trim())
                || !TryNormalizeEndpoint(model.Endpoint, out var endpoint, out _)
                || !IsValidText(model.Model?.Trim(), MaxModelLength)
                || !TryNormalizeReasoningEffort(model.ReasoningEffort, out var effort)
                || model.ApiKey is null || !IsValidKey(model.ApiKey.Trim()) || model.Revision < 1)
                return false;
            model.Name = model.Name.Trim();
            model.Endpoint = endpoint;
            model.Model = model.Model!.Trim();
            model.ReasoningEffort = effort;
            model.ApiKey = model.ApiKey.Trim();
            result.Add(model);
        }
        models = result;
        return true;
    }

    internal static string Serialize(IReadOnlyList<StoredAiModel> models) => JsonSerializer.Serialize(models, StoredOptions);

    private static bool IsValidKey(string key) =>
        key.Length is >= MinApiKeyLength and <= MaxApiKeyLength && !key.Any(char.IsControl);

    private static bool IsValidText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    private static string RequireText(string? value, string field, int maximum)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) throw new WorkflowDomainException($"{field} is required.");
        if (text.Length > maximum) throw new WorkflowDomainException($"{field} must not exceed {maximum} characters.");
        if (text.Any(char.IsControl)) throw new WorkflowDomainException($"{field} must not contain control characters.");
        return text;
    }

    internal sealed record AiModelDefinition(string Name, string Endpoint, string Model, string? ReasoningEffort);

    /// <summary>One stored model. Its key is write-only: map it to <see cref="AiModelConnection"/> only, never to a response.</summary>
    internal sealed class StoredAiModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string Model { get; set; } = "";
        public string? ReasoningEffort { get; set; }
        public string ApiKey { get; set; } = "";
        public long Revision { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
    }
}
