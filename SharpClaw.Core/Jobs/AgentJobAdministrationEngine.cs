using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Jobs;

/// <summary>
/// Store-neutral job administration rules used by SharpClaw runtimes.
/// Hosts own persistence, module dispatch, and cache writes; Core owns job
/// entity construction, effective-agent checks, projection, log mutation,
/// channel-preauthorization gates, and token allocation.
/// </summary>
public sealed class AgentJobAdministrationEngine
{
    /// <summary>
    /// Resolves the agent that should execute a submitted job for a channel.
    /// </summary>
    public Guid ResolveSubmissionAgent(
        ChannelDB channel,
        Guid channelId,
        Guid? requestedAgentId)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var agentId = channel.AgentId ?? channel.AgentContext?.AgentId
            ?? throw new InvalidOperationException(
                $"Channel {channelId} has no agent and no context agent.");

        if (requestedAgentId is not { } requestedAgent || requestedAgent == agentId)
            return agentId;

        var effectiveAllowed = channel.AllowedAgents.Count > 0
            ? channel.AllowedAgents
            : (IEnumerable<AgentDB>)(channel.AgentContext?.AllowedAgents ?? []);

        if (!effectiveAllowed.Any(agent => agent.Id == requestedAgent))
            throw new InvalidOperationException(
                $"Agent {requestedAgent} is not allowed on channel {channelId}. " +
                "Add it to the channel's or context's allowed agents first.");

