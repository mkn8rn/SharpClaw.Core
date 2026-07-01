using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Tasks.Compilation;
using SharpClaw.Core.Tasks.Models;

namespace SharpClaw.Core.Tasks.Runtime;

/// <summary>
/// Executes compiled task plans through the canonical SharpClaw task runtime
/// semantics while delegating persistence and host services to adapters.
/// </summary>
public sealed class TaskPlanExecutionEngine
{
    private readonly IReadOnlyList<ITaskStepExecutorExtension> _stepExtensions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TaskExpressionEngine _expressions;
    private readonly TaskRuntimeLifecycleEngine _runtimeLifecycle;
    private readonly ILogger<TaskPlanExecutionEngine> _logger;

    /// <summary>
    /// Creates the execution engine with module step extensions and host DI
    /// scope support.
    /// </summary>
    public TaskPlanExecutionEngine(
        IServiceScopeFactory scopeFactory,
        IEnumerable<ITaskStepExecutorExtension> stepExtensions,
        TaskExpressionEngine? expressions = null,
        TaskRuntimeLifecycleEngine? runtimeLifecycle = null,
        ILogger<TaskPlanExecutionEngine>? logger = null)
    {
        _scopeFactory = scopeFactory
            ?? throw new ArgumentNullException(nameof(scopeFactory));
        _stepExtensions = stepExtensions?.ToList()
            ?? throw new ArgumentNullException(nameof(stepExtensions));
        _expressions = expressions ?? new TaskExpressionEngine();
        _runtimeLifecycle = runtimeLifecycle ?? new TaskRuntimeLifecycleEngine();
        _logger = logger ?? NullLogger<TaskPlanExecutionEngine>.Instance;
    }

    /// <summary>
    /// Executes a compiled task plan to completion, cancellation, or failure.
    /// </summary>
    public async Task<TaskPlanExecutionOutcome> ExecuteAsync(
        TaskPlanExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = new TaskPlanExecutionContext(
            request.InstanceId,
            request.Plan,
            request.Runtime,
            request.CancellationToken)
        {
            Services = request.Services
        };

        SetupSharedDataStore(request, context);
        RegisterCustomToolHooks(request, context);

        var executionTiming = Stopwatch.StartNew();
        try
        {
            var initialChannelId = await request.Host.LoadInitialChannelIdAsync(
                request.InstanceId,
                request.CancellationToken);
            context.ChannelId = initialChannelId ?? Guid.Empty;

            await ExecuteStepDefinitionsAsync(
                request.Plan.ExecutionSteps,
                context,
                request.Host,
                request.CancellationToken,
                throwOnUnsupportedInvocation: false);

            await request.Host.MarkTerminalStatusAsync(
                request.InstanceId,
                TaskInstanceStatus.Completed,
                request.CancellationToken);
            await EmitRuntimeEventPlanAsync(
                request.InstanceId,
                _runtimeLifecycle.BuildTerminalPlan(TaskInstanceStatus.Completed),
                request.Host,
                request.Runtime,
                request.CancellationToken);

            executionTiming.Stop();
            _logger.LogDebug(
                "Task instance {InstanceId} completed in {ElapsedMs}ms.",
                request.InstanceId,
                executionTiming.ElapsedMilliseconds);
            return new TaskPlanExecutionOutcome(
                TaskInstanceStatus.Completed,
                null,
                executionTiming.Elapsed);
        }
        catch (OperationCanceledException)
        {
            await request.Host.MarkTerminalStatusAsync(
                request.InstanceId,
                TaskInstanceStatus.Cancelled,
                CancellationToken.None);
            await EmitRuntimeEventPlanAsync(
                request.InstanceId,
                _runtimeLifecycle.BuildTerminalPlan(TaskInstanceStatus.Cancelled),
                request.Host,
                request.Runtime,
                CancellationToken.None);

            executionTiming.Stop();
            _logger.LogDebug(
                "Task instance {InstanceId} cancelled after {ElapsedMs}ms.",
                request.InstanceId,
                executionTiming.ElapsedMilliseconds);
            return new TaskPlanExecutionOutcome(
                TaskInstanceStatus.Cancelled,
                null,
                executionTiming.Elapsed);
        }
        catch (Exception ex)
        {
            await request.Host.MarkFailedAsync(
                request.InstanceId,
                ex.Message,
                CancellationToken.None);
            await EmitRuntimeEventPlanAsync(
                request.InstanceId,
                _runtimeLifecycle.BuildFailurePlan(ex.Message),
                request.Host,
                request.Runtime,
                CancellationToken.None);

            executionTiming.Stop();
            _logger.LogWarning(
                ex,
                "Task instance {InstanceId} failed after {ElapsedMs}ms.",
                request.InstanceId,
                executionTiming.ElapsedMilliseconds);
            return new TaskPlanExecutionOutcome(
                TaskInstanceStatus.Failed,
                ex.Message,
                executionTiming.Elapsed);
        }
        finally
        {
            TaskSharedData.Remove(request.InstanceId);
        }
    }

