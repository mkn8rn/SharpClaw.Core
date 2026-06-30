using System.Runtime.InteropServices;
using SharpClaw.Core.Tasks.Models;

namespace SharpClaw.Core.Tasks.Preflight;

/// <summary>
/// Host-neutral task preflight rules and result shaping.
/// </summary>
public sealed class TaskPreflightEngine
{
    /// <summary>
    /// Evaluates platform-only requirements without any host store access.
    /// </summary>
    public TaskPreflightResult CheckStatic(IReadOnlyList<TaskRequirementDefinition> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        var findings = new List<TaskPreflightFinding>();
        foreach (var requirement in requirements)
        {
            if (requirement.Kind != TaskRequirementKind.RequiresPlatform)
                continue;

            if (!Enum.TryParse<TaskPlatform>(requirement.Value, ignoreCase: false, out var platform))
                continue;

            findings.Add(CreateStaticPlatformFinding(requirement, IsPlatformSatisfied(platform)));
        }

        return BuildResult(findings);
    }

    /// <summary>
    /// Returns whether the current process satisfies a declared platform mask.
    /// </summary>
    public bool IsPlatformSatisfied(TaskPlatform platform)
        => (platform.HasFlag(TaskPlatform.Windows) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        || (platform.HasFlag(TaskPlatform.Linux) && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        || (platform.HasFlag(TaskPlatform.MacOS) && RuntimeInformation.IsOSPlatform(OSPlatform.OSX));

    /// <summary>
    /// Creates the runtime platform finding after the host has parsed the requirement.
    /// </summary>
    public TaskPreflightFinding CreateRuntimePlatformFinding(
        TaskRequirementDefinition requirement,
        bool passed)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        return new TaskPreflightFinding(
            requirement.Kind.ToString(),
            requirement.Severity,
            passed,
            passed
                ? $"Platform '{requirement.Value}' is satisfied."
                : $"Platform '{requirement.Value}' is not satisfied on the current host.");
    }

    /// <summary>
    /// Creates the static platform finding used at definition-registration time.
    /// </summary>
    public TaskPreflightFinding CreateStaticPlatformFinding(
        TaskRequirementDefinition requirement,
        bool passed)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        return new TaskPreflightFinding(
            requirement.Kind.ToString(),
            requirement.Severity,
            passed,
            passed
                ? $"Platform '{requirement.Value}' is satisfied on the current host."
                : $"Platform '{requirement.Value}' is not satisfied on the current host.");
    }

    /// <summary>
    /// Evaluates runtime requirements from host-supplied facts.
    /// </summary>
    public TaskPreflightResult CheckRuntime(
        IReadOnlyList<TaskRequirementDefinition> requirements,
        IReadOnlyDictionary<string, object?> paramValues,
        TaskPreflightRuntimeFacts facts,
        bool hasCallerAgent)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(paramValues);
        ArgumentNullException.ThrowIfNull(facts);

        var findings = new List<TaskPreflightFinding>();

        foreach (var req in requirements)
        {
            switch (req.Kind)
            {
                case TaskRequirementKind.RequiresPlatform:
                {
                    var passed = Enum.TryParse<TaskPlatform>(
                                     req.Value,
                                     out var platform)
                                 && IsPlatformSatisfied(platform);
                    findings.Add(CreateRuntimePlatformFinding(req, passed));
                    break;
                }

                case TaskRequirementKind.RequiresProvider:
                    findings.Add(EvaluateProvider(req, facts));
                    break;

                case TaskRequirementKind.RequiresModelCapability:
                    findings.Add(EvaluateModelCapability(req, facts));
                    break;

                case TaskRequirementKind.RequiresModel:
                    findings.Add(EvaluateModel(req, facts));
                    break;

                case TaskRequirementKind.RequiresModule:
                    findings.Add(EvaluateRequiredModule(req, facts));
                    break;

                case TaskRequirementKind.RecommendsModule:
                    findings.Add(EvaluateRecommendedModule(req, facts));
                    break;

                case TaskRequirementKind.RequiresPermission:
                    findings.Add(EvaluatePermission(req, facts, hasCallerAgent));
                    break;

                case TaskRequirementKind.ModelIdParameter:
                    findings.Add(EvaluateModelIdParameter(req, paramValues, facts));
                    break;

                case TaskRequirementKind.RequiresCapabilityParameter:
                    findings.Add(EvaluateCapabilityParameter(req, paramValues, facts));
                    break;
            }
        }

