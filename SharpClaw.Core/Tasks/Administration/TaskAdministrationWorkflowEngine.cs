using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Tasks.Models;
using SharpClaw.Core.Tasks.Preflight;

namespace SharpClaw.Core.Tasks.Administration;

/// <summary>
/// Store-neutral task authoring and instance administration workflow.
/// Hosts own persistence, trigger side effects, and runtime fact gathering;
/// Core owns sequencing, validation, lifecycle transitions, and response
/// mapping.
/// </summary>
public sealed class TaskAdministrationWorkflowEngine(
    TaskAdministrationEngine tasks)
{
    public TaskAdministrationWorkflowEngine()
        : this(new TaskAdministrationEngine())
    {
    }

    public TaskValidationResponse ValidateDefinition(string sourceText)
        => tasks.ValidateDefinition(sourceText);

    public async Task<TaskDefinitionResponse> CreateDefinitionAsync(
        CreateTaskDefinitionRequest request,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var prepared = tasks.PrepareDefinition(request);
        var nameExists = await host.DefinitionNameExistsAsync(
            prepared.Entity.Name,
            ct);
        tasks.EnsureDefinitionNameAvailable(prepared.Entity.Name, nameExists);

        var entity = prepared.Entity;
        host.TrackDefinition(entity);
        await host.SaveAsync(ct);

        var bindingsChanged = await host.SyncTriggersAsync(
            entity,
            prepared.Definition.TriggerDefinitions,
            ct);
        if (bindingsChanged)
        {
            await host.SaveAsync(ct);
            await host.NotifyTriggerBindingsChangedAsync(ct);
        }

        return ToDefinitionResponse(
            entity,
            prepared.Definition.Parameters,
            prepared.Definition.Requirements,
            prepared.Definition.TriggerDefinitions,
            host);
    }

    public async Task<TaskDefinitionResponse?> GetDefinitionAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var entity = await host.LoadDefinitionAsync(id, ct);
        if (entity is null)
            return null;

        return ToDefinitionResponse(
            entity,
            tasks.DeserializeParameters(entity.ParametersJson),
            tasks.DeserializeRequirements(entity.RequirementsJson),
            tasks.DeserializeTriggers(entity.TriggersJson),
            host);
    }

    public async Task<IReadOnlyList<TaskRequirementDefinition>?> GetRequirementsAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var entity = await host.LoadDefinitionAsync(id, ct);
        return entity is null
            ? null
            : tasks.DeserializeRequirements(entity.RequirementsJson);
    }

    public async Task<IReadOnlyList<TaskTriggerDefinition>?> GetTriggersAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var entity = await host.LoadDefinitionAsync(id, ct);
        return entity is null
            ? null
            : tasks.DeserializeTriggers(entity.TriggersJson);
    }

    public async Task<IReadOnlyList<TaskDefinitionResponse>> ListDefinitionsAsync(
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var definitions = await host.ListDefinitionsAsync(ct);
        return definitions
            .OrderByDescending(definition => definition.UpdatedAt)
            .Select(definition => ToDefinitionResponse(
                definition,
                tasks.DeserializeParameters(definition.ParametersJson),
                tasks.DeserializeRequirements(definition.RequirementsJson),
                tasks.DeserializeTriggers(definition.TriggersJson),
                host))
            .ToList();
    }

    public async Task<TaskDefinitionResponse?> UpdateDefinitionAsync(
        Guid id,
        UpdateTaskDefinitionRequest request,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var entity = await host.LoadDefinitionAsync(id, ct);
        if (entity is null)
            return null;

        var updated = tasks.ApplyDefinitionUpdate(entity, request);
        await host.SaveAsync(ct);

        if (updated.SourceWasUpdated)
        {
            var bindingsChanged = await host.SyncTriggersAsync(
                entity,
                updated.Triggers,
                ct);
            if (bindingsChanged)
            {
                await host.SaveAsync(ct);
                await host.NotifyTriggerBindingsChangedAsync(ct);
            }
        }

        return ToDefinitionResponse(
            entity,
            updated.Parameters,
            updated.Requirements,
            updated.Triggers,
            host);
    }

    public async Task<bool> DeleteDefinitionAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var entity = await host.LoadDefinitionAsync(id, ct);
        if (entity is null)
            return false;

        var bindingsChanged = await host.RemoveTriggersAsync(id, ct);
        if (bindingsChanged)
        {
            await host.SaveAsync(ct);
            await host.NotifyTriggerBindingsChangedAsync(ct);
        }

        host.RemoveDefinition(entity);
        await host.SaveAsync(ct);
        return true;
    }

    public async Task<int> SetTriggersEnabledAsync(
        Guid taskDefinitionId,
        bool enabled,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var bindings = await host.LoadTriggerBindingsAsync(
            taskDefinitionId,
            ct);
        foreach (var binding in bindings)
            binding.IsEnabled = enabled;

        await host.SaveAsync(ct);
        return bindings.Count;
    }

    public async Task<TaskInstanceResponse> CreateInstanceAsync(
        StartTaskInstanceRequest request,
        Guid? callerUserId,
        Guid? callerAgentId,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var definition = await host.LoadDefinitionAsync(
                request.TaskDefinitionId,
                ct)
            ?? throw new InvalidOperationException(
                $"Task definition {request.TaskDefinitionId} not found.");

        var requirements = tasks.DeserializeRequirements(
            definition.RequirementsJson);
        if (requirements.Count > 0)
        {
            var preflightResult = await host.CheckRuntimePreflightAsync(
                requirements,
                tasks.ToPreflightParameterMap(request.ParameterValues),
                callerAgentId,
                ct);
            if (preflightResult.IsBlocked)
                throw new TaskPreflightBlockedException(preflightResult);
        }

        var instance = tasks.CreateInstance(
            definition,
            request,
            callerUserId,
            callerAgentId);
        host.TrackInstance(instance);
        await host.SaveAsync(ct);

        return tasks.ToInstanceResponse(instance, definition.Name);
    }

    public async Task<bool> PauseInstanceAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        return await MutateInstanceAsync(id, tasks.TryPauseInstance, host, ct);
    }

    public async Task<bool> ResumeInstanceAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        return await MutateInstanceAsync(id, tasks.TryResumeInstance, host, ct);
    }

    public async Task<bool> TryMarkInstanceRunningAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        return await MutateInstanceAsync(
            id,
            tasks.TryMarkInstanceRunning,
            host,
            ct);
    }

    public async Task<bool> StopInstanceAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        return await MutateInstanceAsync(id, tasks.TryStopInstance, host, ct);
    }

    public async Task<bool> CancelInstanceAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        return await MutateInstanceAsync(id, tasks.TryCancelInstance, host, ct);
    }

    public async Task<TaskInstanceResponse?> GetInstanceAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var instance = await host.LoadInstanceWithLogsAsync(id, ct);
        if (instance is null)
            return null;

        var definition = await host.LoadDefinitionAsync(
            instance.TaskDefinitionId,
            ct);
        return tasks.ToInstanceResponse(
            instance,
            definition?.Name ?? "(unknown)");
    }

    public async Task<IReadOnlyList<TaskInstanceSummaryResponse>> ListInstancesAsync(
        Guid? taskDefinitionId,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var instances = await host.ListInstancesAsync(taskDefinitionId, ct);
        var definitionIds = instances
            .Select(instance => instance.TaskDefinitionId)
            .Distinct()
            .ToList();
        var definitionNames = await host.LoadDefinitionNamesAsync(
            definitionIds,
            ct);

        return instances
            .OrderByDescending(instance => instance.CreatedAt)
            .Select(instance => tasks.ToSummaryResponse(
                instance,
                definitionNames.GetValueOrDefault(
                    instance.TaskDefinitionId,
                    "(unknown)")))
            .ToList();
    }

    public async Task AppendLogAsync(
        Guid instanceId,
        string message,
        string level,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        host.TrackLog(tasks.AddLog(null, instanceId, message, level));
        await host.SaveAsync(ct);
    }

    public async Task<IReadOnlyList<TaskOutputEntryResponse>> GetOutputsAsync(
        Guid instanceId,
        DateTimeOffset? since,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var outputs = await host.ListOutputsAsync(instanceId, since, ct);
        return outputs
            .OrderBy(output => output.Sequence)
            .Select(tasks.ToOutputResponse)
            .ToList();
    }

    private async Task<bool> MutateInstanceAsync(
        Guid id,
        Func<TaskInstanceDB, bool> mutate,
        ITaskAdministrationHost host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(host);

        var instance = await host.LoadInstanceAsync(id, ct);
        if (instance is null || !mutate(instance))
            return false;

        await host.SaveAsync(ct);
        return true;
    }

    private TaskDefinitionResponse ToDefinitionResponse(
        TaskDefinitionDB entity,
        IReadOnlyList<TaskParameterDefinition> parameters,
        IReadOnlyList<TaskRequirementDefinition> requirements,
        IReadOnlyList<TaskTriggerDefinition> triggers,
        ITaskAdministrationHost host)
    {
        return tasks.ToDefinitionResponse(
            entity,
            parameters,
            requirements,
            triggers,
            host.ResolveTriggerValue,
            host.ResolveTriggerFilter);
    }
}

