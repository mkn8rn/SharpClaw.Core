using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Permissions;

/// <summary>
/// Store-neutral implementation of SharpClaw permission and clearance
/// semantics. Application hosts supply permission snapshots; Core owns the
/// decision rules.
/// </summary>
public sealed class PermissionEvaluationEngine
{
    /// <summary>
    /// Evaluates a global flag permission across channel, context, and role
    /// layers, then checks whether the caller satisfies the effective
    /// clearance requirement.
    /// </summary>
    public AgentActionResult EvaluateGlobalFlag(
        string flagKey,
        PermissionSetSnapshot? agentRolePermissions,
        PermissionSetSnapshot? channelPermissions,
        PermissionSetSnapshot? contextPermissions,
        PermissionSetSnapshot? callerPermissions,
        ActionCaller caller)
    {
        if (agentRolePermissions is null)
            return AgentActionResult.Denied("Agent has no role or permissions assigned.");

        if (!HasGlobalFlagGrant(agentRolePermissions, flagKey))
            return AgentActionResult.Denied($"Agent does not have permission to {flagKey}.");

        var roleClearance = GetGlobalFlagClearance(agentRolePermissions, flagKey);
        var (effective, blockingLayer) = ResolveClearanceAcrossLayers(
            grantExists: ps => HasGlobalFlagGrant(ps, flagKey),
            getClearance: ps => GetGlobalFlagClearance(ps, flagKey),
            channelPermissions,
            contextPermissions,
            roleClearance);

        return EvaluateCallerClearance(
            agentRolePermissions,
            effective,
            caller,
            callerPermissions,
            callerPerms => HasGlobalFlagGrant(callerPerms, flagKey),
            blockingLayer);
    }

    /// <summary>
    /// Evaluates a per-resource permission across channel, context, and role
    /// layers, then checks whether the caller satisfies the effective
    /// clearance requirement.
    /// </summary>
    public AgentActionResult EvaluateResourceAccess(
        string resourceType,
        Guid resourceId,
        string resourceDescription,
        PermissionSetSnapshot? agentRolePermissions,
        PermissionSetSnapshot? channelPermissions,
        PermissionSetSnapshot? contextPermissions,
        PermissionSetSnapshot? callerPermissions,
        ActionCaller caller)
    {
        if (agentRolePermissions is null)
            return AgentActionResult.Denied("Agent has no role or permissions assigned.");

        var access = FindResourceGrant(agentRolePermissions, resourceType, resourceId);
        if (access is null)
            return AgentActionResult.Denied($"Agent does not have {resourceDescription}.");

        var (effective, blockingLayer) = ResolveClearanceAcrossLayers(
            grantExists: ps => HasResourceGrant(ps, resourceType, resourceId),
            getClearance: ps => FindResourceGrant(ps, resourceType, resourceId)?.Clearance
                ?? PermissionClearance.Unset,
            channelPermissions,
            contextPermissions,
            access.Clearance);

        return EvaluateCallerClearance(
            agentRolePermissions,
            effective,
            caller,
            callerPermissions,
            callerPerms => HasResourceGrant(callerPerms, resourceType, resourceId),
            blockingLayer);
    }

    /// <summary>Returns whether a snapshot contains a global flag grant.</summary>
    public static bool HasGlobalFlagGrant(PermissionSetSnapshot permissionSet, string flagKey) =>
        permissionSet.GlobalFlags.Any(flag => flag.FlagKey == flagKey);

    /// <summary>
    /// Returns whether a snapshot contains a resource grant for the concrete
    /// resource id or the wildcard all-resources id.
    /// </summary>
    public static bool HasResourceGrant(
        PermissionSetSnapshot permissionSet,
        string resourceType,
        Guid? resourceId) =>
        FindResourceGrant(permissionSet, resourceType, resourceId) is not null;

    /// <summary>
    /// Finds the first matching resource grant for the concrete resource id
    /// or the wildcard all-resources id.
    /// </summary>
    public static ResourcePermissionGrant? FindResourceGrant(
        PermissionSetSnapshot permissionSet,
        string resourceType,
        Guid? resourceId) =>
        permissionSet.ResourceAccesses.FirstOrDefault(access =>
            access.ResourceType == resourceType
            && (access.ResourceId == resourceId
                || access.ResourceId == WellKnownIds.AllResources));

