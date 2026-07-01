using System.Text.Json;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Core.Tasks.Triggers;

/// <summary>
/// Builds the canonical reconciliation plan for task trigger bindings.
/// Hosts load and persist rows; Core owns binding identity, owned-source
/// filtering, value/filter projection, and stale-row detection.
/// </summary>
public sealed class TaskTriggerBindingPlanner
{
    /// <summary>
    /// Builds the trigger binding sync plan for one task definition.
    /// </summary>
    public TaskTriggerBindingSyncPlan BuildSyncPlan(
        TaskTriggerBindingSyncRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Triggers);
        ArgumentNullException.ThrowIfNull(request.ExistingBindings);

        var ownedGroups = BuildOwnedGroups(request);
        var defaultTriggers = request.Triggers
            .Where(trigger => !IsOwnedBySource(
                request.SourceRegistry,
                trigger.TriggerKey))
            .ToList();

        var incomingKeys = defaultTriggers
            .Select(trigger => BindingKey(
                request.TaskDefinition.Id,
                trigger,
                request.SourceRegistry))
            .ToHashSet(StringComparer.Ordinal);

        var removals = request.ExistingBindings
            .Where(binding => !IsOwnedBySource(
                request.SourceRegistry,
                binding.Kind)
                && !incomingKeys.Contains(BindingKey(binding)))
            .ToList();

        var existingKeys = request.ExistingBindings
            .Select(BindingKey)
            .ToHashSet(StringComparer.Ordinal);

        var creations = defaultTriggers
            .Where(trigger => !existingKeys.Contains(BindingKey(
                request.TaskDefinition.Id,
                trigger,
                request.SourceRegistry)))
            .Select(trigger => BuildCreation(
                request.TaskDefinition.Id,
                trigger,
                request.SourceRegistry))
            .ToList();

        return new TaskTriggerBindingSyncPlan(
            ownedGroups,
            removals,
            creations);
    }

    private static IReadOnlyList<TaskTriggerOwnedSourceSync> BuildOwnedGroups(
        TaskTriggerBindingSyncRequest request)
    {
        if (request.SourceRegistry is null)
            return [];

        return request.Triggers
            .Where(trigger => !string.IsNullOrWhiteSpace(trigger.TriggerKey))
            .GroupBy(trigger => request.SourceRegistry.ResolveByKey(
                trigger.TriggerKey))
            .Where(group => group.Key is { OwnsBindingPersistence: true })
            .Select(group => new TaskTriggerOwnedSourceSync(
                group.Key!,
                group.ToList()))
            .ToList();
    }

    private static TaskTriggerBindingCreation BuildCreation(
        Guid definitionId,
        TaskTriggerDefinition trigger,
        ITaskTriggerSourceRegistry? sourceRegistry)
    {
        var kind = trigger.TriggerKey ?? string.Empty;
        var value = TriggerValueFor(trigger, sourceRegistry);
        var filter = TriggerFilterFor(trigger, sourceRegistry);

        return new TaskTriggerBindingCreation(
            definitionId,
            kind,
            value,
            filter,
            JsonSerializer.Serialize(trigger),
            trigger);
    }

    private static bool IsOwnedBySource(
        ITaskTriggerSourceRegistry? sourceRegistry,
        string? triggerKey)
    {
        if (string.IsNullOrWhiteSpace(triggerKey))
            return false;

        var source = sourceRegistry?.ResolveByKey(triggerKey);
        return source is { OwnsBindingPersistence: true };
    }

    private static string BindingKey(
        Guid definitionId,
        TaskTriggerDefinition trigger,
        ITaskTriggerSourceRegistry? sourceRegistry)
    {
        return BindingKey(
            new TaskTriggerBindingSnapshot(
                definitionId,
                trigger.TriggerKey ?? string.Empty,
                TriggerValueFor(trigger, sourceRegistry),
                TriggerFilterFor(trigger, sourceRegistry)));
    }

    /// <summary>
    /// Builds the stable identity key used to compare existing and incoming
    /// trigger bindings.
    /// </summary>
    public static string BindingKey(TaskTriggerBindingSnapshot binding) =>
        $"{binding.TaskDefinitionId}|{binding.Kind}|{binding.TriggerValue}";

    private static string? TriggerValueFor(
        TaskTriggerDefinition trigger,
        ITaskTriggerSourceRegistry? sourceRegistry) =>
        sourceRegistry?.ResolveByKey(trigger.TriggerKey)?.GetBindingValue(
            trigger);

    private static string? TriggerFilterFor(
        TaskTriggerDefinition trigger,
        ITaskTriggerSourceRegistry? sourceRegistry) =>
        sourceRegistry?.ResolveByKey(trigger.TriggerKey)?.GetBindingFilter(
            trigger);
}

/// <summary>
/// Inputs for task trigger binding reconciliation.
/// </summary>
public sealed record TaskTriggerBindingSyncRequest(
    TaskDefinitionDescriptor TaskDefinition,
    IReadOnlyList<TaskTriggerDefinition> Triggers,
    IReadOnlyList<TaskTriggerBindingSnapshot> ExistingBindings,
    ITaskTriggerSourceRegistry? SourceRegistry);

/// <summary>
/// Host-neutral trigger binding reconciliation plan.
/// </summary>
public sealed record TaskTriggerBindingSyncPlan(
    IReadOnlyList<TaskTriggerOwnedSourceSync> OwnedSourceSyncs,
    IReadOnlyList<TaskTriggerBindingSnapshot> DefaultBindingsToRemove,
    IReadOnlyList<TaskTriggerBindingCreation> DefaultBindingsToCreate)
{
    /// <summary>
    /// Returns whether the host must add or remove any default persistence
    /// rows. Owned source changes are reported by the source itself.
    /// </summary>
    public bool HasDefaultBindingChanges =>
        DefaultBindingsToRemove.Count > 0
        || DefaultBindingsToCreate.Count > 0;
}

/// <summary>
/// A grouped sync call for a source that owns its own binding persistence.
/// </summary>
public sealed record TaskTriggerOwnedSourceSync(
    ITaskTriggerSource Source,
    IReadOnlyList<TaskTriggerDefinition> Triggers);

/// <summary>
/// Existing host persistence row projected into Core for reconciliation.
/// </summary>
public sealed record TaskTriggerBindingSnapshot(
    Guid TaskDefinitionId,
    string Kind,
    string? TriggerValue,
    string? Filter);

/// <summary>
/// Default host persistence row Core wants the host to create.
/// </summary>
public sealed record TaskTriggerBindingCreation(
    Guid TaskDefinitionId,
    string Kind,
    string? TriggerValue,
    string? Filter,
    string DefinitionJson,
    TaskTriggerDefinition Trigger)
{
    /// <summary>
    /// Public descriptor shape used by trigger binding side effects.
    /// </summary>
    public TaskTriggerBindingDescriptor ToDescriptor() =>
        new(TaskDefinitionId, Kind, TriggerValue, Filter);
}
