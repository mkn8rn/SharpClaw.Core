using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Modules;

namespace SharpClaw.Core.Jobs;

/// <summary>
/// Store-neutral orchestration for SharpClaw job submission, approval,
/// execution, and lifecycle persistence timing.
/// </summary>
public sealed class AgentJobRuntimeEngine(
    AgentJobLifecycleEngine lifecycle,
    AgentJobAdministrationEngine jobs)
{
    /// <summary>
    /// Submits a job, evaluates permission, optionally executes it, and
    /// returns the host-materialized response.
    /// </summary>
    public async Task<AgentJobResponse> SubmitAsync(
        Guid channelId,
        SubmitAgentJobRequest request,
        IAgentJobRuntimeHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var channel = await host.LoadSubmissionChannelAsync(channelId, ct)
            ?? throw new InvalidOperationException(
                $"Channel {channelId} not found.");

        var agentId = jobs.ResolveSubmissionAgent(
            channel,
            channelId,
            request.AgentId);

        var effectiveResourceId = request.ResourceId;
        if (!effectiveResourceId.HasValue
            && jobs.IsPerResourceAction(host.ModuleRegistry, request.ActionKey))
        {
            effectiveResourceId = await host.ResolveDefaultResourceIdAsync(
                request.ActionKey,
                channelId,
                agentId,
                ct);
        }

        var job = jobs.CreateSubmissionJob(
            channelId,
            agentId,
            request,
            host.SessionUserId,
            effectiveResourceId);

        host.TrackJob(job);
        await ApplyAndSaveAsync(job, lifecycle.Queue(request.ActionKey), host, ct);

        var caller = new ActionCaller(
            host.SessionUserId,
            request.CallerAgentId);
        var result = await host.DispatchPermissionCheckAsync(
            agentId,
            job.ResourceId,
            caller,
            job.ActionKey,
            channel.PermissionSetId,
            channel.AgentContext?.PermissionSetId,
            ct);

        job.EffectiveClearance = result.EffectiveClearance;

        var channelPreauthorized =
            result.Verdict == ClearanceVerdict.PendingApproval
            && await host.HasChannelAuthorizationAsync(
                channelId,
                job.ResourceId,
                result.EffectiveClearance,
                host.SessionUserId,
                job.ActionKey,
                ct);

        var submissionDecision = lifecycle.ResolveSubmissionPermission(
            result,
            channelPreauthorized);
        var submissionLogs = jobs.ApplyLifecycleDecision(
            job,
            submissionDecision);

        if (submissionDecision.ShouldExecute)
        {
            await ExecuteAsync(job, host, initialLogs: submissionLogs, ct);
        }
        else
        {
            await host.SaveAsync(submissionLogs, ct);
        }

        host.CacheJobLogs(job.Id, jobs.ToLogResponses(job.LogEntries));
        return await host.BuildResponseAsync(job, ct);
    }

    /// <summary>
    /// Approves a previously loaded job, optionally executing it when the
    /// approval has sufficient clearance.
    /// </summary>
    public async Task<AgentJobResponse> ApproveAsync(
        AgentJobDB job,
        ApproveAgentJobRequest request,
        IAgentJobRuntimeHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        if (job.Status != AgentJobStatus.AwaitingApproval)
        {
            await ApplyAndSaveAsync(
                job,
                lifecycle.RejectApprovalForStatus(job.Status),
                host,
                ct);
            return await host.BuildResponseAsync(job, ct);
        }

        var approver = new ActionCaller(
            host.SessionUserId,
            request.ApproverAgentId);
        var channel = await host.LoadApprovalChannelAsync(job.ChannelId, ct);
        var result = await host.DispatchPermissionCheckAsync(
            job.AgentId,
            job.ResourceId,
            approver,
            job.ActionKey,
            channel?.PermissionSetId,
            channel?.AgentContext?.PermissionSetId,
            ct);

        var approvalDecision = lifecycle.ResolveApproval(
            result,
            approver,
            DateTimeOffset.UtcNow);
        if (approvalDecision.ShouldExecute)
        {
            job.ApprovedByUserId = host.SessionUserId;
            job.ApprovedByAgentId = request.ApproverAgentId;
        }

        var approvalLogs = jobs.ApplyLifecycleDecision(job, approvalDecision);
        if (approvalDecision.ShouldExecute)
        {
            await ExecuteAsync(job, host, initialLogs: approvalLogs, ct);
        }
        else
        {
            await host.SaveAsync(approvalLogs, ct);
        }

        return await host.BuildResponseAsync(job, ct);
    }

    /// <summary>
    /// Executes a job under Core lifecycle semantics while the host owns
    /// concrete module dispatch.
    /// </summary>
    public async Task ExecuteAsync(
        AgentJobDB job,
        IAgentJobRuntimeHost host,
        IReadOnlyList<AgentJobLogEntryDB>? initialLogs = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(host);

        if (initialLogs is { Count: > 0 })
            await host.SaveAsync(initialLogs, ct);

        await ApplyAndSaveAsync(
            job,
            lifecycle.BeginExecution(DateTimeOffset.UtcNow),
            host,
            ct);

        var dispatchLogs = new List<AgentJobLogEntryDB>();
        try
        {
            var execution = await host.DispatchExecutionAsync(
                job,
                (message, level) => dispatchLogs.Add(
                    jobs.AddLog(job, message, level)),
                ct);
            var completionDecision = lifecycle.CompleteExecution(
                execution.ResultData,
                execution.CompletionBehavior,
                DateTimeOffset.UtcNow);
            var completionLogs = jobs.ApplyLifecycleDecision(
                job,
                completionDecision);
            dispatchLogs.AddRange(completionLogs);

            if (execution.CompletionBehavior
                == ModuleJobCompletionBehavior.RemainExecuting)
            {
                host.LogLongRunningExecutionStarted(job);
            }
        }
        catch (Exception ex)
        {
            dispatchLogs.AddRange(jobs.ApplyLifecycleDecision(
                job,
                lifecycle.FailExecution(
                    ex.Message,
                    ex.ToString(),
                    DateTimeOffset.UtcNow)));
            host.LogExecutionFailed(job, ex);
        }

        await host.SaveAsync(dispatchLogs, ct);
    }

    private async Task ApplyAndSaveAsync(
        AgentJobDB job,
        AgentJobLifecycleDecision decision,
        IAgentJobRuntimeHost host,
        CancellationToken ct)
    {
        var logs = jobs.ApplyLifecycleDecision(job, decision);
        await host.SaveAsync(logs, ct);
    }
}

