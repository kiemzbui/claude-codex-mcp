namespace ClaudeCodexMcp.Domain;

public sealed record UsagePercentReport
{
    public bool Available { get; init; }

    public string Display { get; init; } = "?";

    public double? UsedPercent { get; init; }

    public double? RemainingPercent { get; init; }

    public DateTimeOffset? ResetsAt { get; init; }

    public int? WindowDurationMinutes { get; init; }

    public string? Source { get; init; }
}

public sealed record ContextRemainingReport
{
    public bool Available { get; init; }

    public string Display { get; init; } = "?";

    public bool Estimate { get; init; }

    public double? RemainingPercentEstimate { get; init; }

    public int? TotalTokens { get; init; }

    public int? ContextWindowTokens { get; init; }
}

public sealed record CodexUsageSummary
{
    public ContextRemainingReport ContextRemaining { get; init; } = new();

    public UsagePercentReport WeeklyUsage { get; init; } = new();

    public UsagePercentReport FiveHourUsage { get; init; } = new();

    public double? ContextRemainingPercentEstimate { get; init; }

    public double? WeeklyUsageRemainingPercent { get; init; }

    public double? FiveHourUsageRemainingPercent { get; init; }

    public string Statusline { get; init; } = "[codex status: context ? | weekly ? | 5h ?]";
}

public sealed record CodexUsageResponse
{
    public string? JobId { get; init; }

    public CodexUsageSummary Usage { get; init; } = new();

    public double? ContextRemainingPercentEstimate { get; init; }

    public double? WeeklyUsageRemainingPercent { get; init; }

    public double? FiveHourUsageRemainingPercent { get; init; }

    public string Statusline { get; init; } = "[codex status: context ? | weekly ? | 5h ?]";

    public CodexJobCompactResponse? Job { get; init; }

    public IReadOnlyList<ToolError> Errors { get; init; } = [];
}
