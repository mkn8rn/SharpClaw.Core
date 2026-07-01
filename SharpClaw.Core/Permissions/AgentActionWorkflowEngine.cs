using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Core.Modules;

namespace SharpClaw.Core.Permissions;

public sealed class AgentActionWorkflowEngine(
    PermissionEvaluationEngine permissionEvaluator,
    PermissionDelegateEvaluationEngine permissionDelegates)
{
    public AgentActionWorkflowEngine()
        : this(
            new PermissionEvaluationEngine(),
            new PermissionDelegateEvaluationEngine(new PermissionEvaluationEngine()))
    {
    }

    public async Task<AgentActionResult> EvaluateGlobalFlagByKeyAsync(
        string flagKey,
        Guid agentId,
        ActionCaller caller,
        IAgentActionHost host,
        Guid? channelPermissionSetId = null,
        Guid? contextPermissionSetId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flagKey);
        ArgumentNullException.ThrowIfNull(host);

        var snapshots = await LoadSnapshotsAsync(
            agentId,
            caller,
            channelPermissionSetId,
            contextPermissionSetId,
            host,
            ct);

        return permissionEvaluator.EvaluateGlobalFlag(
            flagKey,
            snapshots.AgentRolePermissions,
            snapshots.ChannelPermissions,
            snapshots.ContextPermissions,
            snapshots.CallerPermissions,
            caller);
    }

    public Task<AgentActionResult>? TryEvaluateByDelegateNameAsync(
        string delegateName,
        Guid agentId,
        Guid? resourceId,
        ActionCaller caller,
        ModuleRegistry moduleRegistry,
        IAgentActionHost host,
        Guid? channelPermissionSetId = null,
        Guid? contextPermissionSetId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(delegateName);
        ArgumentNullException.ThrowIfNull(moduleRegistry);
        ArgumentNullException.ThrowIfNull(host);

        return permissionDelegates.TryEvaluateAsync(
            new PermissionDelegateEvaluationRequest(
                delegateName,
                resourceId,
                caller,
                moduleRegistry,
                innerCt => LoadSnapshotsAsync(
                    agentId,
                    caller,
                    channelPermissionSetId,
                    contextPermissionSetId,
                    host,
                    innerCt)),
            ct);
    }

    public bool HasGrantByDelegateName(
        PermissionSetSnapshot permissionSet,
        string delegateName,
        Guid? resourceId,
        ModuleRegistry moduleRegistry)
    {
        ArgumentNullException.ThrowIfNull(permissionSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(delegateName);
        ArgumentNullException.ThrowIfNull(moduleRegistry);

        var plan = PermissionDelegatePlanner.BuildPlan(
            delegateName,
            resourceId,
            moduleRegistry);

        return PermissionDelegatePlanner.HasGrant(permissionSet, plan);
    }

    private static async Task<PermissionDelegateSnapshotSet> LoadSnapshotsAsync(
        Guid agentId,
        ActionCaller caller,
        Guid? channelPermissionSetId,
        Guid? contextPermissionSetId,
        IAgentActionHost host,
        CancellationToken ct)
    {
        var agentPermissions = await host.LoadAgentPermissionSnapshotAsync(
            agentId,
            ct);
        var channelPermissions = await host.LoadPermissionSnapshotAsync(
            channelPermissionSetId,
            ct);
        var contextPermissions = await host.LoadPermissionSnapshotAsync(
            contextPermissionSetId,
            ct);
        var callerPermissions = await host.LoadCallerPermissionSnapshotAsync(
            caller,
            ct);

        return new PermissionDelegateSnapshotSet(
            agentPermissions,
            channelPermissions,
            contextPermissions,
            callerPermissions);
    }
}

public interface IAgentActionHost
{
    Task<PermissionSetSnapshot?> LoadAgentPermissionSnapshotAsync(
        Guid agentId,
        CancellationToken ct);

    Task<PermissionSetSnapshot?> LoadCallerPermissionSnapshotAsync(
        ActionCaller caller,
        CancellationToken ct);

    Task<PermissionSetSnapshot?> LoadPermissionSnapshotAsync(
        Guid? permissionSetId,
        CancellationToken ct);
}
