namespace SharpClaw.Core.Modules;

/// <summary>
/// Lightweight chat-message descriptor exposed through host context
/// capabilities without coupling modules to host persistence entities.
/// </summary>
public sealed record HostContextChatMessageSummary(
    string Role,
    string Content,
    string Sender,
    DateTimeOffset Timestamp);
