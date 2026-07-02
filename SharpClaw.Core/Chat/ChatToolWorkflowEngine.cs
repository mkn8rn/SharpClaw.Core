using System.Text.Json;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Tasks.Runtime;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral workflow for chat-visible tool surfaces and task tools.
/// </summary>
public sealed class ChatToolWorkflowEngine(
    ModuleRegistry moduleRegistry,
    ChatCache cache,
    ChatToolSelectionEngine toolSelection)
{
    private readonly ModuleRegistry _moduleRegistry = moduleRegistry
        ?? throw new ArgumentNullException(nameof(moduleRegistry));
    private readonly ChatCache _cache = cache
        ?? throw new ArgumentNullException(nameof(cache));
    private readonly ChatToolSelectionEngine _toolSelection = toolSelection
        ?? throw new ArgumentNullException(nameof(toolSelection));

    /// <summary>
    /// Returns the provider-facing tool definitions for a chat request.
    /// Module tools are always the base surface. Task-scoped tools are
    /// appended only for the running task instance, and tool-awareness is
    /// applied after all available tool definitions have been collected.
    /// </summary>
    public async Task<IReadOnlyList<ChatToolDefinition>> GetEffectiveToolsAsync(
        ChatEffectiveToolRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.TaskContext is null && request.AgentId.HasValue)
        {
            return await _cache.GetOrCreateAsync(
                ChatCache.KeyEffectiveTools(
                    request.AgentId.Value,
                    _toolSelection.BuildAwarenessFingerprint(
                        request.ToolAwareness)),
                _ => Task.FromResult<IReadOnlyList<ChatToolDefinition>?>(
                    BuildEffectiveTools(null, request.ToolAwareness)),
                _toolSelection.EstimateToolDefinitions,
                ct)
                ?? [];
        }

        return BuildEffectiveTools(
            request.TaskContext,
            request.ToolAwareness);
    }

    /// <summary>
    /// Attempts to handle a provider tool call as a task-scoped tool for
    /// the active task instance. Unknown tools return <c>false</c>; malformed
    /// arguments or callback failures return a handled error string so the
    /// model receives feedback in the tool loop.
    /// </summary>
    public async Task<(bool Handled, string? Result)> TryHandleTaskToolAsync(
        ChatToolCall toolCall,
        TaskChatContext? taskContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (taskContext is null)
            return (false, null);

        var store = TaskSharedData.Get(taskContext.InstanceId);
        if (store is null)
            return (false, null);

        JsonDocument? argsDocument = null;
        try
        {
            JsonElement? args = null;
            if (!string.IsNullOrEmpty(toolCall.ArgumentsJson))
            {
                argsDocument = JsonDocument.Parse(toolCall.ArgumentsJson);
                args = argsDocument.RootElement;
            }

            var handled = await store.TryInvokeToolAsync(
                toolCall.Name,
                args,
                ct);
            if (handled.Handled)
                return handled;
        }
        catch (Exception ex)
        {
            return (
                true,
                $"Error handling task tool '{toolCall.Name}': {ex.Message}");
        }
        finally
        {
            argsDocument?.Dispose();
        }

        return (false, null);
    }

    private IReadOnlyList<ChatToolDefinition> BuildEffectiveTools(
        TaskChatContext? taskContext,
        IReadOnlyDictionary<string, bool>? toolAwareness)
    {
        var baseTools = new List<ChatToolDefinition>(
            _moduleRegistry.GetAllToolDefinitions());

        if (taskContext is not null)
        {
            var store = TaskSharedData.Get(taskContext.InstanceId);
            if (store is not null)
                baseTools.AddRange(store.GetToolDefinitions());
        }

        return _toolSelection.ApplyAwareness(
            baseTools,
            toolAwareness);
    }
}

public sealed record ChatEffectiveToolRequest(
    TaskChatContext? TaskContext,
    IReadOnlyDictionary<string, bool>? ToolAwareness,
    Guid? AgentId);
