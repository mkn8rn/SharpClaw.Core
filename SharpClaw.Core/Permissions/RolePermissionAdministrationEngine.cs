using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Roles;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Permissions;

/// <summary>
/// Store-neutral role permission administration rules.
/// </summary>
public sealed class RolePermissionAdministrationEngine
{
    /// <summary>
    /// Validates that the caller may grant every requested permission.
    /// </summary>
    public void ValidateRequestedGrants(
        SetRolePermissionsRequest request,
        PermissionSetDB? callerPermissions)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateGlobalFlags(request, callerPermissions);
        ValidateResourceGrants(request, callerPermissions);
    }

    /// <summary>
    /// Reconciles a target permission set with the requested role state.
    /// </summary>
    public void ReconcilePermissionSet(
        PermissionSetDB permissionSet,
        SetRolePermissionsRequest request)
    {
        ArgumentNullException.ThrowIfNull(permissionSet);
        ArgumentNullException.ThrowIfNull(request);

        ReconcileGlobalFlags(permissionSet, request.GlobalFlags);
        ReconcileResourceAccesses(permissionSet, request.ResourceGrants);
    }

    /// <summary>Returns whether unique-name enforcement should be active.</summary>
    public static bool IsUniqueRoleNameEnforced(string? configurationValue)
    {
        return configurationValue is null
            || !bool.TryParse(configurationValue, out var enforced)
            || enforced;
    }

    /// <summary>Throws when a role name already exists.</summary>
    public void EnsureRoleNameAvailable(string name, IEnumerable<string> existingRoleNames)
    {
        ArgumentNullException.ThrowIfNull(existingRoleNames);

        var normalized = name.Trim();
        if (existingRoleNames.Any(existing =>
                existing.Trim().Equals(
                    normalized,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"A role named '{name}' already exists.");
        }
    }

    private static void ValidateGlobalFlags(
        SetRolePermissionsRequest request,
        PermissionSetDB? callerPermissions)
    {
        if (request.GlobalFlags is null or { Count: 0 })
            return;

        if (callerPermissions is null)
            throw new UnauthorizedAccessException(
                "You have no permissions - cannot grant any global flags.");

        foreach (var (flagKey, _) in request.GlobalFlags)
        {
            if (!callerPermissions.GlobalFlags.Any(flag => flag.FlagKey == flagKey))
                throw new UnauthorizedAccessException(
                    $"Cannot grant {flagKey} - you do not hold this permission.");
        }
    }

    private static void ValidateResourceGrants(
        SetRolePermissionsRequest request,
        PermissionSetDB? callerPermissions)
    {
        if (request.ResourceGrants is null or { Count: 0 })
            return;

        foreach (var (resourceType, grants) in request.ResourceGrants)
            ValidateGrants(resourceType, resourceType, grants, callerPermissions);
    }

    private static void ValidateGrants(
        string name,
        string resourceType,
        IReadOnlyList<ResourceGrant>? requested,
        PermissionSetDB? callerPermissions)
    {
        if (requested is null or { Count: 0 })
            return;

        var callerGrants = callerPermissions?.ResourceAccesses
            .Where(access => access.ResourceType == resourceType)
            .Select(access => access.ResourceId)
            .ToHashSet();

        if (callerGrants is null or { Count: 0 })
            throw new UnauthorizedAccessException(
                $"Cannot grant {name} - you hold no grants of this type.");

        var hasWildcard = callerGrants.Contains(WellKnownIds.AllResources);

        foreach (var grant in requested)
        {
            if (hasWildcard)
                continue;

            if (!callerGrants.Contains(grant.ResourceId))
                throw new UnauthorizedAccessException(
                    $"Cannot grant {name} for resource {grant.ResourceId} " +
                    "- you do not hold this permission.");
        }
    }

    private static void ReconcileGlobalFlags(
        PermissionSetDB permissionSet,
        IReadOnlyDictionary<string, PermissionClearance>? requested)
    {
        var requestedMap = requested ?? new Dictionary<string, PermissionClearance>();

        foreach (var existing in permissionSet.GlobalFlags.ToList())
        {
            if (!requestedMap.TryGetValue(existing.FlagKey, out var newClearance))
                permissionSet.GlobalFlags.Remove(existing);
            else if (existing.Clearance != newClearance)
                existing.Clearance = newClearance;
        }

        var existingKeys = permissionSet.GlobalFlags
            .Select(flag => flag.FlagKey)
            .ToHashSet();

        foreach (var (key, clearance) in requestedMap)
        {
            if (!existingKeys.Contains(key))
            {
                permissionSet.GlobalFlags.Add(new GlobalFlagDB
                {
                    FlagKey = key,
                    Clearance = clearance
                });
            }
        }
    }

    private static void ReconcileResourceAccesses(
        PermissionSetDB permissionSet,
        IReadOnlyDictionary<string, IReadOnlyList<ResourceGrant>>? requested)
    {
        var requestedMap = requested?
            .SelectMany(kvp => kvp.Value.Select(grant =>
                (ResourceType: kvp.Key, grant.ResourceId, grant.Clearance)))
            .ToDictionary(
                item => (item.ResourceType, item.ResourceId),
                item => item.Clearance)
            ?? [];

        foreach (var access in permissionSet.ResourceAccesses.ToList())
        {
            var key = (access.ResourceType, access.ResourceId);
            if (!requestedMap.TryGetValue(key, out var newClearance))
            {
                if (access.ResourceId == WellKnownIds.AllResources)
                    throw new InvalidOperationException(
                        $"Wildcard grant for '{access.ResourceType}' is immutable and cannot be removed.");

                permissionSet.ResourceAccesses.Remove(access);
            }
            else if (access.Clearance != newClearance)
            {
                access.Clearance = newClearance;
            }
        }

        var existingKeys = permissionSet.ResourceAccesses
            .Select(access => (access.ResourceType, access.ResourceId))
            .ToHashSet();

        foreach (var (resourceType, grants) in requested
                 ?? new Dictionary<string, IReadOnlyList<ResourceGrant>>())
        {
            foreach (var grant in grants)
            {
                if (!existingKeys.Contains((resourceType, grant.ResourceId)))
                {
                    permissionSet.ResourceAccesses.Add(new ResourceAccessDB
                    {
                        ResourceType = resourceType,
                        ResourceId = grant.ResourceId,
                        Clearance = grant.Clearance
                    });
                }
            }
        }
    }
}
