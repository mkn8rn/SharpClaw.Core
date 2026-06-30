using SharpClaw.Contracts.DTOs.AgentActions;

namespace SharpClaw.Core.Modules;

/// <summary>
/// Store-neutral executor for module tool permission plans.
/// </summary>
public sealed class ModuleToolPermissionExecutor(
    ModuleToolPermissionPlanner planner)
{
    /// <summary>
    /// Resolves and executes the permission check for a module tool action.
    /// </summary>
    public async Task<AgentActionResult> ExecuteAsync(
        ModuleToolPermissionExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ModuleRegistry);
        ArgumentNullException.ThrowIfNull(request.EvaluateHostDelegateAsync);

        var plan = planner.BuildPlan(
            request.ActionKey,
            request.ResourceId,
            request.ModuleRegistry);

        if (plan.Kind == ModuleToolPermissionPlanKind.Denied)
        {
            if (plan.DenialReason
                == ModuleToolPermissionDenialReason.MissingResourceId)
            {
                request.Trace?.Invoke(
                    $"[PermissionCheck] DENIED: ResourceId is null for per-resource tool '{request.ActionKey}'");
            }

            return plan.DeniedResult
                ?? AgentActionResult.Denied(
                    "Module permission plan denied without a reason.");
        }

        request.Trace?.Invoke(
            $"[PermissionCheck] Tool='{plan.ActionKey}' AgentId={request.AgentId} ResourceId={request.ResourceId} DelegateTo='{plan.DelegateTo}'");

        if (plan.Kind == ModuleToolPermissionPlanKind.DirectCheck)
        {
            if (plan.DirectCheck is null)
            {
                throw new InvalidOperationException(
                    "Module permission plan requested direct check without a callback.");
            }

            return await plan.DirectCheck(
                request.AgentId,
                request.ResourceId,
                request.Caller,
                ct);
        }

        if (plan.Kind == ModuleToolPermissionPlanKind.DelegateToHost)
        {
            if (string.IsNullOrWhiteSpace(plan.DelegateTo))
            {
                throw new InvalidOperationException(
                    "Module permission plan requested host delegate without a delegate name.");
            }

            var result = await request.EvaluateHostDelegateAsync(
                plan.DelegateTo,
                request.AgentId,
                request.ResourceId,
                request.Caller,
                ct);
            return result ?? plan.CreateUnrecognizedDelegateDeniedResult();
        }

        throw new InvalidOperationException(
            $"Unsupported module permission plan kind '{plan.Kind}'.");
    }
}

/// <summary>
/// Inputs required to execute a module tool permission check.
/// </summary>
public sealed record ModuleToolPermissionExecutionRequest(
    string? ActionKey,
    Guid? ResourceId,
    Guid AgentId,
    ActionCaller Caller,
    ModuleRegistry ModuleRegistry,
    Func<string, Guid, Guid?, ActionCaller, CancellationToken,
        Task<AgentActionResult?>> EvaluateHostDelegateAsync,
    Action<string>? Trace = null);
