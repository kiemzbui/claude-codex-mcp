using System.Collections.Generic;

namespace ClaudeCodexMcp.Domain;

public sealed record ToolError(string Code, string Message, string? Field = null);

public sealed record CodexProfilePolicySummary
{
    public string Name { get; init; } = string.Empty;

    public string? TaskPrefix { get; init; }

    public string? Backend { get; init; }

    public bool ReadOnly { get; init; }

    public IReadOnlyDictionary<string, string> Permissions { get; init; } =
        new Dictionary<string, string>();

    public bool ChannelNotifications { get; init; }

    public string? DefaultModel { get; init; }

    public string? DefaultEffort { get; init; }

    public bool FastMode { get; init; }

    public string DefaultServiceTier { get; init; } = "normal";

    public string? DefaultWorkflow { get; init; }

    public IReadOnlyList<string> AllowedWorkflows { get; init; } = [];

    public int MaxConcurrentJobs { get; init; }
}

public sealed record CodexListProfilesResponse
{
    public IReadOnlyList<CodexProfilePolicySummary> Profiles { get; init; } = [];

    public IReadOnlyList<ToolError> Errors { get; init; } = [];
}

public sealed record DiscoveryBucketedResponse
{
    public string Kind { get; init; } = string.Empty;

    public bool CacheHit { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public IReadOnlyList<DiscoveryCacheItem> Global { get; init; } = [];

    public IReadOnlyList<DiscoveryCacheItem> RepoLocal { get; init; } = [];

    public IReadOnlyList<DiscoveryCacheItem> Configured { get; init; } = [];

    public IReadOnlyList<DiscoveryCacheItem> Merged { get; init; } = [];
}

public sealed record DiscoveryDetailResponse
{
    public bool Found { get; init; }

    public bool Ambiguous { get; init; }

    public DiscoveryCacheItem? Item { get; init; }

    public IReadOnlyList<DiscoveryCacheItem> Matches { get; init; } = [];

    public string? Body { get; init; }

    public bool BodyIncluded { get; init; }

    public bool Truncated { get; init; }

    public IReadOnlyList<ToolError> Errors { get; init; } = [];
}

public sealed record CodexJobCompactResponse
{
    public string JobId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Profile { get; init; } = string.Empty;

    public string Workflow { get; init; } = string.Empty;

    public string Repo { get; init; } = string.Empty;

    public JobState Status { get; init; }

    public string? CodexThreadId { get; init; }

    public string? CodexTurnId { get; init; }

    public string? CodexSessionId { get; init; }

    public string? Model { get; init; }

    public string? Effort { get; init; }

    public bool FastMode { get; init; }

    public string? ServiceTier { get; init; }

    public WaitingForInputRecord? WaitingForInput { get; init; }

    public string? ResultSummary { get; init; }

    public string? LastError { get; init; }

    public JobQueueSummary InputQueue { get; init; } = new();

    public string LogRef { get; init; } = string.Empty;

    public string NotificationLogRef { get; init; } = string.Empty;

    public string Statusline { get; init; } = "[codex status: context ? | weekly ? | 5h ?]";
}

public sealed record CodexStartTaskResponse
{
    public bool Accepted { get; init; }

    public CodexJobCompactResponse? Job { get; init; }

    public IReadOnlyList<ToolError> Errors { get; init; } = [];
}

public sealed record CodexStatusResponse
{
    public CodexJobCompactResponse? Job { get; init; }

    public bool WaitRequested { get; init; }

    public int WaitTimeoutSeconds { get; init; }

    public IReadOnlyList<ToolError> Errors { get; init; } = [];
}

public sealed record CodexResultResponse
{
    public CodexJobCompactResponse? Job { get; init; }

    public string? Summary { get; init; }

    public bool FullOutputIncluded { get; init; }

    public IReadOnlyList<ToolError> Errors { get; init; } = [];
}

public sealed record CodexSendInputResponse
{
    public bool Accepted { get; init; }

    public CodexJobCompactResponse? Job { get; init; }

    public IReadOnlyList<ToolError> Errors { get; init; } = [];
}

public sealed record CodexQueueInputResponse
{
    public bool Accepted { get; init; }

    public QueueItemSummary? QueueItem { get; init; }

    public int QueuePosition { get; init; }

    public CodexJobCompactResponse? Job { get; init; }

    public IReadOnlyList<ToolError> Errors { get; init; } = [];
}

public sealed record CodexCancelQueuedInputResponse
{
    public bool Accepted { get; init; }

    public QueueItemSummary? QueueItem { get; init; }

    public CodexJobCompactResponse? Job { get; init; }

    public IReadOnlyList<ToolError> Errors { get; init; } = [];
}

public sealed record CodexCancelResponse
{
    public bool Accepted { get; init; }

    public CodexJobCompactResponse? Job { get; init; }

    public IReadOnlyList<ToolError> Errors { get; init; } = [];
}

public sealed record CodexListJobsResponse
{
    public IReadOnlyList<CodexJobCompactResponse> Jobs { get; init; } = [];

    public IReadOnlyList<ToolError> Errors { get; init; } = [];
}
