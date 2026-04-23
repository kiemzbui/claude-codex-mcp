namespace ClaudeCodexMcp.Domain;

public static class NotificationEventNames
{
    public const string JobWaitingForInput = "job_waiting_for_input";
    public const string JobCompleted = "job_completed";
    public const string JobFailed = "job_failed";
    public const string JobCancelled = "job_cancelled";
    public const string QueueItemFailed = "queue_item_failed";
    public const string QueueItemDelivered = "queue_item_delivered";
}

public static class NotificationChannels
{
    public const string PollingFallback = "polling";
    public const string ClaudeChannel = "claude_channel";
}

public static class NotificationModes
{
    public const string Disabled = "disabled";
    public const string Channel = "channel";
}

public enum NotificationDeliveryState
{
    Attempted,
    Delivered,
    Failed
}

public sealed record NotificationRecord
{
    public DateTimeOffset CreatedAt { get; init; }

    public string JobId { get; init; } = string.Empty;

    public string EventName { get; init; } = string.Empty;

    public NotificationDeliveryState DeliveryState { get; init; }

    public string Channel { get; init; } = "polling";

    public string PayloadSummary { get; init; } = string.Empty;

    public string? PayloadJson { get; init; }

    public string? Error { get; init; }
}

public sealed record NotificationDispatchRequest
{
    public required string EventName { get; init; }

    public required CodexJobRecord Job { get; init; }

    public bool ChannelEnabled { get; init; }

    public QueueItemSummary? QueueItem { get; init; }

    public string? Message { get; init; }
}

public sealed record NotificationDispatchResult
{
    public bool Attempted { get; init; }

    public bool Delivered { get; init; }

    public bool Failed { get; init; }

    public string Channel { get; init; } = NotificationChannels.PollingFallback;

    public string? Error { get; init; }
}
