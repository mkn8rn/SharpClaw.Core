using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Providers;

/// <summary>
/// Store-neutral provider catalog rules used by SharpClaw runtimes.
/// </summary>
public sealed class ProviderCatalogEngine
{
    /// <summary>Projects provider plugins into the public provider-type DTO.</summary>
    public IReadOnlyList<ProviderTypeResponse> ListAvailableTypes(
        IEnumerable<IProviderPlugin> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);

        return plugins
            .OrderBy(plugin => plugin.DisplayName)
            .Select(plugin => new ProviderTypeResponse(
                plugin.ProviderKey,
                plugin.DisplayName,
                plugin.RequiresEndpoint,
                plugin.SupportsAutomaticEndpointDiscovery,
                plugin.RequiresApiKey,
                plugin.DeviceCodeFlow is not null))
            .ToList();
    }

    /// <summary>Builds the host persistence plan for a new provider.</summary>
    public ProviderCreatePlan PlanCreate(
        CreateProviderRequest request,
        IProviderPlugin? plugin,
        bool enforceUniqueNames,
        IEnumerable<string> existingProviderNames)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(existingProviderNames);

        if (plugin is null)
            throw new ProviderUnavailableException(request.ProviderKey);

        EnsureEndpointAllowed(request.ProviderKey, request.ApiEndpoint, plugin);

        if (enforceUniqueNames)
            EnsureProviderNameUnique(request.Name, existingProviderNames);

        return new ProviderCreatePlan(
            request.Name,
            request.ProviderKey,
            ShouldStoreEndpoint(plugin) ? request.ApiEndpoint : null,
            request.ApiKey);
    }

    /// <summary>Builds the host persistence plan for an existing provider update.</summary>
    public ProviderUpdatePlan PlanUpdate(
        string currentName,
        UpdateProviderRequest request,
        bool enforceUniqueNames,
        IEnumerable<string> existingProviderNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentName);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(existingProviderNames);

        var nextName = currentName;
        if (request.Name is not null)
        {
            if (enforceUniqueNames
                && !request.Name.Trim().Equals(
                    currentName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                EnsureProviderNameUnique(request.Name, existingProviderNames);
            }

            nextName = request.Name;
        }

        return new ProviderUpdatePlan(
            nextName,
            request.ApiEndpoint is not null,
            request.ApiEndpoint);
    }

    /// <summary>Throws when a provider cannot sync models in its current state.</summary>
    public void EnsureCanSyncModels(
        string providerKey,
        bool hasApiKey,
        IProviderPlugin? plugin)
    {
        if (plugin is null)
            throw new ProviderUnavailableException(providerKey);

        if (plugin.RequiresApiKey && !hasApiKey)
            throw new InvalidOperationException(
                "Provider does not have an API key configured.");
    }

    /// <summary>Returns whether unique-name enforcement should be active.</summary>
    public static bool IsUniqueNameEnforced(string? configurationValue)
    {
        return configurationValue is null
            || !bool.TryParse(configurationValue, out var enforced)
            || enforced;
    }

    /// <summary>Returns whether the provider endpoint should be persisted.</summary>
    public static bool ShouldStoreEndpoint(IProviderPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        return plugin.RequiresEndpoint || plugin.SupportsAutomaticEndpointDiscovery;
    }

    private static void EnsureEndpointAllowed(
        string providerKey,
        string? apiEndpoint,
        IProviderPlugin plugin)
    {
        if (plugin.RequiresEndpoint
            && !plugin.SupportsAutomaticEndpointDiscovery
            && string.IsNullOrWhiteSpace(apiEndpoint))
        {
            throw new ArgumentException(
                $"ApiEndpoint is required for provider '{providerKey}'.");
        }
    }

    private static void EnsureProviderNameUnique(
        string name,
        IEnumerable<string> existingProviderNames)
    {
        var normalized = name.Trim();
        if (existingProviderNames.Any(existing =>
                existing.Trim().Equals(
                    normalized,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"A provider named '{name}' already exists.");
        }
    }
}

/// <summary>Provider creation values the host should persist.</summary>
public sealed record ProviderCreatePlan(
    string Name,
    string ProviderKey,
    string? ApiEndpointToStore,
    string? ApiKey);

/// <summary>Provider update values the host should persist.</summary>
public sealed record ProviderUpdatePlan(
    string Name,
    bool UpdateApiEndpoint,
    string? ApiEndpoint);
