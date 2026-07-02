using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Modules;

/// <summary>
/// Store-neutral state machine for module health failure accounting.
/// Hosts own timers, module invocation, logging, persistence, and disabling.
/// Core owns only the consecutive-failure and auto-disable decision rules.
/// </summary>
public sealed class ModuleHealthPolicyEngine
{
    /// <summary>
    /// Evaluates one already-collected module health observation.
    /// </summary>
    public ModuleHealthPolicyDecision Evaluate(
        ModuleHealthPolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.PreviousConsecutiveFailures < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                input.PreviousConsecutiveFailures,
                "Previous consecutive failures cannot be negative.");
        }

        var threshold = NormalizeFailureThreshold(input.FailureThreshold);
        return input.ResultKind switch
        {
            ModuleHealthProbeResultKind.Healthy => new ModuleHealthPolicyDecision(
                ConsecutiveFailureCount: 0,
                EffectiveFailureThreshold: threshold,
                IsFailure: false,
                ShouldResetFailureCount: true,
                ShouldAutoDisable: false),
            ModuleHealthProbeResultKind.Skipped => new ModuleHealthPolicyDecision(
                ConsecutiveFailureCount: input.PreviousConsecutiveFailures,
                EffectiveFailureThreshold: threshold,
                IsFailure: false,
                ShouldResetFailureCount: false,
                ShouldAutoDisable: false),
            ModuleHealthProbeResultKind.Unhealthy => EvaluateFailure(
                input.PreviousConsecutiveFailures,
                threshold),
            _ => throw new ArgumentOutOfRangeException(
                nameof(input),
                input.ResultKind,
                "Unknown module health probe result kind.")
        };
    }

    /// <summary>
    /// Evaluates a concrete module health status returned by a host probe.
    /// </summary>
    public ModuleHealthPolicyDecision EvaluateStatus(
        int previousConsecutiveFailures,
        int failureThreshold,
        ModuleHealthStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return Evaluate(new ModuleHealthPolicyInput(
            previousConsecutiveFailures,
            failureThreshold,
            status.IsHealthy
                ? ModuleHealthProbeResultKind.Healthy
                : ModuleHealthProbeResultKind.Unhealthy));
    }

    private static ModuleHealthPolicyDecision EvaluateFailure(
        int previousConsecutiveFailures,
        int threshold)
    {
        var nextCount = checked(previousConsecutiveFailures + 1);
        return new ModuleHealthPolicyDecision(
            ConsecutiveFailureCount: nextCount,
            EffectiveFailureThreshold: threshold,
            IsFailure: true,
            ShouldResetFailureCount: false,
            ShouldAutoDisable: nextCount >= threshold);
    }

    private static int NormalizeFailureThreshold(int failureThreshold) =>
        failureThreshold > 0 ? failureThreshold : 1;
}

/// <summary>
/// Already-collected module health result category supplied by the host.
/// </summary>
public enum ModuleHealthProbeResultKind
{
    Skipped = 0,
    Healthy = 1,
    Unhealthy = 2
}

/// <summary>
/// Inputs for one store-neutral module health policy evaluation.
/// </summary>
public sealed record ModuleHealthPolicyInput(
    int PreviousConsecutiveFailures,
    int FailureThreshold,
    ModuleHealthProbeResultKind ResultKind);

/// <summary>
/// Result of one module health policy evaluation.
/// </summary>
public sealed record ModuleHealthPolicyDecision(
    int ConsecutiveFailureCount,
    int EffectiveFailureThreshold,
    bool IsFailure,
    bool ShouldResetFailureCount,
    bool ShouldAutoDisable);
