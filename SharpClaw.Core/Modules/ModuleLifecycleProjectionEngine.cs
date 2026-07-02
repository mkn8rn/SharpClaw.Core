using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Modules;

/// <summary>
/// Store-neutral projection rules for module lifecycle state and detail
/// responses. Hosts collect persistence, registry, and manifest facts; Core
/// owns the response semantics.
/// </summary>
public sealed class ModuleLifecycleProjectionEngine
{
    public ModuleStateResponse ProjectState(ModuleLifecycleStateFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return new ModuleStateResponse(
            facts.ModuleId,
            facts.DisplayName,
            facts.ToolPrefix,
            Enabled: facts.IsExternal || (facts.StateEnabled ?? false),
            Version: facts.ManifestVersion ?? facts.StateVersion,
            Registered: facts.IsExternal || facts.HasPersistedState,
            IsExternal: facts.IsExternal,
            CreatedAt: facts.CreatedAt,
            UpdatedAt: facts.UpdatedAt);
    }

    public ModuleDetailResponse ProjectDetail(ModuleLifecycleDetailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var state = ProjectState(facts.State);
        var exportedContracts = facts.ExportedContractNames.ToArray();
        var requiredContracts = facts.RequiredContracts
            .Select(static requirement => requirement.ContractName)
            .ToArray();
        var allRequirementsSatisfied = facts.RequiredContracts
            .Where(static requirement => !requirement.Optional)
            .All(static requirement => requirement.IsSatisfied);

        return new ModuleDetailResponse(
            state.ModuleId,
            state.DisplayName,
            state.ToolPrefix,
            state.Enabled,
            state.Version,
            state.Registered,
            state.IsExternal,
            state.CreatedAt,
            state.UpdatedAt,
            facts.Author,
            facts.Description,
            facts.License,
            facts.Platforms,
            facts.ExecutionTimeoutSeconds,
            facts.ToolCount,
            facts.InlineToolCount,
            exportedContracts,
            requiredContracts,
            allRequirementsSatisfied);
    }
}

public sealed record ModuleLifecycleStateFacts(
    string ModuleId,
    string DisplayName,
    string ToolPrefix,
    bool IsExternal,
    bool HasPersistedState,
    bool? StateEnabled,
    string? StateVersion,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? ManifestVersion);

public sealed record ModuleLifecycleDetailFacts(
    ModuleLifecycleStateFacts State,
    string? Author,
    string? Description,
    string? License,
    string[]? Platforms,
    int ExecutionTimeoutSeconds,
    int ToolCount,
    int InlineToolCount,
    IReadOnlyList<string> ExportedContractNames,
    IReadOnlyList<ModuleLifecycleRequirementFacts> RequiredContracts);

public sealed record ModuleLifecycleRequirementFacts(
    string ContractName,
    bool Optional,
    bool IsSatisfied);
