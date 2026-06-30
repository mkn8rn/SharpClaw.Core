namespace SharpClaw.Core.Tasks.Preflight;

/// <summary>
/// Runtime facts supplied by a SharpClaw host before Core evaluates task
/// requirements.
/// </summary>
public sealed record TaskPreflightRuntimeFacts(
    IReadOnlyList<TaskPreflightProviderState> Providers,
    IReadOnlyList<TaskPreflightModelState> Models,
    IReadOnlySet<string> EnabledModuleIds,
    IReadOnlySet<string> CallerPermissionFlags)
{
    public static TaskPreflightRuntimeFacts Empty { get; } = new(
        [],
        [],
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// Provider registration and credential state visible to a preflight check.
/// </summary>
public sealed record TaskPreflightProviderState(
    string ProviderKey,
    bool RequiresApiKey,
    bool IsConfigured,
    bool HasApiKey)
{
    public bool IsConfiguredWithRequiredCredentials =>
        IsConfigured && (!RequiresApiKey || HasApiKey);
}

/// <summary>
/// Model reference and capability state visible to a preflight check.
/// </summary>
public sealed record TaskPreflightModelState(
    Guid Id,
    string Name,
    string? CustomId,
    IReadOnlySet<string> CapabilityTags);
