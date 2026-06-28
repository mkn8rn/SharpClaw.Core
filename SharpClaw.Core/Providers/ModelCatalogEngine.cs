namespace SharpClaw.Core.Providers;

/// <summary>
/// Store-neutral model catalog rules used by SharpClaw runtimes.
/// </summary>
public sealed class ModelCatalogEngine
{
    /// <summary>Returns whether unique-name enforcement should be active.</summary>
    public static bool IsUniqueNameEnforced(string? configurationValue)
    {
        return configurationValue is null
            || !bool.TryParse(configurationValue, out var enforced)
            || enforced;
    }

    /// <summary>Throws when a model name is already present.</summary>
    public void EnsureModelNameAvailable(string name, bool exists)
    {
        if (exists)
            throw new InvalidOperationException($"A model named '{name}' already exists.");
    }

    /// <summary>Serializes model capability tags into the persisted shape.</summary>
    public string? SerializeCapabilityTags(IReadOnlyCollection<string>? capabilityTags)
    {
        return capabilityTags is { Count: > 0 }
            ? string.Join(',', capabilityTags)
            : null;
    }
}
