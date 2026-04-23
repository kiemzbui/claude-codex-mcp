using ClaudeCodexMcp.Domain;

namespace ClaudeCodexMcp.Storage;

public sealed record JobDefaultProjection
{
    public string JobId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public JobState Status { get; init; }

    public string Profile { get; init; } = string.Empty;

    public string Workflow { get; init; } = string.Empty;

    public string Repo { get; init; } = string.Empty;

    public string PromptSummary { get; init; } = string.Empty;

    public string? ResultSummary { get; init; }

    public string? LastError { get; init; }

    public JobQueueSummary InputQueue { get; init; } = new();

    public string LogPath { get; init; } = string.Empty;

    public string NotificationLogPath { get; init; } = string.Empty;
}

public static class JobProjection
{
    public static JobDefaultProjection ToDefault(CodexJobRecord job) => new()
    {
        JobId = job.JobId,
        Title = ProjectionSanitizer.ToSummary(job.Title, 160),
        Status = job.Status,
        Profile = job.Profile,
        Workflow = job.Workflow,
        Repo = job.Repo,
        PromptSummary = ProjectionSanitizer.ToSummary(job.PromptSummary),
        ResultSummary = ProjectionSanitizer.ToOptionalSummary(job.ResultSummary),
        LastError = ProjectionSanitizer.ToOptionalSummary(job.LastError),
        InputQueue = job.InputQueue,
        LogPath = job.LogPath,
        NotificationLogPath = job.NotificationLogPath
    };
}
