namespace SharpClaw.Core.Tasks.Preflight;

/// <summary>
/// Thrown when task instance creation is blocked by runtime preflight checks.
/// </summary>
public sealed class TaskPreflightBlockedException(TaskPreflightResult result)
    : InvalidOperationException(
        "Task preflight check failed - one or more requirements are not satisfied.")
{
    /// <summary>
    /// The detailed preflight outcome including all findings.
    /// </summary>
    public TaskPreflightResult PreflightResult { get; } = result;
}
