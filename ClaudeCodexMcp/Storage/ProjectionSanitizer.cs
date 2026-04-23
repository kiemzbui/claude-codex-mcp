using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeCodexMcp.Storage;

public static partial class ProjectionSanitizer
{
    private const int DefaultSummaryByteLimit = 512;

    public static string ToSummary(string? value, int maxUtf8Bytes = DefaultSummaryByteLimit)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var collapsed = WhitespacePattern().Replace(value.Trim(), " ");
        var redacted = SecretPattern().Replace(collapsed, "$1=[redacted]");
        return TruncateUtf8(redacted, maxUtf8Bytes);
    }

    public static string? ToOptionalSummary(string? value, int maxUtf8Bytes = DefaultSummaryByteLimit)
    {
        var summary = ToSummary(value, maxUtf8Bytes);
        return summary.Length == 0 ? null : summary;
    }

    public static string TruncateUtf8(string value, int maxUtf8Bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxUtf8Bytes);

        if (Encoding.UTF8.GetByteCount(value) <= maxUtf8Bytes)
        {
            return value;
        }

        const string marker = "... [truncated]";
        var markerBytes = Encoding.UTF8.GetByteCount(marker);
        if (markerBytes >= maxUtf8Bytes)
        {
            return Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(marker).AsSpan(0, maxUtf8Bytes));
        }

        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            var candidate = builder.ToString() + rune + marker;
            if (Encoding.UTF8.GetByteCount(candidate) > maxUtf8Bytes)
            {
                break;
            }

            builder.Append(rune);
        }

        return builder.Append(marker).ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|token|secret|api[_-]?key|authorization)\s*=\s*[^,\s;]+")]
    private static partial Regex SecretPattern();
}
