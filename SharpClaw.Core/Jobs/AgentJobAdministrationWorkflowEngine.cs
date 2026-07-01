using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Entities.Core.Jobs;

namespace SharpClaw.Core.Jobs;

public sealed class AgentJobAdministrationWorkflowEngine(
    AgentJobAdministrationEngine jobs,
    AgentJobLifecycleEngine lifecycle)
{
    public AgentJobAdministrationWorkflowEngine()
        : this(new AgentJobAdministrationEngine(), new AgentJobLifecycleEngine())
    {
    }

    public async Task<AgentJobResponse?> GetAsync(
        Guid jobId,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var job = await host.LoadJobAsync(jobId, ct);
        return job is null ? null : await BuildResponseAsync(job, host, ct);
    }

    public async Task<AgentJobSummaryResponse?> GetSummaryAsync(
        Guid jobId,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var job = await host.LoadJobAsync(jobId, ct);
        return job is null ? null : jobs.ToSummaryResponse(job);
    }

    public async Task<IReadOnlyList<AgentJobResponse>> ListAsync(
        Guid channelId,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var loaded = await host.ListJobsForChannelAsync(channelId, ct);
        return await BuildResponsesAsync(jobs.OrderMostRecent(loaded), host, ct);
    }

    public async Task<IReadOnlyList<AgentJobSummaryResponse>> ListSummariesAsync(
        Guid channelId,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var loaded = await host.ListJobsForChannelAsync(channelId, ct);
        return jobs
            .OrderMostRecent(loaded)
            .Select(jobs.ToSummaryResponse)
            .ToList();
    }

    public async Task<IReadOnlyList<AgentJobResponse>> ListByActionPrefixAsync(
        string actionKeyPrefix,
        Guid? resourceId,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKeyPrefix);
        ArgumentNullException.ThrowIfNull(host);

        var loaded = await host.ListJobsByActionPrefixAsync(
            actionKeyPrefix,
            resourceId,
            ct);
        return await BuildResponsesAsync(
            jobs.OrderMostRecent(FilterByActionPrefix(loaded, actionKeyPrefix, resourceId)),
            host,
            ct);
    }

    public async Task<IReadOnlyList<AgentJobSummaryResponse>>
        ListSummariesByActionPrefixAsync(
            string actionKeyPrefix,
            Guid? resourceId,
            IAgentJobAdministrationHost host,
            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKeyPrefix);
        ArgumentNullException.ThrowIfNull(host);

        var loaded = await host.ListJobsByActionPrefixAsync(
            actionKeyPrefix,
            resourceId,
            ct);
        return jobs
            .OrderMostRecent(FilterByActionPrefix(loaded, actionKeyPrefix, resourceId))
            .Select(jobs.ToSummaryResponse)
            .ToList();
    }

    public async Task<bool> JobExistsWithActionPrefixAsync(
        Guid jobId,
        string actionKeyPrefix,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKeyPrefix);
        ArgumentNullException.ThrowIfNull(host);

        var job = await host.LoadJobAsync(jobId, ct);
        return jobs.JobMatchesActionPrefix(job, actionKeyPrefix);
    }

    public async Task<AgentJobResponse?> CancelAsync(
        Guid jobId,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var job = await host.LoadJobAsync(jobId, ct);
        if (job is null)
            return null;

        await ApplySaveAndRespondAsync(
            job,
            lifecycle.Cancel(job.Status, DateTimeOffset.UtcNow),
            host,
            ct);
        return await BuildResponseAsync(job, host, ct);
    }

    public async Task<AgentJobResponse?> StopAsync(
        Guid jobId,
        string? requiredActionPrefix,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var job = await host.LoadJobAsync(jobId, ct);
        if (job is null)
            return null;

        await ApplySaveAndRespondAsync(
            job,
            lifecycle.Stop(
                job.Status,
                job.ActionKey,
                requiredActionPrefix,
                DateTimeOffset.UtcNow),
            host,
            ct);
        return await BuildResponseAsync(job, host, ct);
    }

    public async Task<AgentJobResponse?> PauseAsync(
        Guid jobId,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var job = await host.LoadJobAsync(jobId, ct);
        if (job is null)
            return null;

        await ApplySaveAndRespondAsync(job, lifecycle.Pause(job.Status), host, ct);
        return await BuildResponseAsync(job, host, ct);
    }

    public async Task<AgentJobResponse?> ResumeAsync(
        Guid jobId,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var job = await host.LoadJobAsync(jobId, ct);
        if (job is null)
            return null;

        await ApplySaveAndRespondAsync(job, lifecycle.Resume(job.Status), host, ct);
        return await BuildResponseAsync(job, host, ct);
    }

    public async Task RecordTokensAsync(
        IReadOnlyList<Guid> jobIds,
        int promptTokens,
        int completionTokens,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobIds);
        ArgumentNullException.ThrowIfNull(host);

        if (jobIds.Count == 0)
            return;

        var loaded = await host.LoadJobsByIdsAsync(jobIds, ct);
        var byId = loaded.ToDictionary(job => job.Id);
        var ordered = jobIds
            .Select(id => byId.GetValueOrDefault(id))
            .Where(job => job is not null)
            .Select(job => job!)
            .ToList();

        if (ordered.Count == 0)
            return;

        jobs.ApplyTokenUsage(ordered, promptTokens, completionTokens);
        await host.SaveAsync([], ct);
    }

    public async Task<AgentJobResponse> BuildResponseAsync(
        AgentJobDB job,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(host);

        var logs = await LoadLogResponsesAsync(job.Id, host, ct);
        return jobs.ToResponse(job, logs);
    }

    private async Task<IReadOnlyList<AgentJobResponse>> BuildResponsesAsync(
        IReadOnlyList<AgentJobDB> loaded,
        IAgentJobAdministrationHost host,
        CancellationToken ct)
    {
        var responses = new List<AgentJobResponse>(loaded.Count);
        foreach (var job in loaded)
            responses.Add(await BuildResponseAsync(job, host, ct));

        return responses;
    }

    private async Task<IReadOnlyList<AgentJobLogResponse>> LoadLogResponsesAsync(
        Guid jobId,
        IAgentJobAdministrationHost host,
        CancellationToken ct)
    {
        if (host.TryGetCachedJobLogResponses(jobId, out var cached))
            return cached ?? [];

        var entries = await host.LoadJobLogEntriesAsync(jobId, ct);
        var responses = jobs.ToLogResponses(entries);
        host.CacheJobLogResponses(jobId, responses);
        return responses;
    }

    private async Task ApplySaveAndRespondAsync(
        AgentJobDB job,
        AgentJobLifecycleDecision decision,
        IAgentJobAdministrationHost host,
        CancellationToken ct)
    {
        var logs = jobs.ApplyLifecycleDecision(job, decision);
        await host.SaveAsync(logs, ct);
    }

    private static IReadOnlyList<AgentJobDB> FilterByActionPrefix(
        IEnumerable<AgentJobDB> source,
        string actionKeyPrefix,
        Guid? resourceId)
    {
        return source
            .Where(job => job.ActionKey?.StartsWith(
                actionKeyPrefix,
                StringComparison.OrdinalIgnoreCase) == true)
            .Where(job => resourceId is null || job.ResourceId == resourceId)
            .ToList();
    }
}

public interface IAgentJobAdministrationHost
{
    Task<AgentJobDB?> LoadJobAsync(Guid jobId, CancellationToken ct);

    Task<IReadOnlyList<AgentJobDB>> LoadJobsByIdsAsync(
        IReadOnlyList<Guid> jobIds,
        CancellationToken ct);

    Task<IReadOnlyList<AgentJobDB>> ListJobsForChannelAsync(
        Guid channelId,
        CancellationToken ct);

    Task<IReadOnlyList<AgentJobDB>> ListJobsByActionPrefixAsync(
        string actionKeyPrefix,
        Guid? resourceId,
        CancellationToken ct);

    bool TryGetCachedJobLogResponses(
        Guid jobId,
        out IReadOnlyList<AgentJobLogResponse>? logs);

    Task<IReadOnlyList<AgentJobLogEntryDB>> LoadJobLogEntriesAsync(
        Guid jobId,
        CancellationToken ct);

    void CacheJobLogResponses(
        Guid jobId,
        IReadOnlyList<AgentJobLogResponse> logs);

    Task SaveAsync(
        IReadOnlyList<AgentJobLogEntryDB> logs,
        CancellationToken ct);
}