        return BuildResult(findings);
    }

    /// <summary>
    /// Aggregates findings into the canonical preflight result.
    /// </summary>
    public TaskPreflightResult BuildResult(IEnumerable<TaskPreflightFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var materialized = findings.ToArray();
        var isBlocked = materialized.Any(f =>
            f.Severity == TaskDiagnosticSeverity.Error && !f.Passed);
        return new TaskPreflightResult(isBlocked, materialized);
    }

    private static TaskPreflightFinding EvaluateProvider(
        TaskRequirementDefinition req,
        TaskPreflightRuntimeFacts facts)
    {
        var value = req.Value ?? string.Empty;
        var provider = facts.Providers.FirstOrDefault(p =>
            p.ProviderKey.Equals(value, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            return new TaskPreflightFinding(
                req.Kind.ToString(),
                req.Severity,
                Passed: false,
                $"'{value}' is not a recognised provider key.");
        }

        var passed = provider.IsConfiguredWithRequiredCredentials;
        return new TaskPreflightFinding(
            req.Kind.ToString(),
            req.Severity,
            passed,
            passed
                ? $"Provider '{value}' is configured."
                : $"Provider '{value}' is not configured or has no API key.");
    }

    private static TaskPreflightFinding EvaluateModelCapability(
        TaskRequirementDefinition req,
        TaskPreflightRuntimeFacts facts)
    {
        var capName = req.CapabilityValue ?? string.Empty;
        var passed = facts.Models.Any(model => HasCapability(model, capName));

        return new TaskPreflightFinding(
            req.Kind.ToString(),
            req.Severity,
            passed,
            passed
                ? $"A model with capability tag '{capName}' exists."
                : $"No model with capability tag '{capName}' is registered.");
    }

    private static TaskPreflightFinding EvaluateModel(
        TaskRequirementDefinition req,
        TaskPreflightRuntimeFacts facts)
    {
        var value = req.Value ?? string.Empty;
        var passed = FindModel(facts, value) is not null;

        return new TaskPreflightFinding(
            req.Kind.ToString(),
            req.Severity,
            passed,
            passed
                ? $"Model '{value}' is available."
                : $"Model '{value}' is not registered.");
    }

    private static TaskPreflightFinding EvaluateRequiredModule(
        TaskRequirementDefinition req,
        TaskPreflightRuntimeFacts facts)
    {
        var moduleId = req.Value ?? string.Empty;
        var passed = facts.EnabledModuleIds.Contains(moduleId);

        return new TaskPreflightFinding(
            req.Kind.ToString(),
            req.Severity,
            passed,
            passed
                ? $"Module '{moduleId}' is enabled."
                : $"Module '{moduleId}' is not enabled.");
    }

    private static TaskPreflightFinding EvaluateRecommendedModule(
        TaskRequirementDefinition req,
        TaskPreflightRuntimeFacts facts)
    {
        var moduleId = req.Value ?? string.Empty;
        var enabled = facts.EnabledModuleIds.Contains(moduleId);

        return new TaskPreflightFinding(
            req.Kind.ToString(),
            TaskDiagnosticSeverity.Warning,
            Passed: enabled,
            enabled
                ? $"Recommended module '{moduleId}' is enabled."
                : $"Recommended module '{moduleId}' is not enabled. The task may have reduced functionality.");
    }

    private static TaskPreflightFinding EvaluatePermission(
        TaskRequirementDefinition req,
        TaskPreflightRuntimeFacts facts,
        bool hasCallerAgent)
    {
        var flagKey = req.Value ?? string.Empty;
        var passed = hasCallerAgent
                     && facts.CallerPermissionFlags.Contains(flagKey);

        return new TaskPreflightFinding(
            req.Kind.ToString(),
            req.Severity,
            passed,
            passed
                ? $"Caller agent has permission '{flagKey}'."
                : hasCallerAgent
                    ? $"Caller agent does not have permission '{flagKey}'."
                    : $"Permission '{flagKey}' required but no caller agent was supplied.");
    }

    private static TaskPreflightFinding EvaluateModelIdParameter(
        TaskRequirementDefinition req,
        IReadOnlyDictionary<string, object?> paramValues,
        TaskPreflightRuntimeFacts facts)
    {
        var paramName = req.ParameterName ?? string.Empty;
        if (!paramValues.TryGetValue(paramName, out var rawValue)
            || rawValue is null)
        {
            return new TaskPreflightFinding(
                req.Kind.ToString(),
                req.Severity,
                Passed: false,
                $"Parameter '{paramName}' is required for model ID resolution but was not provided.",
                paramName);
        }

        var modelRef = rawValue.ToString() ?? string.Empty;
        var passed = FindModel(facts, modelRef) is not null;

        return new TaskPreflightFinding(
            req.Kind.ToString(),
            req.Severity,
            passed,
            passed
                ? $"Model '{modelRef}' (from parameter '{paramName}') is available."
                : $"Model '{modelRef}' (from parameter '{paramName}') is not registered.",
            paramName);
    }

    private static TaskPreflightFinding EvaluateCapabilityParameter(
        TaskRequirementDefinition req,
        IReadOnlyDictionary<string, object?> paramValues,
        TaskPreflightRuntimeFacts facts)
    {
        var paramName = req.ParameterName ?? string.Empty;
        var capName = req.CapabilityValue ?? string.Empty;

        if (!paramValues.TryGetValue(paramName, out var rawValue)
            || rawValue is null)
        {
            return new TaskPreflightFinding(
                req.Kind.ToString(),
                req.Severity,
                Passed: false,
                $"Parameter '{paramName}' is required for capability check but was not provided.",
                paramName);
        }

        var modelRef = rawValue.ToString() ?? string.Empty;
        var model = FindModel(facts, modelRef);
        var passed = model is not null && HasCapability(model, capName);

        return new TaskPreflightFinding(
            req.Kind.ToString(),
            req.Severity,
            passed,
            passed
                ? $"Model '{modelRef}' (from parameter '{paramName}') has capability tag '{capName}'."
                : $"Model '{modelRef}' (from parameter '{paramName}') does not have capability tag '{capName}'.",
            paramName);
    }

    private static TaskPreflightModelState? FindModel(
        TaskPreflightRuntimeFacts facts,
        string modelRef)
    {
        if (Guid.TryParse(modelRef, out var modelGuid))
            return facts.Models.FirstOrDefault(model => model.Id == modelGuid);

        return facts.Models.FirstOrDefault(model =>
            model.Name == modelRef || model.CustomId == modelRef);
    }

    private static bool HasCapability(
        TaskPreflightModelState model,
        string capabilityTag)
    {
        return model.CapabilityTags.Any(tag =>
            tag.Equals(capabilityTag, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The aggregated outcome of a task preflight check.
/// </summary>
public sealed record TaskPreflightResult(
    bool IsBlocked,
    IReadOnlyList<TaskPreflightFinding> Findings);

/// <summary>
/// The result of evaluating a single requirement during a preflight check.
/// </summary>
public sealed record TaskPreflightFinding(
    string RequirementKind,
    TaskDiagnosticSeverity Severity,
    bool Passed,
    string Message,
    string? ParameterName = null);
