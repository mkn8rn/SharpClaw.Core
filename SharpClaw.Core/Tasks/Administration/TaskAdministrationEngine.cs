using System.Text.Json;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Tasks.Models;

namespace SharpClaw.Core.Tasks.Administration;

/// <summary>
/// Store-neutral task definition and instance administration rules.
/// </summary>
public sealed class TaskAdministrationEngine
{
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a task administration engine with an optional host-supplied clock.
    /// </summary>
    public TaskAdministrationEngine(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Parses and validates task source without creating a persisted entity.
    /// </summary>
    public TaskValidationResponse ValidateDefinition(string sourceText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);

        var parseResult = TaskScriptEngine.Parse(sourceText);
        if (!parseResult.Success || parseResult.Definition is null)
        {
            return new TaskValidationResponse(
                false,
                parseResult.Diagnostics.Select(ToDiagnosticResponse).ToArray());
        }

        var validation = TaskScriptEngine.Validate(parseResult.Definition);
        var diagnostics = parseResult.Diagnostics
            .Concat(validation.Diagnostics)
            .Select(ToDiagnosticResponse)
            .ToArray();

        return new TaskValidationResponse(validation.IsValid, diagnostics);
    }

    /// <summary>
    /// Creates a new task definition entity from validated source.
    /// </summary>
    public TaskDefinitionPreparation PrepareDefinition(
        CreateTaskDefinitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var definition = ParseAndValidateDefinition(request.SourceText);

        var entity = new TaskDefinitionDB
        {
            Name = definition.Name,
            Description = definition.Description,
            SourceText = request.SourceText,
            OutputTypeName = definition.OutputType?.Name,
            ParametersJson = SerializeParameters(definition.Parameters),
            RequirementsJson = SerializeRequirements(definition.Requirements),
            TriggersJson = SerializeTriggers(definition.TriggerDefinitions),
        };

        return new TaskDefinitionPreparation(entity, definition);
    }

    /// <summary>
    /// Throws when a task definition name is already present in the store.
    /// </summary>
    public void EnsureDefinitionNameAvailable(string name, bool nameAlreadyExists)
    {
        if (nameAlreadyExists)
        {
            throw new InvalidOperationException(
                $"Task definition '{name}' already exists.");
        }
    }

    /// <summary>
    /// Applies source and active-state updates to an existing task definition.
    /// </summary>
    public TaskDefinitionUpdatePreparation ApplyDefinitionUpdate(
        TaskDefinitionDB entity,
        UpdateTaskDefinitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<TaskParameterDefinition>? parameters = null;
        IReadOnlyList<TaskRequirementDefinition>? requirements = null;
        IReadOnlyList<TaskTriggerDefinition>? triggers = null;

        if (request.SourceText is not null)
        {
            var definition = ParseAndValidateDefinition(request.SourceText);

            entity.Name = definition.Name;
            entity.Description = definition.Description;
            entity.SourceText = request.SourceText;
            entity.OutputTypeName = definition.OutputType?.Name;
            entity.ParametersJson = SerializeParameters(definition.Parameters);
            entity.RequirementsJson = SerializeRequirements(definition.Requirements);
            entity.TriggersJson = SerializeTriggers(definition.TriggerDefinitions);

            parameters = definition.Parameters;
            requirements = definition.Requirements;
            triggers = definition.TriggerDefinitions;
        }

        if (request.IsActive is not null)
            entity.IsActive = request.IsActive.Value;

        return new TaskDefinitionUpdatePreparation(
            parameters ?? DeserializeParameters(entity.ParametersJson),
            requirements ?? DeserializeRequirements(entity.RequirementsJson),
            triggers ?? DeserializeTriggers(entity.TriggersJson),
            SourceWasUpdated: request.SourceText is not null);
    }

    /// <summary>
    /// Creates a queued task instance row from a validated start request.
    /// </summary>
    public TaskInstanceDB CreateInstance(
        TaskDefinitionDB definition,
        StartTaskInstanceRequest request,
        Guid? callerUserId,
        Guid? callerAgentId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);

        if (!definition.IsActive)
        {
            throw new InvalidOperationException(
                $"Task definition '{definition.Name}' is not active.");
        }

