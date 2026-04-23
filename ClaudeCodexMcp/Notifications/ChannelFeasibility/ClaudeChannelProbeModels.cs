using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ClaudeCodexMcp.Notifications.ChannelFeasibility;

public sealed record ClaudeChannelDeclaration
{
    [JsonPropertyName("capabilities")]
    public required ClaudeChannelCapabilities Capabilities { get; init; }

    [JsonPropertyName("instructions")]
    public required string Instructions { get; init; }
}

public sealed record ClaudeChannelCapabilities
{
    [JsonPropertyName("tools")]
    public IReadOnlyDictionary<string, EmptyCapabilityOptions> Tools { get; init; } =
        new Dictionary<string, EmptyCapabilityOptions>();

    [JsonPropertyName("experimental")]
    public required IReadOnlyDictionary<string, EmptyCapabilityOptions> Experimental { get; init; }
}

public sealed record EmptyCapabilityOptions;

public sealed record ClaudeChannelJsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; init; } = ClaudeChannelProtocolNames.ChannelNotification;

    [JsonPropertyName("params")]
    public required ClaudeChannelNotificationParams Params { get; init; }
}

public sealed record ClaudeChannelNotificationParams
{
    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("meta")]
    public required ClaudeChannelProbeMetadata Meta { get; init; }
}

public sealed record ClaudeChannelProbeMetadata
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = "claude-codex-mcp";

    [JsonPropertyName("event")]
    public string Event { get; init; } = "channel_feasibility_probe";

    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("statusline")]
    public string Statusline { get; init; } = "[codex status: context ? | weekly ? | 5h ?]";

    [JsonPropertyName("probe_id")]
    public required string ProbeId { get; init; }

    [JsonPropertyName("ts")]
    public required string Timestamp { get; init; }
}

public sealed record ClaudeCodeVersionCheck(
    string RawVersion,
    Version? ParsedVersion,
    bool IsAtLeastMinimum,
    bool IsTargetVersion);
