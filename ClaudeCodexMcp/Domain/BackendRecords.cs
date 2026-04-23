using System.Collections.Generic;

namespace ClaudeCodexMcp.Domain;

public static class CodexBackendNames
{
    public const string AppServer = "appServer";
    public const string Cli = "cli";
    public const string Fake = "fake";
}

public static class CodexBackendCapabilityNames
{
    public const string Start = "start";
    public const string ObserveStatus = "observe_status";
    public const string PollStatus = "poll_status";
    public const string SendInput = "send_input";
    public const string Cancel = "cancel";
    public const string ReadFinalOutput = "read_final_output";
    public const string ReadUsage = "read_usage";
    public const string Resume = "resume";
}

public sealed record CodexBackendDegradedCapability(
    string Capability,
    string Reason,
    bool Terminal = false);

public sealed record CodexBackendCapabilities
{
    public string BackendId { get; init; } = string.Empty;

    public string BackendKind { get; init; } = string.Empty;

    public bool SupportsStart { get; init; }

    public bool SupportsObserveStatus { get; init; }

    public bool SupportsStatusPolling { get; init; }

    public bool SupportsSendInput { get; init; }

    public bool SupportsCancel { get; init; }

    public bool SupportsReadFinalOutput { get; init; }

    public bool SupportsReadUsage { get; init; }

    public bool SupportsResume { get; init; }

    public IReadOnlyList<CodexBackendDegradedCapability> DegradedCapabilities { get; init; } = [];

    public static CodexBackendCapabilities AppServer(string backendId = "codex-app-server") => new()
    {
        BackendId = backendId,
        BackendKind = CodexBackendNames.AppServer,
        SupportsStart = true,
        SupportsObserveStatus = true,
        SupportsStatusPolling = true,
        SupportsSendInput = true,
        SupportsCancel = true,
        SupportsReadFinalOutput = true,
        SupportsReadUsage = true,
        SupportsResume = true
    };

    public static CodexBackendCapabilities CliFallbackShape(string backendId = "codex-cli") => new()
    {
        BackendId = backendId,
        BackendKind = CodexBackendNames.Cli,
        SupportsStart = true,
        SupportsObserveStatus = false,
        SupportsStatusPolling = false,
        SupportsSendInput = false,
        SupportsCancel = true,
        SupportsReadFinalOutput = true,
        SupportsReadUsage = false,
        SupportsResume = false,
        DegradedCapabilities =
        [
            new(CodexBackendCapabilityNames.ObserveStatus, "CLI fallback cannot stream app-server lifecycle notifications."),
            new(CodexBackendCapabilityNames.PollStatus, "CLI fallback status polling is not implemented in Stage 8."),
            new(CodexBackendCapabilityNames.SendInput, "CLI fallback follow-up input is not implemented in Stage 6."),
            new(CodexBackendCapabilityNames.ReadUsage, "CLI fallback does not expose app-server token usage or rate-limit windows."),
            new(CodexBackendCapabilityNames.Resume, "CLI fallback does not provide verified thread resume support.")
        ]
    };
}

public sealed record CodexBackendLaunchPolicy
{
    public string ApprovalPolicy { get; init; } = "never";

    public string ApprovalsReviewer { get; init; } = "user";

    public string Sandbox { get; init; } = "read-only";
}

public sealed record CodexBackendIds
{
    public string? ThreadId { get; init; }

    public string? TurnId { get; init; }

    public string? SessionId { get; init; }
}

public sealed record CodexBackendDispatchOptions
{
    public string? Model { get; init; }

    public string? Effort { get; init; }

    public bool FastMode { get; init; }

    public string ServiceTier { get; init; } = "normal";

    public static CodexBackendDispatchOptions FromSelected(SelectedDispatchOptions options) => new()
    {
        Model = options.Model,
        Effort = options.Effort,
        FastMode = options.FastMode,
        ServiceTier = options.ServiceTier
    };
}

public sealed record CodexBackendStartRequest
{
    public required string JobId { get; init; }

    public required string Title { get; init; }

    public required string Repo { get; init; }

    public required string Prompt { get; init; }

    public required string Workflow { get; init; }

    public CodexBackendDispatchOptions Options { get; init; } = new();

    public CodexBackendLaunchPolicy LaunchPolicy { get; init; } = new();

    public string? TaskPrefix { get; init; }
}

public sealed record CodexBackendObserveRequest
{
    public required string JobId { get; init; }

    public required CodexBackendIds BackendIds { get; init; }
}

public sealed record CodexBackendSendInputRequest
{
    public required string JobId { get; init; }

    public required CodexBackendIds BackendIds { get; init; }

    public required string Prompt { get; init; }

    public CodexBackendDispatchOptions Options { get; init; } = new();

    public CodexBackendLaunchPolicy LaunchPolicy { get; init; } = new();
}

public sealed record CodexBackendCancelRequest
{
    public required string JobId { get; init; }

    public required CodexBackendIds BackendIds { get; init; }
}

public sealed record CodexBackendOutputRequest
{
    public required string JobId { get; init; }

    public required CodexBackendIds BackendIds { get; init; }
}

public sealed record CodexBackendUsageRequest
{
    public required string JobId { get; init; }

    public required CodexBackendIds BackendIds { get; init; }
}

public sealed record CodexBackendResumeRequest
{
    public required string JobId { get; init; }

    public required string Repo { get; init; }

    public required CodexBackendIds BackendIds { get; init; }

    public CodexBackendLaunchPolicy LaunchPolicy { get; init; } = new();
}

public sealed record CodexBackendStatus
{
    public JobState State { get; init; } = JobState.Running;

    public CodexBackendIds BackendIds { get; init; } = new();

    public string? Message { get; init; }

    public WaitingForInputRecord? WaitingForInput { get; init; }

    public string? ResultSummary { get; init; }

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public string? TestSummary { get; init; }

    public CodexBackendUsageSnapshot? UsageSnapshot { get; init; }

    public string? LastError { get; init; }
}

public sealed record CodexBackendStartResult
{
    public CodexBackendStatus Status { get; init; } = new();

    public CodexBackendIds BackendIds => Status.BackendIds;
}

public sealed record CodexBackendOutput
{
    public CodexBackendIds BackendIds { get; init; } = new();

    public string? FinalText { get; init; }

    public string? Summary { get; init; }

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public string? TestSummary { get; init; }
}

public sealed record CodexBackendTokenUsage
{
    public int? TotalTokens { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public int? ReasoningOutputTokens { get; init; }

    public int? ContextWindowTokens { get; init; }
}

public sealed record CodexBackendRateLimitWindow
{
    public double? UsedPercent { get; init; }

    public int? WindowDurationMinutes { get; init; }

    public DateTimeOffset? ResetsAt { get; init; }
}

public sealed record CodexBackendRateLimits
{
    public string? LimitId { get; init; }

    public CodexBackendRateLimitWindow? Primary { get; init; }

    public CodexBackendRateLimitWindow? Secondary { get; init; }
}

public sealed record CodexBackendUsageSnapshot
{
    public CodexBackendTokenUsage? TokenUsage { get; init; }

    public CodexBackendRateLimits? RateLimits { get; init; }
}