        return new TaskInstanceDB
        {
            TaskDefinitionId = definition.Id,
            Status = TaskInstanceStatus.Queued,
            ParameterValuesJson = SerializeParameterValues(request.ParameterValues),
            ChannelId = request.ChannelId,
            ContextId = request.ContextId,
            CallerUserId = callerUserId,
            CallerAgentId = callerAgentId,
        };
    }

    /// <summary>
    /// Converts supplied string parameters into the preflight value map.
    /// </summary>
    public IReadOnlyDictionary<string, object?> ToPreflightParameterMap(
        IReadOnlyDictionary<string, string>? parameterValues)
    {
        return parameterValues is not null
            ? parameterValues.ToDictionary(
                kv => kv.Key,
                kv => (object?)kv.Value,
                StringComparer.Ordinal)
            : new Dictionary<string, object?>();
    }

    /// <summary>
    /// Moves a running instance to paused.
    /// </summary>
    public bool TryPauseInstance(TaskInstanceDB instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Status != TaskInstanceStatus.Running)
            return false;

        instance.Status = TaskInstanceStatus.Paused;
        return true;
    }

    /// <summary>
    /// Moves a paused instance back to running.
    /// </summary>
    public bool TryResumeInstance(TaskInstanceDB instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Status != TaskInstanceStatus.Paused)
            return false;

        instance.Status = TaskInstanceStatus.Running;
        return true;
    }

    /// <summary>
    /// Moves a queued instance to running and clears terminal fields.
    /// </summary>
    public bool TryMarkInstanceRunning(TaskInstanceDB instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Status != TaskInstanceStatus.Queued)
            return false;

        instance.Status = TaskInstanceStatus.Running;
        instance.StartedAt = _timeProvider.GetUtcNow();
        instance.CompletedAt = null;
        instance.ErrorMessage = null;
        return true;
    }

    /// <summary>
    /// Cancels a running or paused instance after a graceful stop request.
    /// </summary>
    public bool TryStopInstance(TaskInstanceDB instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Status is not (TaskInstanceStatus.Running or TaskInstanceStatus.Paused))
            return false;

        instance.Status = TaskInstanceStatus.Cancelled;
        instance.CompletedAt = _timeProvider.GetUtcNow();
        return true;
    }

    /// <summary>
    /// Cancels a queued, running, or paused instance.
    /// </summary>
    public bool TryCancelInstance(TaskInstanceDB instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Status is not (TaskInstanceStatus.Queued or TaskInstanceStatus.Running or TaskInstanceStatus.Paused))
            return false;

        instance.Status = TaskInstanceStatus.Cancelled;
        instance.CompletedAt = _timeProvider.GetUtcNow();
        return true;
    }

    /// <summary>
    /// Marks a task instance failed because its script could not compile.
    /// </summary>
    public void ApplyCompilationFailure(TaskInstanceDB instance, string errors)
    {
        ArgumentNullException.ThrowIfNull(instance);

        instance.Status = TaskInstanceStatus.Failed;
        instance.ErrorMessage = $"Compilation failed: {errors}";
        instance.CompletedAt = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Marks a task instance completed or cancelled.
    /// </summary>
    public void ApplyTerminalStatus(TaskInstanceDB instance, TaskInstanceStatus status)
    {
        ArgumentNullException.ThrowIfNull(instance);

        instance.Status = status;
        instance.CompletedAt = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Marks a task instance failed with the supplied error message.
    /// </summary>
    public void ApplyFailure(TaskInstanceDB instance, string error)
    {
        ArgumentNullException.ThrowIfNull(instance);

        instance.Status = TaskInstanceStatus.Failed;
        instance.ErrorMessage = error;
        instance.CompletedAt = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Updates the latest output snapshot and creates the output history row.
    /// </summary>
    public TaskOutputEntryDB ApplyOutput(
        TaskInstanceDB instance,
        long sequence,
        string? outputJson)
    {
        ArgumentNullException.ThrowIfNull(instance);

        instance.OutputSnapshotJson = outputJson;
        return new TaskOutputEntryDB
        {
            TaskInstanceId = instance.Id,
            Sequence = sequence,
            Data = outputJson,
        };
    }

    /// <summary>
    /// Creates and attaches a task execution log row.
    /// </summary>
    public TaskExecutionLogDB AddLog(
        TaskInstanceDB? instance,
        Guid instanceId,
        string message,
        string level = JobLogLevels.Info)
    {
        var entry = new TaskExecutionLogDB
        {
            TaskInstanceId = instanceId,
            Message = message,
            Level = level,
        };

        instance?.LogEntries.Add(entry);
        return entry;
    }

    /// <summary>
    /// Projects a persisted task definition into its public response.
    /// </summary>
    public TaskDefinitionResponse ToDefinitionResponse(
        TaskDefinitionDB entity,
        IReadOnlyList<TaskParameterDefinition> parameters,
        IReadOnlyList<TaskRequirementDefinition> requirements,
        IReadOnlyList<TaskTriggerDefinition> triggers,
        Func<TaskTriggerDefinition, string?>? triggerValueResolver = null,
        Func<TaskTriggerDefinition, string?>? triggerFilterResolver = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(triggers);

        return new TaskDefinitionResponse(
            entity.Id,
            entity.Name,
            entity.Description,
            entity.OutputTypeName,
            entity.IsActive,
            parameters.Select(ToParameterResponse).ToArray(),
            requirements.Select(ToRequirementResponse).ToArray(),
            triggers.Select(t => new TaskTriggerResponse(
                t.TriggerKey ?? string.Empty,
                triggerValueResolver?.Invoke(t),
                triggerFilterResolver?.Invoke(t),
                IsEnabled: true)).ToArray(),
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.CustomId);
    }

    /// <summary>
    /// Projects a task instance and its logs into the public response.
    /// </summary>
    public TaskInstanceResponse ToInstanceResponse(
        TaskInstanceDB instance,
        string taskName)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return new TaskInstanceResponse(
            instance.Id,
            instance.TaskDefinitionId,
            taskName,
            instance.Status,
            instance.OutputSnapshotJson,
            instance.ErrorMessage,
            ToLogResponses(instance.LogEntries),
            instance.CreatedAt,
            instance.StartedAt,
            instance.CompletedAt,
            instance.ChannelId,
            ContextId: instance.ContextId);
    }

    /// <summary>
    /// Projects a task instance into the list-summary response.
    /// </summary>
    public TaskInstanceSummaryResponse ToSummaryResponse(
        TaskInstanceDB instance,
        string taskName)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return new TaskInstanceSummaryResponse(
            instance.Id,
            instance.TaskDefinitionId,
            taskName,
            instance.Status,
            instance.CreatedAt,
            instance.StartedAt,
            instance.CompletedAt);
    }

    /// <summary>
    /// Projects persisted log rows into public log responses.
    /// </summary>
    public IReadOnlyList<TaskExecutionLogResponse> ToLogResponses(
        IEnumerable<TaskExecutionLogDB> logs)
    {
        ArgumentNullException.ThrowIfNull(logs);

        return logs
            .Select(log => new TaskExecutionLogResponse(log.Message, log.Level, log.CreatedAt))
            .ToArray();
    }

    /// <summary>
    /// Projects a persisted output row into its public response.
    /// </summary>
    public TaskOutputEntryResponse ToOutputResponse(TaskOutputEntryDB output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return new TaskOutputEntryResponse(output.Id, output.Sequence, output.Data, output.CreatedAt);
    }

    /// <summary>
    /// Formats a task diagnostic for exception messages.
    /// </summary>
    public string FormatDiagnostic(TaskDiagnostic diagnostic)
        => diagnostic.Line > 0 ? $"[Line {diagnostic.Line}] {diagnostic.Message}" : diagnostic.Message;

    /// <summary>
    /// Projects a task diagnostic into its public response.
    /// </summary>
    public TaskDiagnosticResponse ToDiagnosticResponse(TaskDiagnostic diagnostic)
        => new(
            diagnostic.Severity.ToString(),
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Line,
            diagnostic.Column);

    /// <summary>
    /// Serializes task parameter metadata into the canonical persisted JSON shape.
    /// </summary>
    public string SerializeParameters(IReadOnlyList<TaskParameterDefinition> parameters)
    {
        var dtos = parameters
            .Select(p => new ParameterDto(p.Name, p.TypeName, p.Description, p.DefaultValue, p.IsRequired))
            .ToArray();
        return JsonSerializer.Serialize(dtos);
    }

    /// <summary>
    /// Deserializes task parameter metadata from the canonical persisted JSON shape.
    /// </summary>
    public IReadOnlyList<TaskParameterDefinition> DeserializeParameters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var dtos = JsonSerializer.Deserialize<List<ParameterDto>>(json) ?? [];
        return dtos
            .Select(d => new TaskParameterDefinition(
                Name: d.Name ?? "",
                TypeName: d.TypeName ?? "string",
                Description: d.Description,
                DefaultValue: d.DefaultValue,
                IsRequired: d.IsRequired))
            .ToArray();
    }

    /// <summary>
    /// Serializes task requirement metadata into the canonical persisted JSON shape.
    /// </summary>
    public string SerializeRequirements(IReadOnlyList<TaskRequirementDefinition> requirements)
    {
        var dtos = requirements
            .Select(r => new RequirementDto(
                r.Kind.ToString(),
                r.Severity.ToString(),
                r.Value,
                r.CapabilityValue,
                r.ParameterName,
                r.Line))
            .ToArray();
        return JsonSerializer.Serialize(dtos);
    }

    /// <summary>
    /// Deserializes task requirement metadata from the canonical persisted JSON shape.
    /// </summary>
    public IReadOnlyList<TaskRequirementDefinition> DeserializeRequirements(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var dtos = JsonSerializer.Deserialize<List<RequirementDto>>(json) ?? [];
        return dtos
            .Select(d =>
            {
                Enum.TryParse<TaskRequirementKind>(d.Kind ?? string.Empty, out var kind);
                Enum.TryParse<TaskDiagnosticSeverity>(
                    d.Severity ?? nameof(TaskDiagnosticSeverity.Error),
                    out var severity);

                return new TaskRequirementDefinition
                {
                    Kind = kind,
                    Severity = severity,
                    Value = d.Value,
                    CapabilityValue = d.CapabilityValue,
                    ParameterName = d.ParameterName,
                    Line = d.Line,
                };
            })
            .ToArray();
    }

    /// <summary>
    /// Serializes task trigger metadata into the canonical persisted JSON shape.
    /// </summary>
    public string SerializeTriggers(IReadOnlyList<TaskTriggerDefinition> triggers)
        => JsonSerializer.Serialize(triggers);

    /// <summary>
    /// Deserializes task trigger metadata from the canonical persisted JSON shape.
    /// </summary>
    public IReadOnlyList<TaskTriggerDefinition> DeserializeTriggers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<TaskTriggerDefinition>>(json) ?? [];
    }

    /// <summary>
    /// Serializes task instance parameter values for persistence.
    /// </summary>
    public string? SerializeParameterValues(IReadOnlyDictionary<string, string>? parameterValues)
        => parameterValues is not null ? JsonSerializer.Serialize(parameterValues) : null;

    private TaskScriptDefinition ParseAndValidateDefinition(string sourceText)
    {
        var parseResult = TaskScriptEngine.Parse(sourceText);
        if (!parseResult.Success || parseResult.Definition is null)
        {
            var errors = string.Join("; ", parseResult.Diagnostics.Select(FormatDiagnostic));
            throw new InvalidOperationException($"Task script parse failed: {errors}");
        }

        var validation = TaskScriptEngine.Validate(parseResult.Definition);
        if (!validation.IsValid)
        {
            var errors = string.Join("; ", validation.Diagnostics.Select(FormatDiagnostic));
            throw new InvalidOperationException($"Task script validation failed: {errors}");
        }

        return parseResult.Definition;
    }

    private static TaskParameterResponse ToParameterResponse(TaskParameterDefinition parameter)
        => new(
            parameter.Name,
            parameter.TypeName,
            parameter.Description,
            parameter.DefaultValue,
            parameter.IsRequired);

    private static TaskRequirementResponse ToRequirementResponse(TaskRequirementDefinition requirement)
        => new(
            requirement.Kind.ToString(),
            requirement.Severity.ToString(),
            requirement.Value,
            requirement.CapabilityValue,
            requirement.ParameterName);

    private sealed record ParameterDto(
        string Name,
        string TypeName,
        string? Description,
        string? DefaultValue,
        bool IsRequired);

    private sealed record RequirementDto(
        string? Kind,
        string? Severity,
        string? Value,
        string? CapabilityValue,
        string? ParameterName,
        int Line);
}

/// <summary>
/// Parsed metadata produced while preparing a new task definition.
/// </summary>
public sealed record TaskDefinitionPreparation(
    TaskDefinitionDB Entity,
    TaskScriptDefinition Definition);

/// <summary>
/// Parsed metadata produced while updating a task definition.
/// </summary>
public sealed record TaskDefinitionUpdatePreparation(
    IReadOnlyList<TaskParameterDefinition> Parameters,
    IReadOnlyList<TaskRequirementDefinition> Requirements,
    IReadOnlyList<TaskTriggerDefinition> Triggers,
    bool SourceWasUpdated);
