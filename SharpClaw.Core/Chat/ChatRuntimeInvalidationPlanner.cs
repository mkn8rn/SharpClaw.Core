namespace SharpClaw.Core.Chat;

/// <summary>
/// Builds cache invalidation plans for SharpClaw chat runtime mutations.
/// </summary>
public sealed class ChatRuntimeInvalidationPlanner
{
    /// <summary>
    /// Invalidates cached header, tool, and default-resource facts that depend
    /// on one agent.
    /// </summary>
    public ChatRuntimeInvalidationPlan AgentChanged(Guid agentId) =>
        new(
        [
            ChatCacheInvalidation.HeaderAgentSuffixesForAgent(agentId),
            ChatCacheInvalidation.EffectiveToolsForAgent(agentId),
            ChatCacheInvalidation.DefaultResourceResolutionForAgent(agentId),
        ]);

    /// <summary>
    /// Invalidates cached facts that depend on one channel's topology.
    /// </summary>
    public ChatRuntimeInvalidationPlan ChannelChanged(Guid channelId) =>
        new(
        [
            ChatCacheInvalidation.HeaderAgentSuffixesForChannel(channelId),
            ChatCacheInvalidation.DefaultResourceResolutionForChannel(channelId),
        ]);

    /// <summary>
    /// Invalidates cached facts for all channels affected by a context change.
    /// </summary>
    public ChatRuntimeInvalidationPlan ContextChanged(
        IEnumerable<Guid> channelIds) =>
        ChannelCollectionChanged(channelIds);

    /// <summary>
    /// Invalidates cached default-resource facts for one channel.
    /// </summary>
    public ChatRuntimeInvalidationPlan DefaultResourcesForChannelChanged(
        Guid channelId) =>
        new(
        [
            ChatCacheInvalidation.DefaultResourceResolutionForChannel(channelId),
        ]);

    /// <summary>
    /// Invalidates cached default-resource facts for channels inheriting from
    /// a changed context.
    /// </summary>
    public ChatRuntimeInvalidationPlan DefaultResourcesForContextChanged(
        IEnumerable<Guid> channelIds)
    {
        ArgumentNullException.ThrowIfNull(channelIds);

        return new ChatRuntimeInvalidationPlan(channelIds
            .Distinct()
            .Select(ChatCacheInvalidation.DefaultResourceResolutionForChannel)
            .ToList());
    }

    /// <summary>
    /// Invalidates the thread history-limit cache for one thread.
    /// </summary>
    public ChatRuntimeInvalidationPlan ThreadChanged(Guid threadId) =>
        new(
        [
            ChatCacheInvalidation.Key(ChatCache.KeyThreadHistoryLimits(threadId)),
        ]);

    /// <summary>
    /// Invalidates every effective-tool cache entry.
    /// </summary>
    public ChatRuntimeInvalidationPlan ToolAwarenessSetsChanged() =>
        new(
        [
            ChatCacheInvalidation.Prefix(ChatCache.PrefixEffectiveTools),
        ]);

    /// <summary>
    /// Invalidates all header and permission-derived runtime facts.
    /// </summary>
    public ChatRuntimeInvalidationPlan PermissionSetsChanged() =>
        new(
        [
            ChatCacheInvalidation.Prefix(ChatCache.PrefixHeaderUser),
            ChatCacheInvalidation.Prefix(ChatCache.PrefixHeaderAgentSuffix),
            ChatCacheInvalidation.Prefix(ChatCache.PrefixDefaultResourceResolution),
        ]);

    /// <summary>
    /// Invalidates cached header facts for one user.
    /// </summary>
    public ChatRuntimeInvalidationPlan UserHeaderChanged(Guid userId) =>
        new(
        [
            ChatCacheInvalidation.Key(ChatCache.KeyHeaderUser(userId)),
        ]);

    private static ChatRuntimeInvalidationPlan ChannelCollectionChanged(
        IEnumerable<Guid> channelIds)
    {
        ArgumentNullException.ThrowIfNull(channelIds);

        var invalidations = channelIds
            .Distinct()
            .SelectMany(static channelId => new[]
            {
                ChatCacheInvalidation.HeaderAgentSuffixesForChannel(channelId),
                ChatCacheInvalidation.DefaultResourceResolutionForChannel(channelId),
            })
            .ToList();

        return new ChatRuntimeInvalidationPlan(invalidations);
    }
}

/// <summary>
/// A host-applicable set of chat cache invalidations.
/// </summary>
public sealed record ChatRuntimeInvalidationPlan(
    IReadOnlyList<ChatCacheInvalidation> Invalidations)
{
    /// <summary>
    /// Applies the invalidation plan to the supplied cache.
    /// </summary>
    public void ApplyTo(ChatCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        foreach (var invalidation in Invalidations)
            invalidation.ApplyTo(cache);
    }
}

