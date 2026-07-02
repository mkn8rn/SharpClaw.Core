namespace SharpClaw.Core.Modules;

/// <summary>
/// Store-neutral rule for deciding whether a module can be disabled without
/// breaking other modules' non-optional contract requirements.
/// </summary>
public sealed class ModuleDisableDependencyEngine
{
    public ModuleDisableDependencyDecision Evaluate(
        ModuleDisableDependencyFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var exportedContractNames = facts.ExportedContractNames
            .ToHashSet(StringComparer.Ordinal);
        if (exportedContractNames.Count == 0)
            return ModuleDisableDependencyDecision.Allowed(facts.ModuleId);

        foreach (var otherModule in facts.OtherModules)
        {
            if (string.Equals(otherModule.ModuleId, facts.ModuleId, StringComparison.Ordinal))
                continue;

            var blockingContracts = otherModule.RequiredContracts
                .Where(requirement => !requirement.Optional
                    && exportedContractNames.Contains(requirement.ContractName))
                .Select(requirement => requirement.ContractName)
                .ToArray();
            if (blockingContracts.Length > 0)
            {
                return ModuleDisableDependencyDecision.Blocked(
                    facts.ModuleId,
                    otherModule.ModuleId,
                    blockingContracts);
            }
        }

        return ModuleDisableDependencyDecision.Allowed(facts.ModuleId);
    }

}

public sealed record ModuleDisableDependencyFacts(
    string ModuleId,
    IReadOnlyList<string> ExportedContractNames,
    IReadOnlyList<ModuleDisableDependencyCandidateFacts> OtherModules);

public sealed record ModuleDisableDependencyCandidateFacts(
    string ModuleId,
    IReadOnlyList<ModuleDisableDependencyRequirementFacts> RequiredContracts);

public sealed record ModuleDisableDependencyRequirementFacts(
    string ContractName,
    bool Optional);

public sealed record ModuleDisableDependencyDecision(
    string ModuleId,
    bool CanDisable,
    string? BlockerModuleId,
    IReadOnlyList<string> BlockingContracts)
{
    public static ModuleDisableDependencyDecision Allowed(string moduleId) =>
        new(moduleId, CanDisable: true, BlockerModuleId: null, BlockingContracts: []);

    public static ModuleDisableDependencyDecision Blocked(
        string moduleId,
        string blockerModuleId,
        IReadOnlyList<string> blockingContracts) =>
        new(moduleId, CanDisable: false, blockerModuleId, blockingContracts);
}
