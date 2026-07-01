using SharpClaw.Contracts.DTOs.Roles;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Core.Chat;

namespace SharpClaw.Core.Permissions;

/// <summary>
/// Store-neutral role administration workflow. Hosts own persistence and
/// user lookup; Core owns validation, mutation order, and invalidation plans.
/// </summary>
public sealed class RoleAdministrationEngine(
    RolePermissionAdministrationEngine rolePermissions,
    ChatRuntimeInvalidationPlanner invalidations)
{
    public RoleAdministrationEngine()
        : this(new RolePermissionAdministrationEngine(), new ChatRuntimeInvalidationPlanner())
    {
    }

    public async Task<RoleResponse> CreateAsync(
        string name,
        IRoleAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (host.UniqueRoleNamesEnforced)
        {
            rolePermissions.EnsureRoleNameAvailable(
                name,
                await host.ListRoleNamesAsync(null, ct));
        }

        var role = rolePermissions.CreateRole(name);
        host.TrackRole(role);
        await host.SaveAsync(null, ct);

        return rolePermissions.ToResponse(role);
    }

    public async Task<RolePermissionsResponse?> GetPermissionsAsync(
        Guid roleId,
        IRoleAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var role = await host.LoadRoleWithPermissionReferenceAsync(roleId, ct);
        if (role is null)
            return null;

        var permissionSet = role.PermissionSetId is { } permissionSetId
            ? await host.LoadFullPermissionSetAsync(permissionSetId, ct)
            : null;

        return rolePermissions.ToPermissionsResponse(role, permissionSet);
    }

    public async Task<RolePermissionsResponse?> SetPermissionsAsync(
        Guid roleId,
        SetRolePermissionsRequest request,
        Guid callerUserId,
        IRoleAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var role = await host.LoadRoleWithPermissionReferenceAsync(roleId, ct);
        if (role is null)
            return null;

        if (!await host.IsUserAdminAsync(callerUserId, ct))
        {
            var callerPermissionSet =
                await host.LoadCallerPermissionSetAsync(callerUserId, ct);
            rolePermissions.ValidateRequestedGrants(request, callerPermissionSet);
        }

        PermissionSetDB permissionSet;
        if (role.PermissionSetId is { } existingPermissionSetId)
        {
            permissionSet = await host.LoadFullPermissionSetAsync(
                    existingPermissionSetId,
                    ct)
                ?? throw new InvalidOperationException(
                    $"Permission set {existingPermissionSetId} not found.");
        }
        else
        {
            permissionSet = rolePermissions.CreatePermissionSetForRole(role);
            host.TrackPermissionSet(permissionSet);
        }

        rolePermissions.ReconcilePermissionSet(permissionSet, request);
        await host.SaveAsync(invalidations.PermissionSetsChanged, ct);

        return rolePermissions.ToPermissionsResponse(role, permissionSet);
    }

    public async Task<RoleResponse?> RenameAsync(
        Guid roleId,
        string newName,
        IRoleAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var role = await host.LoadRoleAsync(roleId, ct);
        if (role is null)
            return null;

        if (host.UniqueRoleNamesEnforced)
        {
            rolePermissions.EnsureRoleNameAvailable(
                newName,
                await host.ListRoleNamesAsync(roleId, ct));
        }

        rolePermissions.RenameRole(role, newName);
        await host.SaveAsync(invalidations.PermissionSetsChanged, ct);

        return rolePermissions.ToResponse(role);
    }

    public async Task<bool> DeleteAsync(
        Guid roleId,
        IRoleAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var role = await host.LoadRoleForDeleteAsync(roleId, ct);
        if (role is null)
            return false;

        var deletion = rolePermissions.PlanDeleteRole(role);
        host.ApplyRoleDeletion(deletion);
        await host.SaveAsync(invalidations.PermissionSetsChanged, ct);

        return true;
    }
}

public interface IRoleAdministrationHost
{
    bool UniqueRoleNamesEnforced { get; }

    Task<RoleDB?> LoadRoleAsync(Guid roleId, CancellationToken ct);

    Task<RoleDB?> LoadRoleWithPermissionReferenceAsync(
        Guid roleId,
        CancellationToken ct);

    Task<RoleDB?> LoadRoleForDeleteAsync(Guid roleId, CancellationToken ct);

    Task<PermissionSetDB?> LoadFullPermissionSetAsync(
        Guid permissionSetId,
        CancellationToken ct);

    Task<PermissionSetDB?> LoadCallerPermissionSetAsync(
        Guid userId,
        CancellationToken ct);

    Task<bool> IsUserAdminAsync(Guid userId, CancellationToken ct);

    Task<IReadOnlyList<string>> ListRoleNamesAsync(
        Guid? excludeId,
        CancellationToken ct);

    void TrackRole(RoleDB role);

    void TrackPermissionSet(PermissionSetDB permissionSet);

    void ApplyRoleDeletion(RoleDeletionPlan deletion);

    Task SaveAsync(
        Func<ChatRuntimeInvalidationPlan?>? buildInvalidationPlan,
        CancellationToken ct);
}