/// <summary>
/// Host-owned capabilities required by the store-neutral job runtime.
/// </summary>
public interface IAgentJobRuntimeHost
{
    /// <summary>The current session user, when authenticated.</summary>
    Guid? SessionUserId { get; }

    /// <summary>The module registry used for action-key resolution.</summary>
    ModuleRegistry ModuleRegistry { get; }

    /// <summary>Loads a channel with enough shape to resolve the executing agent.</summary>
    Task<ChannelDB?> LoadSubmissionChannelAsync(
        Guid channelId,
        CancellationToken ct);

    /// <summary>Loads a channel with enough shape to evaluate approval permissions.</summary>
    Task<ChannelDB?> LoadApprovalChannelAsync(
        Guid channelId,
        CancellationToken ct);

    /// <summary>Resolves the default resource id for a per-resource action.</summary>
    Task<Guid?> ResolveDefaultResourceIdAsync(
        string? actionKey,
        Guid channelId,
        Guid agentId,
        CancellationToken ct);

    /// <summary>Marks a new job for persistence in the host unit of work.</summary>
    void TrackJob(AgentJobDB job);

    /// <summary>Saves the host unit of work and caches the supplied log rows.</summary>
    Task SaveAsync(
        IReadOnlyList<AgentJobLogEntryDB> logs,
        CancellationToken ct);

    /// <summary>Runs the host permission dispatcher for a job action.</summary>
    Task<AgentActionResult> DispatchPermissionCheckAsync(
        Guid agentId,
        Guid? resourceId,
        ActionCaller caller,
        string? actionKey,
        Guid? channelPermissionSetId,
        Guid? contextPermissionSetId,
        CancellationToken ct);

    /// <summary>Checks whether channel/context grants preauthorize a pending job.</summary>
    Task<bool> HasChannelAuthorizationAsync(
        Guid channelId,
        Guid? resourceId,
        PermissionClearance agentClearance,
        Guid? callerUserId,
        string? actionKey,
        CancellationToken ct);

    /// <summary>Dispatches concrete module execution for a job.</summary>
    Task<AgentJobExecutionDispatchResult> DispatchExecutionAsync(
        AgentJobDB job,
        Action<string, string> addLog,
        CancellationToken ct);

    /// <summary>Records host diagnostics for long-running execution.</summary>
    void LogLongRunningExecutionStarted(AgentJobDB job);

    /// <summary>Records host diagnostics for failed execution.</summary>
    void LogExecutionFailed(AgentJobDB job, Exception exception);

    /// <summary>Stores the complete job log cache after submission completes.</summary>
    void CacheJobLogs(Guid jobId, IReadOnlyList<AgentJobLogResponse> logs);

    /// <summary>Builds the host-visible job response with host log loading.</summary>
    Task<AgentJobResponse> BuildResponseAsync(
        AgentJobDB job,
        CancellationToken ct);
}

/// <summary>
/// Store-neutral result of host-owned module execution dispatch.
/// </summary>
public sealed record AgentJobExecutionDispatchResult(
    string? ResultData,
    ModuleJobCompletionBehavior CompletionBehavior);
