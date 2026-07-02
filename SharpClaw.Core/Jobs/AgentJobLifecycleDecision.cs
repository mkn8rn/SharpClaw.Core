using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Jobs;

/// <summary>
/// Store-neutral job lifecycle decision. Hosts apply the requested field
/// changes to their own persisted job shape and write the emitted log entries.
/// </summary>
public sealed record AgentJobLifecycleDecision
{
    /// <summary>The status to assign, or null when status should not change.</summary>
    public AgentJobStatus? Status { get; init; }

    /// <summary>Whether the persisted StartedAt field should be assigned.</summary>
    public bool UpdateStartedAt { get; init; }

    /// <summary>The StartedAt value to assign when <see cref="UpdateStartedAt"/> is true.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Whether the persisted CompletedAt field should be assigned.</summary>
    public bool UpdateCompletedAt { get; init; }

    /// <summary>The CompletedAt value to assign when <see cref="UpdateCompletedAt"/> is true.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Whether the persisted ResultData field should be assigned.</summary>
    public bool UpdateResultData { get; init; }

    /// <summary>The ResultData value to assign when <see cref="UpdateResultData"/> is true.</summary>
    public string? ResultData { get; init; }

    /// <summary>Whether the persisted ErrorLog field should be assigned.</summary>
    public bool UpdateErrorLog { get; init; }

    /// <summary>The ErrorLog value to assign when <see cref="UpdateErrorLog"/> is true.</summary>
    public string? ErrorLog { get; init; }

    /// <summary>Whether the host should dispatch job execution after applying this decision.</summary>
    public bool ShouldExecute { get; init; }

    /// <summary>Lifecycle log entries the host should persist in order.</summary>
    public IReadOnlyList<AgentJobLifecycleLog> Logs { get; init; } = [];

    /// <summary>Returns whether applying this decision changes persisted state or logs.</summary>
    public bool HasChanges =>
        Status is not null
        || UpdateStartedAt
        || UpdateCompletedAt
        || UpdateResultData
        || UpdateErrorLog
        || Logs.Count > 0;
}

/// <summary>A job lifecycle log entry independent of the host database type.</summary>
public sealed record AgentJobLifecycleLog(string Message, string Level);
