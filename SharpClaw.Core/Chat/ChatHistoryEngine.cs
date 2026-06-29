using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral chat history window rules.
/// </summary>
public sealed class ChatHistoryEngine
{
    /// <summary>Default maximum number of prior messages sent to a provider.</summary>
    public const int DefaultMaxMessages = 50;

    /// <summary>Default maximum character budget for prior messages.</summary>
    public const int DefaultMaxCharacters = 100_000;

    /// <summary>
    /// Resolves nullable per-thread limits to Core defaults.
    /// </summary>
    public ChatHistoryLimits ResolveLimits(
        int? maxMessages,
        int? maxCharacters)
    {
        return new ChatHistoryLimits(
            maxMessages ?? DefaultMaxMessages,
            maxCharacters ?? DefaultMaxCharacters);
    }

    /// <summary>
    /// Orders persisted messages, enforces the message limit, then trims
    /// oldest messages until the character limit also fits.
    /// </summary>
    public IReadOnlyList<ChatCompletionMessage> BuildProviderHistory(
        IEnumerable<ChatHistoryMessage> messages,
        ChatHistoryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(limits);

        var ordered = messages
            .OrderBy(m => m.CreatedAt)
            .TakeLast(limits.MaxMessages)
            .Select(m => new ChatCompletionMessage(m.Role, m.Content)
            {
                ProviderMetadataJson = m.ProviderMetadataJson
            })
            .ToList();

        var totalChars = 0;
        for (var i = ordered.Count - 1; i >= 0; i--)
            totalChars += ordered[i].Content.Length;

        while (ordered.Count > 0 && totalChars > limits.MaxCharacters)
        {
            totalChars -= ordered[0].Content.Length;
            ordered.RemoveAt(0);
        }

        return ordered;
    }
}

/// <summary>
/// Effective chat history limits for one provider request.
/// </summary>
public sealed record ChatHistoryLimits(
    int MaxMessages,
    int MaxCharacters);

/// <summary>
/// Store-neutral persisted message facts used for provider history shaping.
/// </summary>
public sealed record ChatHistoryMessage(
    DateTimeOffset CreatedAt,
    string Role,
    string Content,
    string? ProviderMetadataJson = null);
