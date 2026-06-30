using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Core.Modules;

namespace SharpClaw.Core.Permissions;

/// <summary>
/// Store-neutral executor for permission delegate method names exposed by
/// modules.
/// </summary>
public sealed class PermissionDelegateEvaluationEngine(
    PermissionEvaluationEngine permissionEvaluator)
{
    /// <summary>
    /// Evaluates a delegate method name if the module registry can map it to a
    /// SharpClaw permission shape. Returns <see langword="null"/> when the
    /// delegate is not recognized or cannot be evaluated without a resource id.
    /// </summary>
    public Task<AgentActionResult>? TryEvaluateAsync(
        PermissionDelegateEvaluationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DelegateName);
        ArgumentNullException.ThrowIfNull(request.ModuleRegistry);
        ArgumentNullException.ThrowIfNull(request.LoadSnapshotsAsync);

        var plan = PermissionDelegatePlanner.BuildPlan(
            request.DelegateName,
            request.ResourceId,
            request.ModuleRegistry);

        return plan.Kind switch
        {
            PermissionDelegatePlanKind.GlobalFlag
                => EvaluateGlobalFlagAsync(request, plan, ct),
            PermissionDelegatePlanKind.ResourceAccess when plan.ResourceId.HasValue
                => EvaluateResourceAccessAsync(request, plan, ct),
            _ => null
        };
    }

    private async Task<AgentActionResult> EvaluateGlobalFlagAsync(
        PermissionDelegateEvaluationRequest request,
        PermissionDelegatePlan plan,
        CancellationToken ct)
    {
        var snapshots = await request.LoadSnapshotsAsync(ct);
        return permissionEvaluator.EvaluateGlobalFlag(
            plan.FlagKey
                ?? throw new InvalidOperationException(
                    "Global flag delegate plan has no flag key."),
            snapshots.AgentRolePermissions,
            snapshots.ChannelPermissions,
            snapshots.ContextPermissions,
            snapshots.CallerPermissions,
            request.Caller);
    }

    private async Task<AgentActionResult> EvaluateResourceAccessAsync(
        PermissionDelegateEvaluationRequest request,
        PermissionDelegatePlan plan,
        CancellationToken ct)
    {
        var resourceType = plan.ResourceType
            ?? throw new InvalidOperationException(
                "Resource delegate plan has no resource type.");
        var resourceId = plan.ResourceId
            ?? throw new InvalidOperationException(
                "Resource delegate plan has no resource id.");
        var snapshots = await request.LoadSnapshotsAsync(ct);

        return permissionEvaluator.EvaluateResourceAccess(
            resourceType,
            resourceId,
            $"{resourceType} access",
            snapshots.AgentRolePermissions,
            snapshots.ChannelPermissions,
            snapshots.ContextPermissions,
            snapshots.CallerPermissions,
            request.Caller);
    }
}

/// <summary>
/// Inputs for permission delegate evaluation.
/// </summary>
public sealed record PermissionDelegateEvaluationRequest(
    string DelegateName,
    Guid? ResourceId,
    ActionCaller Caller,
    ModuleRegistry ModuleRegistry,
    Func<CancellationToken, Task<PermissionDelegateSnapshotSet>>
        LoadSnapshotsAsync);

/// <summary>
/// Permission snapshots needed to evaluate a delegate plan.
/// </summary>
public sealed record PermissionDelegateSnapshotSet(
    PermissionSetSnapshot? AgentRolePermissions,
    PermissionSetSnapshot? ChannelPermissions,
    PermissionSetSnapshot? ContextPermissions,
    PermissionSetSnapshot? CallerPermissions);
