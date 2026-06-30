using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpClaw.Core.Tasks.Runtime;

/// <summary>
/// Evaluates the small expression surface used by SharpClaw task scripts at
/// runtime.
/// </summary>
public sealed class TaskExpressionEngine
{
    /// <summary>
    /// Resolves variables, simple JSON property access, quoted string literals,
    /// and concatenation in a task runtime expression.
    /// </summary>
    public string ResolveExpression(
        string? expression,
        IReadOnlyDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        if (expression is null)
            return string.Empty;

        foreach (var (name, value) in variables.OrderByDescending(kv => kv.Key.Length))
        {
            expression = expression.Replace(name, value?.ToString() ?? string.Empty);
        }

        expression = Regex.Replace(expression, @"(\w+)\.(\w+)", match =>
        {
            var varName = match.Groups[1].Value;
            var propName = match.Groups[2].Value;
            if (variables.TryGetValue(varName, out var val) && val is string json)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty(propName, out var prop))
                    {
                        return prop.ValueKind == JsonValueKind.String
                            ? prop.GetString() ?? string.Empty
                            : prop.GetRawText();
                    }
                }
                catch (JsonException)
                {
                }
            }

            return match.Value;
        });

        if (expression.Contains(" + ", StringComparison.Ordinal))
        {
            var parts = expression.Split(" + ");
            var sb = new StringBuilder(expression.Length);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                sb.Append(IsQuotedStringLiteral(trimmed)
                    ? trimmed[1..^1]
                    : trimmed);
            }

            expression = sb.ToString();
        }
        else if (IsQuotedStringLiteral(expression))
        {
            expression = expression[1..^1];
        }

        return expression;
    }

    /// <summary>
    /// Evaluates task control-flow truthiness and comparisons after resolving
    /// the expression against runtime variables.
    /// </summary>
    public bool EvaluateCondition(
        string? expression,
        IReadOnlyDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var resolved = ResolveExpression(expression, variables);

        if (string.Equals(resolved, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(resolved, "false", StringComparison.OrdinalIgnoreCase))
            return false;

        if (resolved.EndsWith("!= null", StringComparison.Ordinal))
        {
            var val = resolved[..^7].Trim();
            return !string.IsNullOrEmpty(val) && val != "null";
        }

        if (resolved.EndsWith("== null", StringComparison.Ordinal))
        {
            var val = resolved[..^7].Trim();
            return string.IsNullOrEmpty(val) || val == "null";
        }

        foreach (var op in new[] { "!=", "==", ">=", "<=", ">", "<" })
        {
            var idx = resolved.IndexOf(op, StringComparison.Ordinal);
            if (idx < 0)
                continue;

            var left = resolved[..idx].Trim();
            var right = resolved[(idx + op.Length)..].Trim();

            if (double.TryParse(left, out var lNum) &&
                double.TryParse(right, out var rNum))
            {
                return op switch
                {
                    "==" => Math.Abs(lNum - rNum) < 0.0001,
                    "!=" => Math.Abs(lNum - rNum) >= 0.0001,
                    ">" => lNum > rNum,
                    "<" => lNum < rNum,
                    ">=" => lNum >= rNum,
                    "<=" => lNum <= rNum,
                    _ => false
                };
            }

            return op switch
            {
                "==" => string.Equals(left, right, StringComparison.Ordinal),
                "!=" => !string.Equals(left, right, StringComparison.Ordinal),
                _ => false
            };
        }

        return !string.IsNullOrEmpty(resolved);
    }

    private static bool IsQuotedStringLiteral(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"';
}
