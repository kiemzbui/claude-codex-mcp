using System.Collections.Generic;

namespace ClaudeCodexMcp.Workflows;

public static class CodexServiceTiers
{
    public const string Fast = "fast";
    public const string Normal = "normal";
    public const string Flex = "flex";

    private static readonly Dictionary<string, string> CanonicalByName = new(StringComparer.OrdinalIgnoreCase)
    {
        [Fast] = Fast,
        [Normal] = Normal,
        [Flex] = Flex
    };

    public static bool TryNormalize(string? serviceTier, out string normalized)
    {
        if (!string.IsNullOrWhiteSpace(serviceTier)
            && CanonicalByName.TryGetValue(serviceTier.Trim(), out var canonical))
        {
            normalized = canonical;
            return true;
        }

        normalized = string.Empty;
        return false;
    }
}