    private void SetupSharedDataStore(
        TaskPlanExecutionRequest request,
        TaskPlanExecutionContext context)
    {
        var store = TaskSharedData.GetOrCreate(request.InstanceId);
        store.TaskName = request.Plan.TaskName;
        store.TaskDescription = request.Plan.Description;
        store.TaskSourceText = request.Plan.Definition.SourceText;
        store.TaskParametersJson = request.Plan.ParameterValues.Count > 0
            ? JsonSerializer.Serialize(request.Plan.ParameterValues)
            : null;
        store.AllowedOutputFormat = request.Plan.AgentOutputFormat;
        store.RegisterBuiltInTools();
        store.OnAgentOutput = async data =>
            await EmitOutputAsync(context, request.Host, data, context.CancellationToken);
        store.OnSharedDataChanged = async (
            description,
            lightSnapshot,
            bigSnapshotJson) =>
        {
            await request.Host.PersistSharedDataSnapshotAsync(
                request.InstanceId,
                lightSnapshot,
                bigSnapshotJson,
                context.CancellationToken);
            await EmitRuntimeEventPlanAsync(
                request.InstanceId,
                _runtimeLifecycle.BuildSharedDataChangedPlan(description),
                request.Host,
                request.Runtime,
                context.CancellationToken);
        };
    }

    private void RegisterCustomToolHooks(
        TaskPlanExecutionRequest request,
        TaskPlanExecutionContext context)
    {
        var store = TaskSharedData.GetOrCreate(request.InstanceId);

        foreach (var hook in request.Plan.ToolCallHooks)
        {
            store.RegisterCustomToolHook(hook, async (args, hookCt) =>
            {
                foreach (var parameter in hook.Parameters)
                {
                    var value = args?.TryGetProperty(
                        parameter.Name,
                        out var property) == true
                        ? property.ValueKind == JsonValueKind.String
                            ? property.GetString()
                            : property.GetRawText()
                        : null;
                    context.Variables[parameter.Name] = value;
                }

                await ExecuteStepDefinitionsAsync(
                    hook.Body,
                    context,
                    request.Host,
                    hookCt,
                    throwOnUnsupportedInvocation: false);

                return hook.ReturnVariable is not null
                    && context.Variables.TryGetValue(
                        hook.ReturnVariable,
                        out var result)
                    ? result?.ToString() ?? string.Empty
                    : string.Empty;
            });
        }
    }

    private async Task<TaskStepExecutionResult> ExecuteStepAsync(
        TaskStepDefinition step,
        TaskPlanExecutionContext context,
        ITaskPlanExecutionHost host,
        CancellationToken ct)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
        await context.Runtime.WaitIfPausedAsync(context.CancellationToken);

        var stepKey = step.StepKey ?? string.Empty;
        var executor = _stepExtensions.FirstOrDefault(e => e.CanExecute(stepKey));
        if (executor is null)
            return TaskStepExecutionResult.Continue;

        var moduleContext = new TaskStepContextAdapter(context, this, host);
        var stepTiming = Stopwatch.StartNew();

        if (executor is ITaskStepInvocationExecutor invocationExecutor)
        {
            var result = await invocationExecutor.ExecuteInvocationAsync(
                step,
                moduleContext);
            stepTiming.Stop();
            _logger.LogDebug(
                "Task instance {InstanceId} step {StepKey} completed in {ElapsedMs}ms. Result={Result}",
                context.InstanceId,
                SanitizeForLog(stepKey),
                stepTiming.ElapsedMilliseconds,
                result);
            return result == TaskStepResult.Return
                ? TaskStepExecutionResult.Return
                : TaskStepExecutionResult.Continue;
        }

        var resolvedArguments = step.Arguments?
            .Select(argument => _expressions.ResolveExpression(
                argument,
                context.Variables))
            .ToList();
        if (step.TypeName is not null)
        {
            resolvedArguments ??= [];
            resolvedArguments.Insert(0, step.TypeName);
        }

        var resolvedExpression = step.Expression is not null
            ? _expressions.ResolveExpression(step.Expression, context.Variables)
            : null;
        var keepGoing = await executor.ExecuteAsync(
            stepKey,
            moduleContext,
            resolvedArguments,
            resolvedExpression,
            step.ResultVariable);

