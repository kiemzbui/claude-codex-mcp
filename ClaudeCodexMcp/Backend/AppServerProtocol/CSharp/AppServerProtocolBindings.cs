using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ClaudeCodexMcp.Backend.AppServerProtocol.CSharp;

public sealed record AppServerJsonRpcRequest(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] object? Params);

public sealed record AppServerClientInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

public sealed record AppServerInitializeCapabilities
{
    [JsonPropertyName("experimentalApi")]
    public bool ExperimentalApi { get; init; }

    [JsonPropertyName("optOutNotificationMethods")]
    public IReadOnlyList<string>? OptOutNotificationMethods { get; init; }
}

public sealed record AppServerInitializeParams
{
    [JsonPropertyName("clientInfo")]
    public required AppServerClientInfo ClientInfo { get; init; }

    [JsonPropertyName("capabilities")]
    public AppServerInitializeCapabilities? Capabilities { get; init; }
}

public sealed record AppServerThreadStartParams
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("serviceTier")]
    public string? ServiceTier { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("approvalPolicy")]
    public string? ApprovalPolicy { get; init; }

    [JsonPropertyName("approvalsReviewer")]
    public string? ApprovalsReviewer { get; init; }

    [JsonPropertyName("sandbox")]
    public string? Sandbox { get; init; }

    [JsonPropertyName("baseInstructions")]
    public string? BaseInstructions { get; init; }

    [JsonPropertyName("developerInstructions")]
    public string? DeveloperInstructions { get; init; }

    [JsonPropertyName("experimentalRawEvents")]
    public bool ExperimentalRawEvents { get; init; }

    [JsonPropertyName("persistExtendedHistory")]
    public bool PersistExtendedHistory { get; init; }
}

public sealed record AppServerTurnStartParams
{
    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }

    [JsonPropertyName("input")]
    public required IReadOnlyList<AppServerUserInput> Input { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("approvalPolicy")]
    public string? ApprovalPolicy { get; init; }

    [JsonPropertyName("approvalsReviewer")]
    public string? ApprovalsReviewer { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("serviceTier")]
    public string? ServiceTier { get; init; }

    [JsonPropertyName("effort")]
    public string? Effort { get; init; }
}

public sealed record AppServerTurnSteerParams
{
    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }

    [JsonPropertyName("turnId")]
    public required string TurnId { get; init; }

    [JsonPropertyName("input")]
    public required IReadOnlyList<AppServerUserInput> Input { get; init; }
}

public sealed record AppServerThreadReadParams
{
    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }

    [JsonPropertyName("includeTurns")]
    public bool IncludeTurns { get; init; }
}

public sealed record AppServerThreadResumeParams
{
    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("approvalPolicy")]
    public string? ApprovalPolicy { get; init; }

    [JsonPropertyName("approvalsReviewer")]
    public string? ApprovalsReviewer { get; init; }

    [JsonPropertyName("sandbox")]
    public string? Sandbox { get; init; }

    [JsonPropertyName("persistExtendedHistory")]
    public bool PersistExtendedHistory { get; init; }
}

public sealed record AppServerThreadIdParams
{
    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }
}

public sealed record AppServerTurnInterruptParams
{
    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }

    [JsonPropertyName("turnId")]
    public required string TurnId { get; init; }
}

public sealed record AppServerUserInput
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("text_elements")]
    public IReadOnlyList<object> TextElements { get; init; } = [];

    public static AppServerUserInput FromText(string text) => new()
    {
        Type = "text",
        Text = text,
        TextElements = []
    };
}
