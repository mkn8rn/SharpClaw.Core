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
