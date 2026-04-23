using System.Text.Json.Serialization;

namespace ClaudeCodexMcp.Notifications;

public static class ClaudeChannelProtocol
{
    public const string ChannelNotificationMethod = "notifications/claude/channel";
    public const int ChannelEventHardCapBytes = 4096;
}

public sealed record ClaudeChannelNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; init; } = ClaudeChannelProtocol.ChannelNotificationMethod;

    [JsonPropertyName("params")]
    public required ClaudeChannelNotificationParams Params { get; init; }
}

public sealed record ClaudeChannelNotificationParams
{
    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("meta")]
    public required ClaudeChannelNotificationMetadata Meta { get; init; }
}

public sealed record ClaudeChannelNotificationMetadata
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = "claude-codex-mcp";

    [JsonPropertyName("event")]
    public required string Event { get; init; }

    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("statusline")]
    public required string Statusline { get; init; }

    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    [JsonPropertyName("workflow")]
    public string? Workflow { get; init; }

    [JsonPropertyName("thread_id")]
    public string? ThreadId { get; init; }

    [JsonPropertyName("turn_id")]
    public string? TurnId { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("queue_item_id")]
    public string? QueueItemId { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("ts")]
    public required string Timestamp { get; init; }
}

public sealed record ClaudeChannelDeliveryResult
{
    public bool Delivered { get; init; }

    public string? Error { get; init; }

    public static ClaudeChannelDeliveryResult Success() => new() { Delivered = true };

    public static ClaudeChannelDeliveryResult Failure(string error) => new()
    {
        Delivered = false,
        Error = error
    };
}
