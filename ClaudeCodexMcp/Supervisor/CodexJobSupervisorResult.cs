namespace ClaudeCodexMcp.Supervisor;

public sealed record CodexJobSupervisorResult
{
    public int ActiveJobsScanned { get; init; }

    public int JobsUpdated { get; init; }

    public int JobsFailed { get; init; }
}
