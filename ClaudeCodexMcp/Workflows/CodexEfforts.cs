using System.Collections.Generic;

namespace ClaudeCodexMcp.Workflows;

public static class CodexEfforts
{
    public const string None = "none";
    public const string Minimal = "minimal";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string XHigh = "xhigh";

    private static readonly Dictionary<string, string> CanonicalByName = new(StringComparer.OrdinalIgnoreCase)
    {
        [None] = None,
        [Minimal] = Minimal,
        [Low] = Low,
        [Medium] = Medium,
        [High] = High,
        [XHigh] = XHigh
    };

    public static IReadOnlyCollection<string> All => CanonicalByName.Values;

    public static bool TryNormalize(string? effort, out string normalized)
    {
        if (!string.IsNullOrWhiteSpace(effort)
            && CanonicalByName.TryGetValue(effort.Trim(), out var canonical))
        {
            normalized = canonical;
            return true;
        }

        normalized = string.Empty;
        return false;
    }
}

