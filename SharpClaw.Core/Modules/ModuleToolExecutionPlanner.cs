using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Core.Modules;

/// <summary>
/// Store-neutral module tool execution planning. Hosts own module execution,
/// scoped services, process/runtime hosts, persistence, and metrics; Core owns
/// the envelope contract and the action-key-first resolution rules.
/// </summary>
public sealed class ModuleToolExecutionPlanner
{
    /// <summary>
    /// Default maximum job envelope size used when a host supplies no valid cap.
    /// </summary>
    public const int DefaultMaxEnvelopeSize = 1 * 1024 * 1024;

    /// <summary>
    /// Builds the standard module tool envelope JSON stored as job ScriptJson.
    /// </summary>
    public string CreateEnvelopeJson(
        string moduleId,
        string toolName,
        string? parametersJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var parameters = ParseParametersOrEmpty(parametersJson);
        return SerializeEnvelope(new ModuleToolEnvelope(
            moduleId,
            toolName,
            parameters));
    }

    /// <summary>
    /// Resolves a job into the module, tool, and parameters the host should
    /// execute. ActionKey resolution takes priority over embedded envelopes.
    /// </summary>
    public ModuleToolExecutionPlan BuildPlan(
        string? actionKey,
        string? scriptJson,
        int maxEnvelopeBytes,
        ModuleRegistry moduleRegistry)
    {
        ArgumentNullException.ThrowIfNull(moduleRegistry);

        var maxBytes = NormalizeMaxEnvelopeBytes(maxEnvelopeBytes);
        if (!string.IsNullOrWhiteSpace(actionKey)
            && moduleRegistry.TryResolve(
                actionKey,
                out var moduleId,
                out var toolName))
        {
            return new ModuleToolExecutionPlan(
                moduleId,
                toolName,
                ResolveActionKeyParameters(scriptJson, maxBytes),
                ResolvedFromActionKey: true);
        }

        var envelope = DeserializeEnvelope(scriptJson, maxBytes);
        return new ModuleToolExecutionPlan(
            envelope.Module,
            envelope.Tool,
            envelope.Params,
            ResolvedFromActionKey: false);
    }

    /// <summary>
    /// Serializes a module tool envelope with the standard SharpClaw shape.
    /// </summary>
    public string SerializeEnvelope(ModuleToolEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.Serialize(envelope, EnvelopeJson);
    }

    /// <summary>
    /// Deserializes a required module tool envelope from job ScriptJson.
    /// </summary>
    public ModuleToolEnvelope DeserializeEnvelope(
        string? scriptJson,
        int maxEnvelopeBytes)
    {
        if (string.IsNullOrWhiteSpace(scriptJson))
            throw new InvalidOperationException(
                "Module action requires a ScriptJson envelope.");

        var maxBytes = NormalizeMaxEnvelopeBytes(maxEnvelopeBytes);
        EnsureWithinEnvelopeLimit(scriptJson, maxBytes);

        return JsonSerializer.Deserialize<ModuleToolEnvelope>(
                scriptJson,
                EnvelopeJson)
            ?? throw new InvalidOperationException(
                "Failed to deserialize module envelope from ScriptJson.");
    }

    private static JsonElement ResolveActionKeyParameters(
        string? scriptJson,
        int maxEnvelopeBytes)
    {
        if (string.IsNullOrWhiteSpace(scriptJson))
            return ParseParametersOrEmpty(null);

        EnsureWithinEnvelopeLimit(scriptJson, maxEnvelopeBytes);

        using var doc = JsonDocument.Parse(
            scriptJson,
            DocumentOptions);
        var root = doc.RootElement;
        return TryGetNestedEnvelopeParameters(root, out var parameters)
            ? parameters.Clone()
            : root.Clone();
    }

    private static JsonElement ParseParametersOrEmpty(string? parametersJson)
    {
        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson,
            DocumentOptions);
        return doc.RootElement.Clone();
    }

    private static bool TryGetNestedEnvelopeParameters(
        JsonElement root,
        out JsonElement parameters)
    {
        parameters = default;

        var hasModule = root.TryGetProperty("module", out _)
            || root.TryGetProperty("Module", out _);
        var hasTool = root.TryGetProperty("tool", out _)
            || root.TryGetProperty("Tool", out _);
        var hasParams = root.TryGetProperty("params", out parameters)
            || root.TryGetProperty("Params", out parameters);

        return hasModule && hasTool && hasParams;
    }

    private static void EnsureWithinEnvelopeLimit(
        string scriptJson,
        int maxEnvelopeBytes)
    {
        if (scriptJson.Length > maxEnvelopeBytes)
        {
            throw new InvalidOperationException(
                $"ScriptJson exceeds maximum envelope size ({maxEnvelopeBytes} bytes).");
        }
    }

    private static int NormalizeMaxEnvelopeBytes(int maxEnvelopeBytes)
    {
        return maxEnvelopeBytes > 0
            ? maxEnvelopeBytes
            : DefaultMaxEnvelopeSize;
    }

    private static readonly JsonSerializerOptions EnvelopeJson = new()
    {
        MaxDepth = 32,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = 32,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };
}

/// <summary>
/// JSON-compatible module tool envelope stored as job ScriptJson.
/// </summary>
public sealed record ModuleToolEnvelope(
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("params")] JsonElement Params);

/// <summary>
/// Immutable host execution plan for a module tool job.
/// </summary>
public sealed record ModuleToolExecutionPlan(
    string ModuleId,
    string ToolName,
    JsonElement Parameters,
    bool ResolvedFromActionKey);