        stepTiming.Stop();
        _logger.LogDebug(
            "Task instance {InstanceId} step {StepKey} completed in {ElapsedMs}ms. Continue={Continue}",
            context.InstanceId,
            SanitizeForLog(stepKey),
            stepTiming.ElapsedMilliseconds,
            keepGoing);
        return keepGoing
            ? TaskStepExecutionResult.Continue
            : TaskStepExecutionResult.Return;
    }

    private async Task<TaskStepResult> ExecuteStepDefinitionsAsync(
        IReadOnlyList<ITaskStepInvocation> steps,
        TaskPlanExecutionContext context,
        ITaskPlanExecutionHost host,
        CancellationToken ct,
        bool throwOnUnsupportedInvocation)
    {
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (step is not TaskStepDefinition definition)
            {
                if (throwOnUnsupportedInvocation)
                    throw new InvalidOperationException(
                        $"Unsupported step invocation type: {step.GetType().FullName}");
                continue;
            }

            var result = await ExecuteStepAsync(
                definition,
                context,
                host,
                ct);
            if (result == TaskStepExecutionResult.Return)
                return TaskStepResult.Return;
        }

        return TaskStepResult.Continue;
    }

    private async Task ExecuteScopedStepDefinitionsAsync(
        IReadOnlyList<ITaskStepInvocation> steps,
        TaskPlanExecutionContext context,
        ITaskPlanExecutionHost host,
        CancellationToken ct,
        bool throwOnUnsupportedInvocation)
    {
        using var scope = _scopeFactory.CreateScope();
        using var serviceScope = context.UseServices(scope.ServiceProvider);

        await ExecuteStepDefinitionsAsync(
            steps,
            context,
            host,
            ct,
            throwOnUnsupportedInvocation);
    }

    private static async Task EmitOutputAsync(
        TaskPlanExecutionContext context,
        ITaskPlanExecutionHost host,
        string? outputJson,
        CancellationToken ct)
    {
        var sequence = context.Runtime.IncrementSequence();
        await host.PersistOutputAsync(
            context.InstanceId,
            sequence,
            outputJson,
            ct);

        await context.Runtime.WriteEventAsync(
            TaskOutputEventType.Output,
            outputJson,
            ct);
    }

    private static async Task EmitRuntimeEventPlanAsync(
        Guid instanceId,
        TaskRuntimeEventPlan plan,
        ITaskPlanExecutionHost host,
        TaskRuntimeInstance runtime,
        CancellationToken ct)
    {
        if (plan.LogMessage is not null)
            await host.AppendLogAsync(
                instanceId,
                plan.LogMessage,
                plan.LogLevel,
                ct);

        foreach (var evt in plan.OutputEvents)
            await runtime.WriteEventAsync(evt.Type, evt.Data, ct);
    }

    private static string SanitizeForLog(string value)
        => value.Replace('\r', '_').Replace('\n', '_');

    private sealed class TaskPlanExecutionContext(
        Guid instanceId,
        CompiledTaskPlan plan,
        TaskRuntimeInstance runtime,
        CancellationToken cancellationToken)
    {
        public Guid InstanceId { get; } = instanceId;
        public Guid ChannelId { get; set; }
        public CompiledTaskPlan Plan { get; } = plan;
        public TaskRuntimeInstance Runtime { get; } = runtime;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Dictionary<string, object?> Variables { get; } = new(StringComparer.Ordinal);
        public List<RegisteredEventHandler> EventHandlers { get; } = [];
        public IServiceProvider Services { get; set; } = default!;

        public IDisposable UseServices(IServiceProvider services)
        {
            var previous = Services;
            Services = services;
            return new ServiceScopeRestore(() => Services = previous);
        }
    }

    private sealed record RegisteredEventHandler(
        string ModuleTriggerKey,
        string? ParameterName,
        IReadOnlyList<ITaskStepInvocation> Body);

    private sealed class TaskStepContextAdapter(
        TaskPlanExecutionContext context,
        TaskPlanExecutionEngine engine,
        ITaskPlanExecutionHost host) : ITaskStepExecutionContext
    {
        public Guid InstanceId => context.InstanceId;
        public Guid ChannelId => context.ChannelId;
        public CancellationToken CancellationToken => context.CancellationToken;
        public IServiceProvider Services => context.Services;
        public IDictionary<string, object?> Variables => context.Variables;

        public IReadOnlyList<ITaskEventHandler> EventHandlers =>
            context.EventHandlers
                .Select(handler => (ITaskEventHandler)new EventHandlerAdapter(
                    handler,
                    context,
                    engine,
                    host))
                .ToList();

        public string ResolveExpression(string expression) =>
            engine._expressions.ResolveExpression(
                expression,
                context.Variables);

        public Task AppendLogAsync(string message) =>
            host.AppendLogAsync(
                context.InstanceId,
                message,
                JobLogLevels.Info,
                context.CancellationToken);

        public Task WriteOutputAsync(string? outputJson) =>
            EmitOutputAsync(
                context,
                host,
                outputJson,
                context.CancellationToken);

        public void SetChannelId(Guid channelId) =>
            context.ChannelId = channelId;

        public Task WaitIfPausedAsync() =>
            context.Runtime.WaitIfPausedAsync(context.CancellationToken);

        public bool EvaluateCondition(string? expression) =>
            engine._expressions.EvaluateCondition(
                expression,
                context.Variables);

        public void RegisterEventHandler(
            string moduleTriggerKey,
            string? parameterName,
            IReadOnlyList<ITaskStepInvocation> body)
        {
            context.EventHandlers.Add(new RegisteredEventHandler(
                moduleTriggerKey,
                parameterName,
                body));
        }

        public async Task<TaskStepResult> ExecuteStepsAsync(
            IReadOnlyList<ITaskStepInvocation> steps,
            CancellationToken cancellationToken)
        {
            using var scope = engine._scopeFactory.CreateScope();
            using var serviceScope = context.UseServices(scope.ServiceProvider);

            return await engine.ExecuteStepDefinitionsAsync(
                steps,
                context,
                host,
                cancellationToken,
                throwOnUnsupportedInvocation: true);
        }
    }

    private sealed class EventHandlerAdapter(
        RegisteredEventHandler handler,
        TaskPlanExecutionContext context,
        TaskPlanExecutionEngine engine,
        ITaskPlanExecutionHost host) : ITaskEventHandler
    {
        public string? ModuleTriggerKey => handler.ModuleTriggerKey;
        public string? ParameterName => handler.ParameterName;

        public async Task ExecuteBodyAsync(CancellationToken ct)
        {
            await engine.ExecuteScopedStepDefinitionsAsync(
                handler.Body,
                context,
                host,
                ct,
                throwOnUnsupportedInvocation: false);
        }
    }

    private sealed class ServiceScopeRestore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private enum TaskStepExecutionResult
    {
        Continue,
        Return,
    }
}

