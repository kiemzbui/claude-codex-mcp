using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Storage;

namespace ClaudeCodexMcp.Supervisor;

public static class CodexJobRecordUpdater
{
    public static bool IsTerminal(JobState state) =>
        state is JobState.Completed or JobState.Failed or JobState.Cancelled;

    public static CodexJobRecord ApplyStatus(CodexJobRecord job, CodexBackendStatus status)
    {
        if (IsTerminal(job.Status))
        {
            return job;
        }

        var nextState = status.WaitingForInput is not null
            ? JobState.WaitingForInput
            : status.State;

        if (IsTerminal(job.Status))
        {
            nextState = job.Status;
        }

        return job with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = nextState,
            CodexThreadId = status.BackendIds.ThreadId ?? job.CodexThreadId,
            CodexTurnId = status.BackendIds.TurnId ?? job.CodexTurnId,
            CodexSessionId = status.BackendIds.SessionId ?? job.CodexSessionId,
            WaitingForInput = nextState == JobState.WaitingForInput ? status.WaitingForInput : null,
            ResultSummary = ProjectionSanitizer.ToOptionalSummary(status.ResultSummary ?? job.ResultSummary, 2048),
            ChangedFiles = status.ChangedFiles.Count > 0 ? status.ChangedFiles : job.ChangedFiles,
            TestSummary = ProjectionSanitizer.ToOptionalSummary(status.TestSummary ?? job.TestSummary, 2048),
            UsageSnapshot = status.UsageSnapshot ?? job.UsageSnapshot,
            LastError = ProjectionSanitizer.ToOptionalSummary(status.LastError)
        };
    }

    public static CodexJobRecord ApplyOutput(CodexJobRecord job, CodexBackendOutput output)
    {
        if (job.Status != JobState.Completed)
        {
            return job;
        }

        return job with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            CodexThreadId = output.BackendIds.ThreadId ?? job.CodexThreadId,
            CodexTurnId = output.BackendIds.TurnId ?? job.CodexTurnId,
            CodexSessionId = output.BackendIds.SessionId ?? job.CodexSessionId,
            ResultSummary = ProjectionSanitizer.ToOptionalSummary(output.Summary ?? output.FinalText ?? job.ResultSummary, 2048),
            ChangedFiles = output.ChangedFiles.Count > 0 ? output.ChangedFiles : job.ChangedFiles,
            TestSummary = ProjectionSanitizer.ToOptionalSummary(output.TestSummary ?? job.TestSummary, 2048)
        };
    }

    public static CodexJobRecord ApplyUsage(CodexJobRecord job, CodexBackendUsageSnapshot usage) => job with
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        UsageSnapshot = usage
    };

    public static CodexJobRecord ApplyTransientError(CodexJobRecord job, Exception exception) => job with
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        LastError = ProjectionSanitizer.ToSummary(exception.Message)
    };

    public static CodexJobRecord ApplyUnrecoverableThreadFailure(CodexJobRecord job, string reason) => job with
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        Status = JobState.Failed,
        WaitingForInput = null,
        LastError = ProjectionSanitizer.ToSummary($"backend_thread_unrecoverable: {reason}")
    };

    public static CodexBackendIds ToBackendIds(CodexJobRecord job) => new()
    {
        ThreadId = job.CodexThreadId,
        TurnId = job.CodexTurnId,
        SessionId = job.CodexSessionId
    };
}