        return requestedAgent;
    }

    /// <summary>Creates the persisted job row for a submitted action.</summary>
    public AgentJobDB CreateSubmissionJob(
        Guid channelId,
        Guid agentId,
        SubmitAgentJobRequest request,
        Guid? callerUserId,
        Guid? effectiveResourceId)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AgentJobDB
        {
            AgentId = agentId,
            ChannelId = channelId,
            CallerUserId = callerUserId,
            CallerAgentId = request.CallerAgentId,
            ActionKey = request.ActionKey,
            ResourceId = effectiveResourceId,
            ScriptJson = request.ScriptJson,
            WorkingDirectory = request.WorkingDirectory,
        };
    }

    /// <summary>Applies a lifecycle decision and creates persisted log rows.</summary>
    public IReadOnlyList<AgentJobLogEntryDB> ApplyLifecycleDecision(
        AgentJobDB job,
        AgentJobLifecycleDecision decision)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Status is { } status)
            job.Status = status;
        if (decision.UpdateStartedAt)
            job.StartedAt = decision.StartedAt;
        if (decision.UpdateCompletedAt)
            job.CompletedAt = decision.CompletedAt;
        if (decision.UpdateResultData)
            job.ResultData = decision.ResultData;
        if (decision.UpdateErrorLog)
            job.ErrorLog = decision.ErrorLog;

        var entries = new List<AgentJobLogEntryDB>(decision.Logs.Count);
        foreach (var log in decision.Logs)
            entries.Add(AddLog(job, log.Message, log.Level));

        return entries;
    }

    /// <summary>Creates and attaches a job log row.</summary>
    public AgentJobLogEntryDB AddLog(
        AgentJobDB job,
        string message,
        string level = JobLogLevels.Info)
    {
        ArgumentNullException.ThrowIfNull(job);

        var entry = new AgentJobLogEntryDB
        {
            AgentJobId = job.Id,
            Message = message,
            Level = level
        };

        job.LogEntries.Add(entry);
        return entry;
    }

    /// <summary>
    /// Returns whether a channel/context permission set can preauthorize a
    /// pending user-facing approval for the requested clearance.
    /// </summary>
    public bool CanUseChannelPreauthorization(
        PermissionClearance agentClearance)
    {
        return agentClearance is PermissionClearance.ApprovedBySameLevelUser
            or PermissionClearance.ApprovedByWhitelistedUser
            or PermissionClearance.ApprovedByWhitelistedAgent;
    }

    /// <summary>
    /// Returns whether the caller must personally hold the same grant before
    /// channel/context preauthorization can be used.
    /// </summary>
    public bool RequiresCallerGrantForChannelPreauthorization(
        PermissionClearance agentClearance)
    {
        return agentClearance == PermissionClearance.ApprovedBySameLevelUser;
    }

    /// <summary>Projects a job and its log rows into the public response.</summary>
    public AgentJobResponse ToResponse(AgentJobDB job)
    {
        return ToResponse(job, ToLogResponses(job.LogEntries));
    }

    /// <summary>Projects a job and supplied log DTOs into the public response.</summary>
    public AgentJobResponse ToResponse(
        AgentJobDB job,
        IReadOnlyList<AgentJobLogResponse> logs)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(logs);

        var jobCost = job.PromptTokens is not null || job.CompletionTokens is not null
            ? new TokenUsageResponse(
                job.PromptTokens ?? 0,
                job.CompletionTokens ?? 0,
                (job.PromptTokens ?? 0) + (job.CompletionTokens ?? 0))
            : null;

        return new AgentJobResponse(
            Id: job.Id,
            ChannelId: job.ChannelId,
            AgentId: job.AgentId,
            ActionKey: job.ActionKey,
            ResourceId: job.ResourceId,
            Status: job.Status,
            EffectiveClearance: job.EffectiveClearance,
            ResultData: job.ResultData,
            ErrorLog: job.ErrorLog,
            Logs: logs,
            CreatedAt: job.CreatedAt,
            StartedAt: job.StartedAt,
            CompletedAt: job.CompletedAt,
            ScriptJson: job.ScriptJson,
            WorkingDirectory: job.WorkingDirectory,
            JobCost: jobCost);
    }

    /// <summary>Projects a job into the lightweight summary response.</summary>
    public AgentJobSummaryResponse ToSummaryResponse(AgentJobDB job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new AgentJobSummaryResponse(
            job.Id,
            job.ChannelId,
            job.AgentId,
            job.ActionKey,
            job.ResourceId,
            job.Status,
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt);
    }

    /// <summary>Projects persisted log rows into ordered log DTOs.</summary>
    public IReadOnlyList<AgentJobLogResponse> ToLogResponses(
        IEnumerable<AgentJobLogEntryDB> logs)
    {
        ArgumentNullException.ThrowIfNull(logs);

        return logs
            .OrderBy(static log => log.CreatedAt)
            .Select(ToLogResponse)
            .ToArray();
    }

    /// <summary>Projects a single persisted log row into its DTO.</summary>
    public AgentJobLogResponse ToLogResponse(AgentJobLogEntryDB log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return new AgentJobLogResponse(log.Message, log.Level, log.CreatedAt);
    }

    /// <summary>
    /// Splits one LLM round's token usage across the jobs that participated
    /// in that round. Any remainder is assigned to the first job.
    /// </summary>
    public void ApplyTokenUsage(
        IReadOnlyList<AgentJobDB> jobs,
        int promptTokens,
        int completionTokens)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        if (promptTokens < 0)
            throw new ArgumentOutOfRangeException(
                nameof(promptTokens),
                promptTokens,
                "Prompt tokens cannot be negative.");
        if (completionTokens < 0)
            throw new ArgumentOutOfRangeException(
                nameof(completionTokens),
                completionTokens,
                "Completion tokens cannot be negative.");
        if (jobs.Count == 0)
            return;

        var promptPer = promptTokens / jobs.Count;
        var completionPer = completionTokens / jobs.Count;
        var promptRemainder = promptTokens % jobs.Count;
        var completionRemainder = completionTokens % jobs.Count;

        for (var i = 0; i < jobs.Count; i++)
        {
            jobs[i].PromptTokens =
                (jobs[i].PromptTokens ?? 0)
                + promptPer
                + (i == 0 ? promptRemainder : 0);
            jobs[i].CompletionTokens =
                (jobs[i].CompletionTokens ?? 0)
                + completionPer
                + (i == 0 ? completionRemainder : 0);
        }
    }
}
