using SharpClaw.Core.Modules;

namespace SharpClaw.Core.Permissions;

/// <summary>
/// Store-neutral resolver for module permission delegate method names.
/// Core owns the mapping from delegate name to permission shape; hosts own
/// loading permission snapshots and executing the resulting evaluation.
/// </summary>
public static class PermissionDelegatePlanner
{
    /// <summary>
    /// Builds a permission plan for a module delegate method name.
    /// Global flag delegates take priority over resource delegates, matching
    /// the historical SharpClaw permission dispatch order.
    /// </summary>
    public static PermissionDelegatePlan BuildPlan(
        string delegateName,
        Guid? resourceId,
        ModuleRegistry moduleRegistry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(delegateName);
        ArgumentNullException.ThrowIfNull(moduleRegistry);

        var flagKey = moduleRegistry.ResolveGlobalFlag(delegateName);
        if (flagKey is not null)
        {
            return new PermissionDelegatePlan(
                PermissionDelegatePlanKind.GlobalFlag,
                delegateName,
                flagKey,
                null,
                resourceId);
        }

        var resourceType = moduleRegistry.ResolveResourceType(delegateName);
        if (resourceType is not null)
        {
            return new PermissionDelegatePlan(
                PermissionDelegatePlanKind.ResourceAccess,
                delegateName,
                null,
                resourceType,
                resourceId);
        }

        return new PermissionDelegatePlan(
            PermissionDelegatePlanKind.Unrecognized,
            delegateName,
            null,
            null,
            resourceId);
    }

    /// <summary>
    /// Returns whether a permission snapshot grants the permission described
    /// by a delegate plan. Resource plans support wildcard resource grants
    /// even when the concrete resource id is not available.
    /// </summary>
    public static bool HasGrant(
        PermissionSetSnapshot permissionSet,
        PermissionDelegatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(permissionSet);
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Kind switch
        {
            PermissionDelegatePlanKind.GlobalFlag
                => PermissionEvaluationEngine.HasGlobalFlagGrant(
                    permissionSet,
                    plan.FlagKey ?? throw new InvalidOperationException(
                        "Global flag delegate plan has no flag key.")),
            PermissionDelegatePlanKind.ResourceAccess
                => PermissionEvaluationEngine.HasResourceGrant(
                    permissionSet,
                    plan.ResourceType ?? throw new InvalidOperationException(
                        "Resource delegate plan has no resource type."),
                    plan.ResourceId),
            _ => false
        };
    }
}

/// <summary>
/// Store-neutral permission delegate plan produced from a module delegate
/// method name.
/// </summary>
public sealed record PermissionDelegatePlan(
    PermissionDelegatePlanKind Kind,
    string DelegateName,
    string? FlagKey,
    string? ResourceType,
    Guid? ResourceId);

/// <summary>
/// Permission shape selected for a module delegate method name.
/// </summary>
public enum PermissionDelegatePlanKind
{
    /// <summary>The delegate maps to a global flag permission.</summary>
    GlobalFlag,

    /// <summary>The delegate maps to a per-resource permission.</summary>
    ResourceAccess,

    /// <summary>The delegate name is not known to the module registry.</summary>
    Unrecognized
}
