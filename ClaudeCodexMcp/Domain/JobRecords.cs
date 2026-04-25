using System.Collections.Generic;

namespace ClaudeCodexMcp.Domain;

public sealed record WaitingForInputRecord
{
    public string? RequestId { get; init; }

    public string? Prompt { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}

public sealed record JobQueueSummary
{
    public int PendingCount { get; init; }

    public int DeliveredCount { get; init; }

    public int FailedCount { get; init; }

    public int CancelledCount { get; init; }

    public string? NextQueueItemId { get; init; }

    public string QueuePath { get; init; } = string.Empty;

    public IReadOnlyList<QueueItemSummary> Items { get; init; } = [];
}

public sealed record QueueItemSummary
{
    public string QueueItemId { get; init; } = string.Empty;

    public string JobId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public QueueItemState Status { get; init; }

    public string? Title { get; init; }

    public string PromptSummary { get; init; } = string.Empty;

    public string PromptRef { get; init; } = string.Empty;

    public int DeliveryAttemptCount { get; init; }

    public DateTimeOffset? DeliveredAt { get; init; }

    public DateTimeOffset? CancelledAt { get; init; }

    public string? LastError { get; init; }
}

public sealed record CodexJobRecord
{
    public string JobId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string Profile { get; init; } = string.Empty;

    public string Workflow { get; init; } = string.Empty;

    public string Repo { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public JobState Status { get; init; }

    public string PromptSummary { get; init; } = string.Empty;

    public string? CodexThreadId { get; init; }

    public string? CodexTurnId { get; init; }

    public string? CodexSessionId { get; init; }

    public string? WakeSessionId { get; init; }

    public string? Model { get; init; }

    public string? Effort { get; init; }

    public bool FastMode { get; init; }

    public string? ServiceTier { get; init; }

    public WaitingForInputRecord? WaitingForInput { get; init; }

    public string? ResultSummary { get; init; }

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public string? TestSummary { get; init; }

    public CodexBackendUsageSnapshot? UsageSnapshot { get; init; }

    public string? LastError { get; init; }

    public string LogPath { get; init; } = string.Empty;

    public JobQueueSummary InputQueue { get; init; } = new();

    public string NotificationMode { get; init; } = "disabled";

    public string NotificationLogPath { get; init; } = string.Empty;
}

public sealed record JobIndexRecord
{
    public DateTimeOffset RebuiltAt { get; init; }

    public IReadOnlyList<JobIndexEntry> Jobs { get; init; } = [];
}

public sealed record JobIndexEntry
{
    public string JobId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string Profile { get; init; } = string.Empty;

    public string Workflow { get; init; } = string.Empty;

    public string Repo { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public JobState Status { get; init; }

    public string? CodexThreadId { get; init; }

    public string? CodexTurnId { get; init; }

    public string? CodexSessionId { get; init; }

    public string? WakeSessionId { get; init; }

    public string? ResultSummary { get; init; }

    public string? LastError { get; init; }

    public JobQueueSummary InputQueue { get; init; } = new();

    public string JobPath { get; init; } = string.Empty;
}
