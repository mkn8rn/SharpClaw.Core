namespace SharpClaw.Core.Chat;

/// <summary>
/// Plans which host facts are required before expanding a chat header.
/// </summary>
public sealed class ChatHeaderExpansionPlanner
{
    /// <summary>
    /// Builds a store-neutral data loading plan for a header template.
    /// </summary>
    public ChatHeaderExpansionPlan BuildPlan(
        string template,
        Guid? userId,
        ChatHeaderExpansionOptions options)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(options);

        if (options.DisableHeaderTagExpansion)
        {
            return new ChatHeaderExpansionPlan(
                ShouldExpand: false,
                TagNames: [],
                RequiresUser: false,
                RequiresUserPermissionSet: false,
                RequiresAgentPermissionSet: false);
        }

        var tagNames = ChatHeaderTemplateEngine.ExtractTagNames(template);
        if (tagNames.Count == 0)
        {
            return new ChatHeaderExpansionPlan(
                ShouldExpand: false,
                TagNames: tagNames,
                RequiresUser: false,
                RequiresUserPermissionSet: false,
                RequiresAgentPermissionSet: false);
        }

        var requiredTags = tagNames
            .Select(static tag => tag.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var requiresUser = userId is not null
            && (requiredTags.Contains("user")
                || requiredTags.Contains("role")
                || requiredTags.Contains("bio")
                || requiredTags.Contains("grants"));
        var requiresUserPermissionSet = requiresUser
            && (requiredTags.Contains("role")
                || requiredTags.Contains("grants"));
        var requiresAgentPermissionSet = requiredTags.Contains("agent-role")
            || requiredTags.Contains("agent-grants");

        return new ChatHeaderExpansionPlan(
            ShouldExpand: true,
            TagNames: tagNames,
            RequiresUser: requiresUser,
            RequiresUserPermissionSet: requiresUserPermissionSet,
            RequiresAgentPermissionSet: requiresAgentPermissionSet);
    }
}

/// <summary>
/// Host data requirements for one chat header expansion.
/// </summary>
public sealed record ChatHeaderExpansionPlan(
    bool ShouldExpand,
    IReadOnlyList<string> TagNames,
    bool RequiresUser,
    bool RequiresUserPermissionSet,
    bool RequiresAgentPermissionSet);
