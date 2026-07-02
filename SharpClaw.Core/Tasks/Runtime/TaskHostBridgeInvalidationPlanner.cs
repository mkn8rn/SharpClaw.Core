using SharpClaw.Core.Chat;

namespace SharpClaw.Core.Tasks.Runtime;

/// <summary>
/// Store-neutral invalidation policy for task host bridge mutations.
/// </summary>
public sealed class TaskHostBridgeInvalidationPlanner
{
    public ChatRuntimeInvalidationPlan BuildPlan(
        TaskHostBridgeInvalidationTarget target,
        Guid? entityId = null)
    {
        return target switch
        {
            TaskHostBridgeInvalidationTarget.Agent => AgentRuntimeStateChanged(),
            TaskHostBridgeInvalidationTarget.Channel => ChannelRuntimeStateChanged(),
            TaskHostBridgeInvalidationTarget.Thread => ThreadRuntimeStateChanged(entityId),
            TaskHostBridgeInvalidationTarget.Permission => PermissionRuntimeStateChanged(),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
        };
    }

    private static ChatRuntimeInvalidationPlan AgentRuntimeStateChanged() =>
        new(
        [
            ChatCacheInvalidation.Prefix(ChatCache.PrefixHeaderAgentSuffix),
            ChatCacheInvalidation.Prefix(ChatCache.PrefixEffectiveTools),
        ]);

    private static ChatRuntimeInvalidationPlan ChannelRuntimeStateChanged() =>
        new(
        [
            ChatCacheInvalidation.Prefix(ChatCache.PrefixHeaderAgentSuffix),
            ChatCacheInvalidation.Prefix(ChatCache.PrefixEffectiveTools),
        ]);

    private static ChatRuntimeInvalidationPlan ThreadRuntimeStateChanged(
        Guid? threadId)
    {
        var invalidations = new List<ChatCacheInvalidation>();
        if (threadId is { } id)
            invalidations.Add(ChatCacheInvalidation.Key(
                ChatCache.KeyThreadHistoryLimits(id)));

        invalidations.Add(
            ChatCacheInvalidation.Prefix(ChatCache.PrefixHeaderAgentSuffix));

        return new ChatRuntimeInvalidationPlan(invalidations);
    }

    private static ChatRuntimeInvalidationPlan PermissionRuntimeStateChanged() =>
        new(
        [
            ChatCacheInvalidation.Prefix(ChatCache.PrefixHeaderUser),
            ChatCacheInvalidation.Prefix(ChatCache.PrefixHeaderAgentSuffix),
            ChatCacheInvalidation.Prefix(ChatCache.PrefixEffectiveTools),
        ]);
}
