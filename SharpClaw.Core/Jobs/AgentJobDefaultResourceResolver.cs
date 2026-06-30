using SharpClaw.Core.Modules;
using SharpClaw.Core.Permissions;
using SharpClaw.Core.Resources;

namespace SharpClaw.Core.Jobs;

/// <summary>
/// Store-neutral resolver for the default resource attached to a
/// per-resource job submission.
/// </summary>
public sealed class AgentJobDefaultResourceResolver(
    AgentJobAdministrationEngine jobAdministration,
    DefaultResourceEngine defaultResources)
{
    /// <summary>
    /// Resolves the effective resource id for a job action using SharpClaw's
    /// module delegate mapping and default-resource fallback order.
    /// </summary>
    public Guid? ResolveDefaultResource(
        AgentJobDefaultResourceResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ModuleRegistry);

        var delegateTo = jobAdministration.ResolveDelegateTo(
            request.ModuleRegistry,
            request.ActionKey);
        var defaultResourceKey = delegateTo is null
            ? null
            : request.ModuleRegistry.GetDefaultResourceKeyForDelegate(
                delegateTo);
        var resourceType = delegateTo is null
            ? null
            : request.ModuleRegistry.ResolveResourceType(delegateTo);

        return defaultResources.ResolveDefaultResource(
            new DefaultResourceResolutionRequest(
                defaultResourceKey,
                resourceType,
                request.ChannelDefaults,
                request.ContextDefaults,
                request.OrderedPermissionSets));
    }
}

/// <summary>
/// Host-loaded inputs for job default-resource resolution.
/// </summary>
public sealed record AgentJobDefaultResourceResolutionRequest(
    string? ActionKey,
    ModuleRegistry ModuleRegistry,
    DefaultResourceSetSnapshot? ChannelDefaults,
    DefaultResourceSetSnapshot? ContextDefaults,
    IReadOnlyList<PermissionSetSnapshot> OrderedPermissionSets);