public interface ITaskAdministrationHost
{
    Task<bool> DefinitionNameExistsAsync(
        string name,
        CancellationToken ct);

    Task<TaskDefinitionDB?> LoadDefinitionAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<TaskDefinitionDB>> ListDefinitionsAsync(
        CancellationToken ct);

    void TrackDefinition(TaskDefinitionDB definition);

    void RemoveDefinition(TaskDefinitionDB definition);

    Task<IReadOnlyList<TaskTriggerBindingDB>> LoadTriggerBindingsAsync(
        Guid taskDefinitionId,
        CancellationToken ct);

    Task<bool> SyncTriggersAsync(
        TaskDefinitionDB definition,
        IReadOnlyList<TaskTriggerDefinition> triggers,
        CancellationToken ct);

    Task<bool> RemoveTriggersAsync(Guid definitionId, CancellationToken ct);

    Task NotifyTriggerBindingsChangedAsync(CancellationToken ct);

    string? ResolveTriggerValue(TaskTriggerDefinition trigger);

    string? ResolveTriggerFilter(TaskTriggerDefinition trigger);

    Task<TaskPreflightResult> CheckRuntimePreflightAsync(
        IReadOnlyList<TaskRequirementDefinition> requirements,
        IReadOnlyDictionary<string, object?> parameterValues,
        Guid? callerAgentId,
        CancellationToken ct);

    void TrackInstance(TaskInstanceDB instance);

    Task<TaskInstanceDB?> LoadInstanceAsync(Guid id, CancellationToken ct);

    Task<TaskInstanceDB?> LoadInstanceWithLogsAsync(
        Guid id,
        CancellationToken ct);

    Task<IReadOnlyList<TaskInstanceDB>> ListInstancesAsync(
        Guid? taskDefinitionId,
        CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, string>> LoadDefinitionNamesAsync(
        IReadOnlyCollection<Guid> definitionIds,
        CancellationToken ct);

    void TrackLog(TaskExecutionLogDB log);

    Task<IReadOnlyList<TaskOutputEntryDB>> ListOutputsAsync(
        Guid instanceId,
        DateTimeOffset? since,
        CancellationToken ct);

    Task SaveAsync(CancellationToken ct);
}
