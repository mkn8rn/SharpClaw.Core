using System.Security.Cryptography;
using System.Text;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral chat tool selection rules used by SharpClaw runtimes.
/// </summary>
public sealed class ChatToolSelectionEngine
{
    /// <summary>
    /// Builds a stable cache fingerprint for a tool-awareness map.
    /// </summary>
    public string BuildAwarenessFingerprint(
        IReadOnlyDictionary<string, bool>? toolAwareness)
    {
        if (toolAwareness is null || toolAwareness.Count == 0)
            return "all";

        var sb = new StringBuilder();
        foreach (var (key, enabled) in toolAwareness.OrderBy(
                     static kvp => kvp.Key,
                     StringComparer.Ordinal))
        {
            sb.Append(key).Append('=').Append(enabled ? '1' : '0').Append(';');
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// Applies tool-awareness filtering to the supplied provider tool set.
    /// </summary>
    public IReadOnlyList<ChatToolDefinition> ApplyAwareness(
        IEnumerable<ChatToolDefinition> tools,
        IReadOnlyDictionary<string, bool>? toolAwareness)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var toolList = tools.ToList();
        if (toolAwareness is null || toolAwareness.Count == 0)
            return toolList;

        return toolList
            .Where(tool =>
                !toolAwareness.TryGetValue(tool.Name, out var enabled)
                || enabled)
            .ToList();
    }

    /// <summary>
    /// Estimates cache size for effective tool definition lists.
    /// </summary>
    public long EstimateToolDefinitions(
        IReadOnlyList<ChatToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        return 64 + tools.Sum(static tool =>
            64
            + ChatCache.EstimateString(tool.Name)
            + ChatCache.EstimateString(tool.Description)
            + ChatCache.EstimateString(tool.ParametersSchema.GetRawText()));
    }
}
