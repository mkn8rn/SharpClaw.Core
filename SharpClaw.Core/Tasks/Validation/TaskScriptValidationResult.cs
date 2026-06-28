using SharpClaw.Core.Tasks.Models;

namespace SharpClaw.Core.Tasks.Validation;

/// <summary>
/// Result of <see cref="TaskScriptValidator.Validate"/>.
/// </summary>
public sealed record TaskScriptValidationResult(
    bool IsValid,
    IReadOnlyList<TaskDiagnostic> Diagnostics);
