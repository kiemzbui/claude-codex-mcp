using System.Collections.Generic;

namespace ClaudeCodexMcp.Domain;

public sealed record DispatchOptions(string? Model = null, string? Effort = null, bool? FastMode = null);

public sealed record StartDispatchRequest(
    string? Profile,
    string? Workflow,
    string? Title,
    string? Repo,
    DispatchOptions Options);

public sealed record SelectedDispatchOptions(
    string? Model,
    string? Effort,
    bool FastMode,
    string ServiceTier);

public sealed record ChannelNotificationPolicy(bool Enabled);

public sealed record ValidatedDispatchPolicy(
    string Profile,
    string Repo,
    string Workflow,
    string Title,
    string? TaskPrefix,
    string? Backend,
    bool ReadOnly,
    IReadOnlyDictionary<string, string> Permissions,
    int MaxConcurrentJobs,
    ChannelNotificationPolicy ChannelNotifications,
    SelectedDispatchOptions Options);

public sealed record ProfilePolicySummary(
    string Name,
    string? Repo,
    IReadOnlyList<string> AllowedRepos,
    string? TaskPrefix,
    string? Backend,
    bool ReadOnly,
    IReadOnlyDictionary<string, string> Permissions,
    string? DefaultWorkflow,
    IReadOnlyList<string> AllowedWorkflows,
    int MaxConcurrentJobs,
    ChannelNotificationPolicy ChannelNotifications,
    string? DefaultModel,
    IReadOnlyList<string> AllowedModels,
    bool AllowModelOverride,
    string? DefaultEffort,
    IReadOnlyList<string> AllowedEfforts,
    bool AllowEffortOverride,
    bool FastMode,
    bool AllowFastModeOverride,
    bool RequireFastMode,
    string DefaultServiceTier);

