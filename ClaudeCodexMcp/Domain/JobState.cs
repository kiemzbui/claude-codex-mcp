namespace ClaudeCodexMcp.Domain;

public enum JobState
{
    Queued,
    Running,
    WaitingForInput,
    Completed,
    Failed,
    Cancelled
}
