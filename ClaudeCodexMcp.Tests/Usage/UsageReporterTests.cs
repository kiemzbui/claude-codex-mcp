using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Usage;

namespace ClaudeCodexMcp.Tests.Usage;

public sealed class UsageReporterTests
{
    [Fact]
    public void FullDataReturnsRemainingPercentsResetTimesAndEstimatedContext()
    {
        var fiveHourReset = DateTimeOffset.UtcNow.AddHours(1);
        var weeklyReset = DateTimeOffset.UtcNow.AddDays(2);
        var reporter = new UsageReporter();

        var summary = reporter.CreateSummary(new CodexBackendUsageSnapshot
        {
            TokenUsage = new CodexBackendTokenUsage
            {
                TotalTokens = 2_500,
                ContextWindowTokens = 10_000
            },
            RateLimits = new CodexBackendRateLimits
            {
                Primary = new CodexBackendRateLimitWindow
                {
                    UsedPercent = 40,
                    WindowDurationMinutes = 300,
                    ResetsAt = fiveHourReset
                },
                Secondary = new CodexBackendRateLimitWindow
                {
                    UsedPercent = 20,
                    WindowDurationMinutes = 10_080,
                    ResetsAt = weeklyReset
                }
            }
        });

        Assert.Equal(75, summary.ContextRemainingPercentEstimate);
        Assert.True(summary.ContextRemaining.Estimate);
        Assert.Equal("75% estimate", summary.ContextRemaining.Display);
        Assert.Equal(80, summary.WeeklyUsageRemainingPercent);
        Assert.Equal(60, summary.FiveHourUsageRemainingPercent);
        Assert.Equal(weeklyReset, summary.WeeklyUsage.ResetsAt);
        Assert.Equal(fiveHourReset, summary.FiveHourUsage.ResetsAt);
        Assert.Equal("[codex status: context 75% estimate | weekly 80% | 5h 60%]", summary.Statusline);
    }

    [Fact]
    public void PartialDataKeepsUnavailableFieldsAsQuestionMarks()
    {
        var reporter = new UsageReporter();

        var summary = reporter.CreateSummary(new CodexBackendUsageSnapshot
        {
            TokenUsage = new CodexBackendTokenUsage
            {
                TotalTokens = 1_000,
                ContextWindowTokens = 2_000
            }
        });

        Assert.Equal(50, summary.ContextRemainingPercentEstimate);
        Assert.Null(summary.WeeklyUsageRemainingPercent);
        Assert.Null(summary.FiveHourUsageRemainingPercent);
        Assert.Equal("?", summary.WeeklyUsage.Display);
        Assert.Equal("?", summary.FiveHourUsage.Display);
        Assert.Equal("[codex status: context 50% estimate | weekly ? | 5h ?]", summary.Statusline);
    }

    [Fact]
    public void UnavailableDataRendersStableQuestionMarkStatusline()
    {
        var reporter = new UsageReporter();

        var summary = reporter.CreateSummary(new CodexBackendUsageSnapshot());

        Assert.Null(summary.ContextRemainingPercentEstimate);
        Assert.Null(summary.WeeklyUsageRemainingPercent);
        Assert.Null(summary.FiveHourUsageRemainingPercent);
        Assert.Equal("?", summary.ContextRemaining.Display);
        Assert.Equal(UsageReporter.UnknownStatusline, summary.Statusline);
        Assert.Equal("[codex status: context ? | weekly ? | 5h ?]", summary.Statusline);
    }

    [Fact]
    public void RemainingPercentSemanticsUseOneHundredMinusUsedPercentWithClamping()
    {
        var reporter = new UsageReporter();

        var summary = reporter.CreateSummary(new CodexBackendUsageSnapshot
        {
            RateLimits = new CodexBackendRateLimits
            {
                Primary = new CodexBackendRateLimitWindow
                {
                    UsedPercent = 125,
                    WindowDurationMinutes = 300
                },
                Secondary = new CodexBackendRateLimitWindow
                {
                    UsedPercent = -10,
                    WindowDurationMinutes = 10_080
                }
            }
        });

        Assert.Equal(0, summary.FiveHourUsageRemainingPercent);
        Assert.Equal(100, summary.WeeklyUsageRemainingPercent);
        Assert.Equal("[codex status: context ? | weekly 100% | 5h 0%]", summary.Statusline);
    }

    [Fact]
    public void UnknownWindowDurationsAreNotMappedToWeeklyOrFiveHourFields()
    {
        var reporter = new UsageReporter();

        var summary = reporter.CreateSummary(new CodexBackendUsageSnapshot
        {
            RateLimits = new CodexBackendRateLimits
            {
                Primary = new CodexBackendRateLimitWindow { UsedPercent = 40 },
                Secondary = new CodexBackendRateLimitWindow { UsedPercent = 20 }
            }
        });

        Assert.Null(summary.WeeklyUsageRemainingPercent);
        Assert.Null(summary.FiveHourUsageRemainingPercent);
        Assert.Equal("[codex status: context ? | weekly ? | 5h ?]", summary.Statusline);
    }
}
