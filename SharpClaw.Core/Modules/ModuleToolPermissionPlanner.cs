using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Modules;

/// <summary>
/// Store-neutral module tool permission planning. Hosts own persistence and
/// host-specific delegate execution; Core owns action-key resolution,
/// descriptor checks, dispatch mode selection, and denial messages.
/// </summary>
public sealed class ModuleToolPermissionPlanner
{
    /// <summary>
    /// Builds the permission dispatch plan for a module tool action.
    /// </summary>
    public ModuleToolPermissionPlan BuildPlan(
        string? actionKey,
        Guid? resourceId,
        ModuleRegistry moduleRegistry)
    {
        ArgumentNullException.ThrowIfNull(moduleRegistry);

        if (string.IsNullOrWhiteSpace(actionKey))
        {
            return ModuleToolPermissionPlan.Denied(
                actionKey,
                null,
                null,
                "Module action requires an ActionKey to resolve permissions.",
                ModuleToolPermissionDenialReason.MissingActionKey);
        }

        if (!moduleRegistry.TryResolve(actionKey, out var moduleId, out var toolName))
        {
            return ModuleToolPermissionPlan.Denied(
                actionKey,
                null,
                null,
                $"No module registered for tool '{actionKey}'.",
                ModuleToolPermissionDenialReason.ToolNotRegistered);
        }

        var descriptor = moduleRegistry.GetPermissionDescriptor(moduleId, toolName);
        if (descriptor is null)
        {
            return ModuleToolPermissionPlan.Denied(
                actionKey,
                moduleId,
                toolName,
                $"Module tool '{actionKey}' has no permission descriptor.",
                ModuleToolPermissionDenialReason.MissingPermissionDescriptor);
        }

        if (descriptor.IsPerResource && !resourceId.HasValue)
        {
            return ModuleToolPermissionPlan.Denied(
                actionKey,
                moduleId,
                toolName,
                $"ResourceId is required for module tool '{actionKey}'.",
                ModuleToolPermissionDenialReason.MissingResourceId);
        }

        if (descriptor.Check is not null)
        {
            return new ModuleToolPermissionPlan(
                ModuleToolPermissionPlanKind.DirectCheck,
                actionKey,
                moduleId,
                toolName,
                descriptor,
                descriptor.Check,
                null,
                null,
                null);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.DelegateTo))
        {
            return new ModuleToolPermissionPlan(
                ModuleToolPermissionPlanKind.DelegateToHost,
                actionKey,
                moduleId,
                toolName,
                descriptor,
                null,
                descriptor.DelegateTo,
                null,
                null);
        }

        return ModuleToolPermissionPlan.Denied(
            actionKey,
            moduleId,
            toolName,
            $"Module tool '{actionKey}' has no permission check configured.",
            ModuleToolPermissionDenialReason.NoPermissionCheckConfigured);
    }
}

/// <summary>
/// Host-executable permission dispatch plan for a module tool action.
/// </summary>
public sealed record ModuleToolPermissionPlan(
    ModuleToolPermissionPlanKind Kind,
    string? ActionKey,
    string? ModuleId,
    string? ToolName,
    ModuleToolPermission? Descriptor,
    Func<Guid, Guid?, ActionCaller, CancellationToken, Task<AgentActionResult>>? DirectCheck,
    string? DelegateTo,
    ModuleToolPermissionDenialReason? DenialReason,
    AgentActionResult? DeniedResult)
{
    /// <summary>Creates a denied plan with the standard denied result.</summary>
    public static ModuleToolPermissionPlan Denied(
        string? actionKey,
        string? moduleId,
        string? toolName,
        string reason,
        ModuleToolPermissionDenialReason denialReason) =>
        new(
            ModuleToolPermissionPlanKind.Denied,
            actionKey,
            moduleId,
            toolName,
            null,
            null,
            null,
            denialReason,
            AgentActionResult.Denied(reason));

    /// <summary>
    /// Returns the standard denial used when a host cannot execute a delegate.
    /// </summary>
    public AgentActionResult CreateUnrecognizedDelegateDeniedResult()
    {
        if (Kind != ModuleToolPermissionPlanKind.DelegateToHost
            || string.IsNullOrWhiteSpace(ActionKey)
            || string.IsNullOrWhiteSpace(DelegateTo))
        {
            throw new InvalidOperationException(
                "Only delegate permission plans can produce unrecognized delegate denials.");
        }

        return AgentActionResult.Denied(
            $"Module tool '{ActionKey}' delegates to '{DelegateTo}' " +
            "which is not a recognised permission check method.");
    }
}

/// <summary>
/// Permission dispatch mode selected for a module tool action.
/// </summary>
public enum ModuleToolPermissionPlanKind
{
    /// <summary>The action is denied before host delegate execution.</summary>
    Denied,

    /// <summary>The host should call the module-supplied direct check.</summary>
    DirectCheck,

    /// <summary>The host should call one of its named permission delegates.</summary>
    DelegateToHost
}

/// <summary>
/// Store-neutral reason a module permission plan was denied before host
/// delegate execution.
/// </summary>
public enum ModuleToolPermissionDenialReason
{
    /// <summary>The job had no action key to resolve permissions from.</summary>
    MissingActionKey,

    /// <summary>No registered module tool matched the action key.</summary>
    ToolNotRegistered,

    /// <summary>The resolved tool did not declare a permission descriptor.</summary>
    MissingPermissionDescriptor,

    /// <summary>The tool requires a resource id and the job did not provide one.</summary>
    MissingResourceId,

    /// <summary>The tool declared neither a direct check nor a host delegate.</summary>
    NoPermissionCheckConfigured
}
