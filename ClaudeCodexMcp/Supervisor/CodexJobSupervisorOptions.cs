namespace ClaudeCodexMcp.Supervisor;

public sealed record CodexJobSupervisorOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxTransientFailures { get; init; } = 3;
}
