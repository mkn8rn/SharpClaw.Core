using System.Text.Json;
using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Conversation;

/// <summary>
/// Store-neutral conversation steering rules. Hosts own target lookup,
/// persistence, and activity publication; Core owns text normalization,
/// message shape, metadata, target validation messages, and response
/// projection.
/// </summary>
public sealed class ConversationSteeringEngine
{
    public const int MaxSummaryCharacters = 8000;
    public const int MaxDetailsCharacters = 16000;
    public const int MaxMetadataCharacters = 128;
    public const int DefaultListLimit = 20;
    public const int MaxListLimit = 100;
    public const string MetadataKind = "sharpclaw.conversation_steering";
    public const string ContentPrefix = "[SharpClaw conversation steering]";

    private static readonly JsonSerializerOptions MetadataJsonOptions =
        new(JsonSerializerDefaults.Web);

    public ConversationSteeringPreparedMessage PrepareAdd(
        ConversationSteeringRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var summary = RequireText(
            request.Summary,
            nameof(request.Summary),
            MaxSummaryCharacters);
        var details = NormalizeOptionalText(
            request.Details,
            MaxDetailsCharacters);
        var metadata = new ConversationSteeringMetadata(
            MetadataKind,
            NormalizeOptionalText(request.Source, MaxMetadataCharacters),
            NormalizeOptionalText(request.Category, MaxMetadataCharacters));
        var content = FormatContent(summary, details, metadata);
        var clientType = string.IsNullOrWhiteSpace(request.ClientType)
            ? WellKnownClientKeys.Api
            : request.ClientType.Trim();

        return new ConversationSteeringPreparedMessage(
            request.ChannelId,
            request.ThreadId,
            ChatRoles.System,
            MessageOrigin.System,
            content,
            clientType,
            JsonSerializer.Serialize(metadata, MetadataJsonOptions),
            metadata.Source,
            metadata.Category);
    }

    public int ClampListLimit(int limit) => Math.Clamp(limit, 1, MaxListLimit);

    public bool IsSteeringMessage(string? role, string? content) =>
        role == ChatRoles.System
        && content?.StartsWith(ContentPrefix, StringComparison.Ordinal) == true;

    public void EnsureTargetValid(
        Guid channelId,
        Guid? threadId,
        bool channelExists,
        Guid? threadChannelId)
    {
        if (channelId == Guid.Empty)
            throw new ArgumentException("channelId is required.", nameof(channelId));

        if (threadId is null)
        {
            if (!channelExists)
                throw new InvalidOperationException(
                    $"Channel '{channelId}' was not found.");
            return;
        }

        if (threadChannelId is null)
            throw new InvalidOperationException(
                $"Thread '{threadId}' was not found.");

        if (threadChannelId != channelId)
        {
            throw new InvalidOperationException(
                $"Thread '{threadId}' belongs to channel '{threadChannelId}', not '{channelId}'.");
        }
    }

    public ConversationSteeringResponse ToResponse(
        ConversationSteeringStoredMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var metadata = ParseMetadata(message.ProviderMetadataJson);
        return new ConversationSteeringResponse(
            message.MessageId,
            message.ChannelId,
            message.ThreadId,
            message.Content,
            message.Timestamp,
            metadata?.Source,
            metadata?.Category);
    }

    public IReadOnlyList<ConversationSteeringResponse> ToListResponse(
        IEnumerable<ConversationSteeringStoredMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return messages
            .OrderBy(static message => message.Timestamp)
            .ThenBy(static message => message.MessageId)
            .Select(ToResponse)
            .ToArray();
    }

    public ConversationSteeringMetadata? ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var metadata = JsonSerializer.Deserialize<ConversationSteeringMetadata>(
                json,
                MetadataJsonOptions);
            return string.Equals(metadata?.Kind, MetadataKind, StringComparison.Ordinal)
                ? metadata
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FormatContent(
        string summary,
        string? details,
        ConversationSteeringMetadata metadata)
    {
        var parts = new List<string> { ContentPrefix };
        if (!string.IsNullOrWhiteSpace(metadata.Source))
            parts.Add($"Source: {metadata.Source}");
        if (!string.IsNullOrWhiteSpace(metadata.Category))
            parts.Add($"Category: {metadata.Category}");
        parts.Add("Summary:");
        parts.Add(summary);

        if (!string.IsNullOrWhiteSpace(details))
        {
            parts.Add("Details:");
            parts.Add(details);
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string RequireText(
        string value,
        string name,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : throw new ArgumentException(
                $"{name} must be {maxLength} characters or less.",
                name);
    }

    private static string? NormalizeOptionalText(
        string? value,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : throw new ArgumentException(
                $"Value must be {maxLength} characters or less.",
                nameof(value));
    }
}

public sealed record ConversationSteeringPreparedMessage(
    Guid ChannelId,
    Guid? ThreadId,
    string Role,
    MessageOrigin Origin,
    string Content,
    string ClientType,
    string ProviderMetadataJson,
    string? Source,
    string? Category);

public sealed record ConversationSteeringStoredMessage(
    Guid MessageId,
    Guid ChannelId,
    Guid? ThreadId,
    string Content,
    DateTimeOffset Timestamp,
    string? ProviderMetadataJson);

public sealed record ConversationSteeringMetadata(
    string Kind,
    string? Source,
    string? Category);
