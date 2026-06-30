using System.Text.Json;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Modules;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral parser for native provider tool calls emitted during chat.
/// </summary>
public sealed class ChatNativeToolCallParser
{
    /// <summary>
    /// Provider-facing tool-result text used when a tool call cannot be
    /// resolved or parsed.
    /// </summary>
    public const string MalformedToolCallResult =
        "Error: unrecognized tool or malformed arguments.";

    /// <summary>
    /// Resolves a native provider tool call into the parsed SharpClaw job
    /// call representation, including optional module-owned resource-id
    /// extraction when no standard resource id argument is present.
    /// </summary>
    public async Task<ParsedChatToolCall?> ResolveAsync(
        ChatNativeToolCallResolutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = BuildParsePlan(
            request.ToolCall,
            request.ModuleRegistry,
            request.ExecutionPlanner);
        if (plan is null)
            return null;

        request.Trace?.Invoke(
            $"[ParseToolCall] Module tool: {plan.ActionKey} \u2192 {plan.ModuleId}.{plan.ToolName}");

        Guid? resourceId = plan.DirectResourceId;
        if (resourceId is null && plan.RequiresResourceExtractor)
        {
            var extractor = request.ModuleRegistry.GetResourceIdExtractor(
                plan.ActionKey);
            if (extractor is not null
                && request.ExtractResourceIdAsync is not null)
            {
                resourceId = await request.ExtractResourceIdAsync(
                    new ChatNativeToolCallResourceExtraction(
                        plan.ActionKey,
                        plan.ArgumentsJson,
                        extractor),
                    ct);
            }
        }

        request.Trace?.Invoke(
            $"[ParseToolCall] ResourceId={resourceId?.ToString() ?? "(null)"} from args: {request.ToolCall.ArgumentsJson}");

        return CompleteParse(plan, resourceId);
    }

    /// <summary>
    /// Builds a parse plan for a module tool call, or returns null when the
    /// tool name does not resolve to a registered module tool.
    /// </summary>
    public ChatNativeToolCallParsePlan? BuildParsePlan(
        ChatToolCall toolCall,
        ModuleRegistry moduleRegistry,
        ModuleToolExecutionPlanner executionPlanner)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(moduleRegistry);
        ArgumentNullException.ThrowIfNull(executionPlanner);

        if (!moduleRegistry.TryResolve(
                toolCall.Name,
                out var moduleId,
                out var toolName))
        {
            return null;
        }

        var envelope = executionPlanner.CreateEnvelopeJson(
            moduleId,
            toolName,
            toolCall.ArgumentsJson);
        var resourceId = TryExtractKnownResourceId(toolCall.ArgumentsJson);

        return new ChatNativeToolCallParsePlan(
            toolCall.Id,
            toolCall.Name,
            toolCall.ArgumentsJson ?? "{}",
            moduleId,
            toolName,
            envelope,
            resourceId,
            RequiresResourceExtractor: resourceId is null);
    }

    /// <summary>
    /// Completes a parse plan after the host optionally runs a module-provided
    /// resource-id extractor.
    /// </summary>
    public ParsedChatToolCall CompleteParse(
        ChatNativeToolCallParsePlan plan,
        Guid? extractedResourceId)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new ParsedChatToolCall(
            plan.CallId,
            plan.DirectResourceId ?? extractedResourceId,
            ScriptJson: plan.ScriptJson,
            ActionKey: plan.ActionKey);
    }

    /// <summary>
    /// Creates the job submission request for a parsed tool call.
    /// </summary>
    public SubmitAgentJobRequest BuildJobRequest(
        ParsedChatToolCall parsed,
        Guid callerAgentId)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        return new SubmitAgentJobRequest(
            ActionKey: parsed.ActionKey,
            ResourceId: parsed.ResourceId,
            CallerAgentId: callerAgentId,
            ScriptJson: parsed.ScriptJson);
    }

    /// <summary>
    /// Extracts a resource id from the standard native-tool argument aliases.
    /// </summary>
    public Guid? TryExtractKnownResourceId(string? argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson ?? "{}");
            return TryReadResourceId(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Guid? TryReadResourceId(JsonElement root)
    {
        if ((root.TryGetProperty("resourceId", out var resourceProperty)
             || root.TryGetProperty("resource_id", out resourceProperty)
             || root.TryGetProperty("targetId", out resourceProperty))
            && resourceProperty.ValueKind == JsonValueKind.String
            && Guid.TryParse(resourceProperty.GetString(), out var resourceId))
        {
            return resourceId;
        }

        return null;
    }
}

/// <summary>
/// Host-neutral parse plan for one native provider tool call.
/// </summary>
public sealed record ChatNativeToolCallParsePlan(
    string CallId,
    string ActionKey,
    string ArgumentsJson,
    string ModuleId,
    string ToolName,
    string ScriptJson,
    Guid? DirectResourceId,
    bool RequiresResourceExtractor);

/// <summary>
/// Inputs required by Core to resolve a native provider tool call.
/// </summary>
public sealed record ChatNativeToolCallResolutionRequest(
    ChatToolCall ToolCall,
    ModuleRegistry ModuleRegistry,
    ModuleToolExecutionPlanner ExecutionPlanner,
    Func<ChatNativeToolCallResourceExtraction, CancellationToken, Task<Guid?>>? ExtractResourceIdAsync = null,
    Action<string>? Trace = null);

/// <summary>
/// Resource-id extraction callback requested by Core when a module registered
/// a custom extractor for the native tool call action key.
/// </summary>
public sealed record ChatNativeToolCallResourceExtraction(
    string ActionKey,
    string ArgumentsJson,
    Func<IServiceProvider, string, CancellationToken, Task<Guid?>> Extractor);

/// <summary>
/// Parsed provider tool call ready to become an agent job request.
/// </summary>
public sealed record ParsedChatToolCall(
    string CallId,
    Guid? ResourceId,
    string? ScriptJson,
    string? RawJson = null,
    string? ActionKey = null);
