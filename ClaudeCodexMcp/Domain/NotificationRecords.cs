namespace ClaudeCodexMcp.Domain;

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

    public string? Error { get; init; }
}