/// <summary>
/// Input required to execute one compiled task plan.
/// </summary>
public sealed record TaskPlanExecutionRequest(
    Guid InstanceId,
    CompiledTaskPlan Plan,
    TaskRuntimeInstance Runtime,
    IServiceProvider Services,
    ITaskPlanExecutionHost Host,
    CancellationToken CancellationToken);

/// <summary>
/// Result of a completed task-plan execution attempt.
/// </summary>
public sealed record TaskPlanExecutionOutcome(
    TaskInstanceStatus Status,
    string? Error,
    TimeSpan Elapsed);

/// <summary>
/// Host adapter used by the Core task execution engine for persistence and
/// runtime side effects.
/// </summary>
public interface ITaskPlanExecutionHost
{
    /// <summary>
    /// Loads the task instance's initial channel id, or null for context-only
    /// starts.
    /// </summary>
    Task<Guid?> LoadInitialChannelIdAsync(
        Guid instanceId,
        CancellationToken ct);

    /// <summary>
    /// Persists one task output payload with the supplied sequence number.
    /// </summary>
    Task PersistOutputAsync(
        Guid instanceId,
        long sequence,
        string? outputJson,
        CancellationToken ct);

    /// <summary>
    /// Persists the current shared-data snapshots for an instance.
    /// </summary>
    Task PersistSharedDataSnapshotAsync(
        Guid instanceId,
        string? lightSnapshot,
        string? bigSnapshotJson,
        CancellationToken ct);

    /// <summary>
    /// Appends a log entry for a task instance.
    /// </summary>
    Task AppendLogAsync(
        Guid instanceId,
        string message,
        string level,
        CancellationToken ct);

    /// <summary>
    /// Marks a task instance as a non-failed terminal status.
    /// </summary>
    Task MarkTerminalStatusAsync(
        Guid instanceId,
        TaskInstanceStatus status,
        CancellationToken ct);

    /// <summary>
    /// Marks a task instance as failed with the supplied error.
    /// </summary>
    Task MarkFailedAsync(
        Guid instanceId,
        string error,
        CancellationToken ct);
}
