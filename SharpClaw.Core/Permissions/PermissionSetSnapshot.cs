using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Permissions;

/// <summary>
/// Store-neutral permission facts used by the Core permission evaluator.
/// Hosts may build this from EF entities, JSON records, remote APIs, or tests.
/// </summary>
public sealed record PermissionSetSnapshot(
    IReadOnlyList<GlobalFlagPermissionGrant> GlobalFlags,
    IReadOnlyList<ResourcePermissionGrant> ResourceAccesses,
    IReadOnlySet<Guid> ClearanceUserWhitelist,
    IReadOnlySet<Guid> ClearanceAgentWhitelist)
{
    /// <summary>An empty permission set with no grants or approver whitelists.</summary>
    public static PermissionSetSnapshot Empty { get; } = new(
        [],
        [],
        new HashSet<Guid>(),
        new HashSet<Guid>());

    /// <summary>
    /// Creates a snapshot from the shared contracts entity shape without
    /// retaining any dependency on an EF change tracker.
    /// </summary>
    public static PermissionSetSnapshot FromPermissionSet(PermissionSetDB permissionSet)
    {
        ArgumentNullException.ThrowIfNull(permissionSet);

        return new PermissionSetSnapshot(
            permissionSet.GlobalFlags
                .Select(flag => new GlobalFlagPermissionGrant(flag.FlagKey, flag.Clearance))
                .ToList(),
            permissionSet.ResourceAccesses
                .Select(access => new ResourcePermissionGrant(
                    access.ResourceType,
                    access.ResourceId,
                    access.Clearance,
                    access.SubType,
                    access.AccessLevel,
                    access.IsDefault))
                .ToList(),
            permissionSet.ClearanceUserWhitelist
                .Select(entry => entry.UserId)
                .ToHashSet(),
            permissionSet.ClearanceAgentWhitelist
                .Select(entry => entry.AgentId)
                .ToHashSet());
    }
}

/// <summary>A single global flag grant and its configured clearance.</summary>
public sealed record GlobalFlagPermissionGrant(
    string FlagKey,
    PermissionClearance Clearance);

/// <summary>A single resource grant and its configured clearance.</summary>
public sealed record ResourcePermissionGrant(
    string ResourceType,
    Guid ResourceId,
    PermissionClearance Clearance,
    string SubType = "",
    string? AccessLevel = null,
    bool IsDefault = false);
