using System.Text.RegularExpressions;

namespace SharpClaw.Core.Modules;

/// <summary>
/// Sanitizes module tool exception messages before a host exposes them to
/// jobs, chat models, or other user-visible surfaces.
/// </summary>
public static partial class ModuleToolExceptionSanitizer
{
    /// <summary>
    /// Builds the standard SharpClaw module tool failure message after
    /// truncating and scrubbing common sensitive fragments.
    /// </summary>
    public static string Sanitize(string moduleId, string toolName, string? message)
    {
        var safeMessage = message ?? "";
        var truncated = safeMessage.Length > 200
            ? safeMessage[..200] + "..."
            : safeMessage;

        truncated = FilePathRegex().Replace(truncated, "[path]");
        truncated = Ipv4Regex().Replace(truncated, "[ip]");
        truncated = GuidRegex().Replace(truncated, "[id]");
        truncated = ConnStringRegex().Replace(truncated, "[connection]");

        return $"Module tool '{moduleId}.{toolName}' failed: {truncated}";
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^\s\""']+|/(?:usr|home|tmp|var|etc|opt|mnt)[^\s\""']*")]
    private static partial Regex FilePathRegex();

    [GeneratedRegex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(:\d+)?\b")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"(Server|Data Source|Host|Password|User Id|Uid|Pwd)=[^;\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnStringRegex();
}