/// <summary>
/// One cache invalidation operation.
/// </summary>
public sealed record ChatCacheInvalidation(
    ChatCacheInvalidationKind Kind,
    string? Value = null,
    Guid? Id = null)
{
    /// <summary>
    /// Creates an invalidation for one exact cache key.
    /// </summary>
    public static ChatCacheInvalidation Key(string key) =>
        new(ChatCacheInvalidationKind.Key, key);

    /// <summary>
    /// Creates an invalidation for every cache key with a prefix.
    /// </summary>
    public static ChatCacheInvalidation Prefix(string prefix) =>
        new(ChatCacheInvalidationKind.Prefix, prefix);

    /// <summary>
    /// Creates an invalidation for cached header suffixes for one agent.
    /// </summary>
    public static ChatCacheInvalidation HeaderAgentSuffixesForAgent(
        Guid agentId) =>
        new(ChatCacheInvalidationKind.HeaderAgentSuffixesForAgent, Id: agentId);

    /// <summary>
    /// Creates an invalidation for cached header suffixes for one channel.
    /// </summary>
    public static ChatCacheInvalidation HeaderAgentSuffixesForChannel(
        Guid channelId) =>
        new(ChatCacheInvalidationKind.HeaderAgentSuffixesForChannel, Id: channelId);

    /// <summary>
    /// Creates an invalidation for effective-tool caches for one agent.
    /// </summary>
    public static ChatCacheInvalidation EffectiveToolsForAgent(Guid agentId) =>
        new(ChatCacheInvalidationKind.EffectiveToolsForAgent, Id: agentId);

    /// <summary>
    /// Creates an invalidation for default-resource resolution for one channel.
    /// </summary>
    public static ChatCacheInvalidation DefaultResourceResolutionForChannel(
        Guid channelId) =>
        new(ChatCacheInvalidationKind.DefaultResourceResolutionForChannel, Id: channelId);

    /// <summary>
    /// Creates an invalidation for default-resource resolution for one agent.
    /// </summary>
    public static ChatCacheInvalidation DefaultResourceResolutionForAgent(
        Guid agentId) =>
        new(ChatCacheInvalidationKind.DefaultResourceResolutionForAgent, Id: agentId);

    /// <summary>
    /// Applies this operation to a chat cache.
    /// </summary>
    public void ApplyTo(ChatCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        switch (Kind)
        {
            case ChatCacheInvalidationKind.Key:
                cache.Remove(Value ?? throw MissingValue());
                break;
            case ChatCacheInvalidationKind.Prefix:
                cache.RemoveByPrefix(Value ?? throw MissingValue());
                break;
            case ChatCacheInvalidationKind.HeaderAgentSuffixesForAgent:
                cache.RemoveHeaderAgentSuffixesForAgent(Id ?? throw MissingId());
                break;
            case ChatCacheInvalidationKind.HeaderAgentSuffixesForChannel:
                cache.RemoveHeaderAgentSuffixesForChannel(Id ?? throw MissingId());
                break;
            case ChatCacheInvalidationKind.EffectiveToolsForAgent:
                cache.RemoveEffectiveToolsForAgent(Id ?? throw MissingId());
                break;
            case ChatCacheInvalidationKind.DefaultResourceResolutionForChannel:
                cache.RemoveDefaultResourceResolutionForChannel(Id ?? throw MissingId());
                break;
            case ChatCacheInvalidationKind.DefaultResourceResolutionForAgent:
                cache.RemoveDefaultResourceResolutionForAgent(Id ?? throw MissingId());
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown cache invalidation kind '{Kind}'.");
        }
    }

    private static InvalidOperationException MissingValue() =>
        new("Cache invalidation value is required.");

    private static InvalidOperationException MissingId() =>
        new("Cache invalidation id is required.");
}

/// <summary>
/// Supported chat cache invalidation operation kinds.
/// </summary>
public enum ChatCacheInvalidationKind
{
    /// <summary>
    /// Remove one exact cache key.
    /// </summary>
    Key,

    /// <summary>
    /// Remove every cache key with a prefix.
    /// </summary>
    Prefix,

    /// <summary>
    /// Remove cached header suffixes for one agent.
    /// </summary>
    HeaderAgentSuffixesForAgent,

    /// <summary>
    /// Remove cached header suffixes for one channel.
    /// </summary>
    HeaderAgentSuffixesForChannel,

    /// <summary>
    /// Remove effective-tool caches for one agent.
    /// </summary>
    EffectiveToolsForAgent,

    /// <summary>
    /// Remove default-resource resolution caches for one channel.
    /// </summary>
    DefaultResourceResolutionForChannel,

    /// <summary>
    /// Remove default-resource resolution caches for one agent.
    /// </summary>
    DefaultResourceResolutionForAgent,
}
