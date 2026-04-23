using System.Collections.Generic;

namespace ClaudeCodexMcp.Domain;

public sealed record QueueRecord
{
    public string JobId { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; }

    public IReadOnlyList<QueueItemRecord> Items { get; init; } = [];
}

public sealed record QueueItemRecord
{
    public string QueueItemId { get; init; } = string.Empty;

    public string JobId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public QueueItemState Status { get; init; }

    public string? Title { get; init; }

    public string Prompt { get; init; } = string.Empty;

    public string PromptSummary { get; init; } = string.Empty;

    public string PromptRef { get; init; } = string.Empty;

    public int DeliveryAttemptCount { get; init; }

    public DateTimeOffset? DeliveredAt { get; init; }

    public DateTimeOffset? CancelledAt { get; init; }

    public string? LastError { get; init; }
}