    private static PermissionClearance GetGlobalFlagClearance(
        PermissionSetSnapshot permissionSet,
        string flagKey) =>
        permissionSet.GlobalFlags.FirstOrDefault(flag => flag.FlagKey == flagKey)?.Clearance
        ?? PermissionClearance.Unset;

    private static (PermissionClearance Clearance, string? BlockingLayer) ResolveClearanceAcrossLayers(
        Func<PermissionSetSnapshot, bool> grantExists,
        Func<PermissionSetSnapshot, PermissionClearance> getClearance,
        PermissionSetSnapshot? channelPermissions,
        PermissionSetSnapshot? contextPermissions,
        PermissionClearance roleClearance)
    {
        if (channelPermissions is not null && grantExists(channelPermissions))
        {
            var clearance = getClearance(channelPermissions);
            if (clearance == PermissionClearance.Restricted)
                return (PermissionClearance.Restricted, "channel");
            if (clearance != PermissionClearance.Unset)
                return (clearance, null);
        }

        if (contextPermissions is not null && grantExists(contextPermissions))
        {
            var clearance = getClearance(contextPermissions);
            if (clearance == PermissionClearance.Restricted)
                return (PermissionClearance.Restricted, "context");
            if (clearance != PermissionClearance.Unset)
                return (clearance, null);
        }

        if (roleClearance == PermissionClearance.Restricted)
            return (PermissionClearance.Restricted, "role");

        return (roleClearance, null);
    }

    private static AgentActionResult EvaluateCallerClearance(
        PermissionSetSnapshot agentPermissions,
        PermissionClearance effectiveClearance,
        ActionCaller caller,
        PermissionSetSnapshot? callerPermissions,
        Func<PermissionSetSnapshot, bool> callerHasSamePermission,
        string? blockingLayer)
    {
        if (effectiveClearance == PermissionClearance.Restricted)
        {
            return AgentActionResult.Denied(
                $"Permission is restricted (hard denied) at the {blockingLayer ?? "unknown"} layer. "
                + "No approval path exists.");
        }

        if (effectiveClearance == PermissionClearance.Unset)
        {
            return AgentActionResult.Denied(
                "Permission clearance is unset across all layers. "
                + "An admin must set a clearance level on at least one layer.");
        }

        if (effectiveClearance == PermissionClearance.Independent)
            return AgentActionResult.Approve("Agent can act independently.", effectiveClearance);

        if (caller.UserId is null && caller.AgentId is null)
        {
            return AgentActionResult.Pending(
                $"No caller identified. Awaiting approval (clearance: {effectiveClearance}).",
                effectiveClearance);
        }

        if (caller.AgentId is { } whitelistAgentId
            && effectiveClearance == PermissionClearance.ApprovedByWhitelistedAgent
            && agentPermissions.ClearanceAgentWhitelist.Contains(whitelistAgentId))
        {
            return AgentActionResult.Approve("Approved by whitelisted agent.", effectiveClearance);
        }

        if (caller.AgentId is not null
            && effectiveClearance is PermissionClearance.ApprovedByPermittedAgent
                or PermissionClearance.ApprovedByWhitelistedAgent
            && callerPermissions is not null
            && callerHasSamePermission(callerPermissions))
        {
            return AgentActionResult.Approve("Approved by permitted agent.", effectiveClearance);
        }

        if (caller.UserId is { } whitelistUserId
            && effectiveClearance is PermissionClearance.ApprovedByWhitelistedUser
                or PermissionClearance.ApprovedByWhitelistedAgent
            && agentPermissions.ClearanceUserWhitelist.Contains(whitelistUserId))
        {
            return AgentActionResult.Approve("Approved by whitelisted user.", effectiveClearance);
        }

        if (caller.UserId is not null
            && effectiveClearance != PermissionClearance.ApprovedByPermittedAgent
            && callerPermissions is not null
            && callerHasSamePermission(callerPermissions))
        {
            return AgentActionResult.Approve("Approved by same-level user.", effectiveClearance);
        }

        return AgentActionResult.Pending(
            $"Awaiting approval (clearance: {effectiveClearance}).",
            effectiveClearance);
    }
}
