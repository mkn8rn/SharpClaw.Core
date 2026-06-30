using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Modules;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Host-neutral executor for inline module tools emitted by chat providers.
/// </summary>
public sealed class ChatInlineToolExecutor(ModuleMetricsCollector metricsCollector)
{
    private readonly ModuleMetricsCollector _metricsCollector = metricsCollector
        ?? throw new ArgumentNullException(nameof(metricsCollector));

    /// <summary>
    /// Resolves, authorizes, and invokes one inline module tool call.
    /// </summary>
    public async Task<ChatInlineToolExecutionResult> ExecuteAsync(
        ChatInlineToolExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var toolCall = request.ToolCall;
        if (!request.ModuleRegistry.TryResolve(
                toolCall.Name,
                out var moduleId,
                out var canonicalName))
        {
            return ChatInlineToolExecutionResult.NotInvoked(
                $"Error: inline tool '{toolCall.Name}' not found in any module.");
        }

        var module = request.ModuleRegistry.GetModule(moduleId)
            ?? throw new InvalidOperationException(
                $"Module '{moduleId}' resolved by registry but not loaded.");

        var prefixedToolName = $"{module.ToolPrefix}_{canonicalName}";
        var context = new InlineToolContext(
            request.AgentId,
            request.ChannelId,
            request.ThreadId,
            toolCall.Id);

        JsonElement parameters;
        try
        {
            using var doc = JsonDocument.Parse(toolCall.ArgumentsJson ?? "{}");
            parameters = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ChatInlineToolExecutionResult.NotInvoked(
                "Error: malformed tool arguments JSON.");
        }

        var descriptor = request.ModuleRegistry.GetPermissionDescriptor(
            moduleId,
            canonicalName);
        if (descriptor is not null)
        {
            var permissionKey = new ChatInlineToolPermissionCacheKey(
                request.AgentId,
                moduleId,
                canonicalName);
            if (!request.PermissionCache.TryGetValue(permissionKey, out var verdict))
            {
                verdict = await request.CheckPermissionAsync(
                    new ChatInlineToolPermissionCheck(
                        request.AgentId,
                        moduleId,
                        canonicalName,
                        toolCall.Name),
                    ct);
                request.PermissionCache[permissionKey] = verdict;
            }

            if (verdict.Verdict != ClearanceVerdict.Approved)
            {
                return ChatInlineToolExecutionResult.NotInvoked(
                    $"Error: permission denied for inline tool '{toolCall.Name}': {verdict.Reason}");
            }
        }

        var runtimeHost = request.ModuleRegistry.GetRuntimeHost(moduleId);
        if (runtimeHost is not null && !runtimeHost.TryAcquireExecution())
        {
            return ChatInlineToolExecutionResult.NotInvoked(
                $"Error: module '{moduleId}' is unloading.");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var externalScope = runtimeHost?.CreateScope();
            var scopedProvider = externalScope?.ServiceProvider
                ?? request.HostServiceProvider;

            var execCtx = scopedProvider.GetService<ModuleExecutionContext>();
            if (execCtx is not null)
                execCtx.ModuleId = module.Id;

            var restrictedScope = new ModuleServiceScope(
                scopedProvider,
                module.Id,
                request.BlockedServiceTypes);

            var result = await module.ExecuteInlineToolAsync(
                canonicalName,
                parameters,
                context,
                restrictedScope,
                ct);

            sw.Stop();
            _metricsCollector.RecordSuccess(prefixedToolName, sw.Elapsed);
            return ChatInlineToolExecutionResult.Invoked(
                result,
                prefixedToolName,
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _metricsCollector.RecordFailure(prefixedToolName);
            return ChatInlineToolExecutionResult.Failed(
                $"Error executing inline tool '{toolCall.Name}': {ex.Message}",
                prefixedToolName,
                sw.Elapsed,
                ex);
        }
        finally
        {
            runtimeHost?.ReleaseExecution();
        }
    }
}

/// <summary>
/// Inputs required by Core to execute an inline module tool.
/// </summary>
public sealed record ChatInlineToolExecutionRequest(
    ChatToolCall ToolCall,
    Guid AgentId,
    Guid ChannelId,
    Guid? ThreadId,
    ModuleRegistry ModuleRegistry,
    IDictionary<ChatInlineToolPermissionCacheKey, AgentActionResult> PermissionCache,
    Func<ChatInlineToolPermissionCheck, CancellationToken, Task<AgentActionResult>> CheckPermissionAsync,
    IServiceProvider HostServiceProvider,
    IReadOnlyCollection<Type> BlockedServiceTypes);

/// <summary>
/// Permission check requested by Core before an inline module tool runs.
/// </summary>
public sealed record ChatInlineToolPermissionCheck(
    Guid AgentId,
    string ModuleId,
    string ToolName,
    string ActionKey);

/// <summary>
/// Cache key for one inline module permission decision in a chat round.
/// </summary>
public readonly record struct ChatInlineToolPermissionCacheKey(
    Guid AgentId,
    string ModuleId,
    string ToolName);

/// <summary>
/// Result of one inline tool execution attempt.
/// </summary>
public sealed record ChatInlineToolExecutionResult(
    string ToolResult,
    string? PrefixedToolName,
    TimeSpan Elapsed,
    bool ModuleInvoked,
    bool Succeeded,
    Exception? Exception)
{
    /// <summary>Creates a result for a tool call that never invoked a module.</summary>
    public static ChatInlineToolExecutionResult NotInvoked(string toolResult) =>
        new(toolResult, null, TimeSpan.Zero, ModuleInvoked: false, Succeeded: false, null);

    /// <summary>Creates a result for a successful module invocation.</summary>
    public static ChatInlineToolExecutionResult Invoked(
        string toolResult,
        string prefixedToolName,
        TimeSpan elapsed) =>
        new(toolResult, prefixedToolName, elapsed, ModuleInvoked: true, Succeeded: true, null);

    /// <summary>Creates a result for a failed module invocation.</summary>
    public static ChatInlineToolExecutionResult Failed(
        string toolResult,
        string prefixedToolName,
        TimeSpan elapsed,
        Exception exception) =>
        new(toolResult, prefixedToolName, elapsed, ModuleInvoked: true, Succeeded: false, exception);
}
