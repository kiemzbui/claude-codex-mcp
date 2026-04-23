using System.Globalization;
using ClaudeCodexMcp.Domain;

namespace ClaudeCodexMcp.Usage;

public sealed class UsageReporter
{
    public const string UnknownStatusline = "[codex status: context ? | weekly ? | 5h ?]";

    public CodexUsageSummary CreateSummary(CodexBackendUsageSnapshot? snapshot)
    {
        var context = CreateContextReport(snapshot?.TokenUsage);
        var windows = CreateWindowCandidates(snapshot?.RateLimits).ToArray();
        var fiveHour = SelectFiveHourWindow(windows);
        var weekly = SelectWeeklyWindow(windows, fiveHour);

        var summary = new CodexUsageSummary
        {
            ContextRemaining = context,
            FiveHourUsage = fiveHour?.Report ?? new UsagePercentReport(),
            WeeklyUsage = weekly?.Report ?? new UsagePercentReport(),
            ContextRemainingPercentEstimate = context.RemainingPercentEstimate,
            FiveHourUsageRemainingPercent = fiveHour?.Report.RemainingPercent,
            WeeklyUsageRemainingPercent = weekly?.Report.RemainingPercent
        };

        return summary with
        {
            Statusline = CreateStatusline(summary)
        };
    }

    public string CreateStatusline(CodexBackendUsageSnapshot? snapshot) =>
        CreateSummary(snapshot).Statusline;

    private static ContextRemainingReport CreateContextReport(CodexBackendTokenUsage? tokenUsage)
    {
        if (tokenUsage?.TotalTokens is not { } totalTokens ||
            tokenUsage.ContextWindowTokens is not { } contextWindowTokens ||
            contextWindowTokens <= 0)
        {
            return new ContextRemainingReport();
        }

        var remaining = ClampPercent(100d - (totalTokens * 100d / contextWindowTokens));
        return new ContextRemainingReport
        {
            Available = true,
            Display = $"{FormatPercent(remaining)} estimate",
            Estimate = true,
            RemainingPercentEstimate = remaining,
            TotalTokens = totalTokens,
            ContextWindowTokens = contextWindowTokens
        };
    }

    private static IEnumerable<WindowCandidate> CreateWindowCandidates(CodexBackendRateLimits? rateLimits)
    {
        if (rateLimits?.Primary is not null)
        {
            yield return CreateWindowCandidate("primary", rateLimits.Primary);
        }

        if (rateLimits?.Secondary is not null)
        {
            yield return CreateWindowCandidate("secondary", rateLimits.Secondary);
        }
    }

    private static WindowCandidate CreateWindowCandidate(string source, CodexBackendRateLimitWindow window)
    {
        if (window.UsedPercent is not { } usedPercent)
        {
            return new WindowCandidate(source, window.WindowDurationMinutes, new UsagePercentReport
            {
                WindowDurationMinutes = window.WindowDurationMinutes,
                ResetsAt = window.ResetsAt,
                Source = source
            });
        }

        var remaining = ClampPercent(100d - usedPercent);
        return new WindowCandidate(source, window.WindowDurationMinutes, new UsagePercentReport
        {
            Available = true,
            Display = FormatPercent(remaining),
            UsedPercent = ClampPercent(usedPercent),
            RemainingPercent = remaining,
            ResetsAt = window.ResetsAt,
            WindowDurationMinutes = window.WindowDurationMinutes,
            Source = source
        });
    }

    private static WindowCandidate? SelectFiveHourWindow(IReadOnlyList<WindowCandidate> windows)
    {
        var fiveHour = windows
            .Where(window => window.DurationMinutes is >= 240 and <= 360)
            .OrderBy(window => Math.Abs(window.DurationMinutes!.Value - 300))
            .FirstOrDefault();

        if (fiveHour is not null)
        {
            return fiveHour;
        }

        return windows.Count(window => window.DurationMinutes is not null) >= 2
            ? windows
                .Where(window => window.DurationMinutes is not null)
                .OrderBy(window => window.DurationMinutes)
                .FirstOrDefault()
            : null;
    }

    private static WindowCandidate? SelectWeeklyWindow(
        IReadOnlyList<WindowCandidate> windows,
        WindowCandidate? fiveHour)
    {
        var weekly = windows
            .Where(window => window.DurationMinutes is >= 8640 and <= 11520)
            .OrderBy(window => Math.Abs(window.DurationMinutes!.Value - 10080))
            .FirstOrDefault();

        if (weekly is not null)
        {
            return weekly;
        }

        return windows.Count(window => window.DurationMinutes is not null) >= 2
            ? windows
                .Where(window => window.DurationMinutes is not null && !ReferenceEquals(window, fiveHour))
                .OrderByDescending(window => window.DurationMinutes)
                .FirstOrDefault()
            : null;
    }

    private static string CreateStatusline(CodexUsageSummary summary) =>
        $"[codex status: context {summary.ContextRemaining.Display} | weekly {summary.WeeklyUsage.Display} | 5h {summary.FiveHourUsage.Display}]";

    private static double ClampPercent(double value) =>
        Math.Min(100d, Math.Max(0d, value));

    private static string FormatPercent(double percent) =>
        Math.Round(percent, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    private sealed record WindowCandidate(
        string Source,
        int? DurationMinutes,
        UsagePercentReport Report);
}
