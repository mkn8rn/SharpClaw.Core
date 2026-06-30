using Microsoft.Extensions.DependencyInjection;

namespace SharpClaw.Core.Modules;

/// <summary>
/// Restricted service provider exposed to module code during tool execution.
/// </summary>
public sealed class ModuleServiceScope(
    IServiceProvider inner,
    string moduleId,
    IEnumerable<Type> blockedTypes)
    : IServiceProvider, ISupportRequiredService, IKeyedServiceProvider
{
    private readonly IServiceProvider _inner = inner
        ?? throw new ArgumentNullException(nameof(inner));
    private readonly string _moduleId = string.IsNullOrWhiteSpace(moduleId)
        ? throw new ArgumentException("Module id is required.", nameof(moduleId))
        : moduleId;
    private readonly HashSet<Type> _blockedTypes = blockedTypes is null
        ? throw new ArgumentNullException(nameof(blockedTypes))
        : blockedTypes.ToHashSet();

    /// <summary>
    /// Service types that this scope refuses to resolve for the module.
    /// </summary>
    public IReadOnlyCollection<Type> BlockedTypes => _blockedTypes;

    /// <inheritdoc />
    public object? GetService(Type serviceType)
    {
        ThrowIfBlocked(serviceType);
        return _inner.GetService(serviceType);
    }

    /// <inheritdoc />
    public object GetRequiredService(Type serviceType)
    {
        ThrowIfBlocked(serviceType);
        if (_inner is ISupportRequiredService required)
            return required.GetRequiredService(serviceType);

        return _inner.GetService(serviceType)
            ?? throw new InvalidOperationException(
                $"No service for type '{serviceType.FullName}' has been registered.");
    }

    /// <inheritdoc />
    public object? GetKeyedService(Type serviceType, object? serviceKey)
    {
        ThrowIfBlocked(serviceType);
        if (_inner is IKeyedServiceProvider keyed)
            return keyed.GetKeyedService(serviceType, serviceKey);

        throw new InvalidOperationException(
            "The inner service provider does not support keyed services.");
    }

    /// <inheritdoc />
    public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
    {
        ThrowIfBlocked(serviceType);
        if (_inner is IKeyedServiceProvider keyed)
            return keyed.GetRequiredKeyedService(serviceType, serviceKey);

        throw new InvalidOperationException(
            "The inner service provider does not support keyed services.");
    }

    private void ThrowIfBlocked(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (_blockedTypes.Contains(serviceType))
            throw new InvalidOperationException(
                $"Module '{_moduleId}' attempted to resolve blocked service " +
                $"'{serviceType.Name}'. Modules cannot access pipeline internals.");
    }
}
