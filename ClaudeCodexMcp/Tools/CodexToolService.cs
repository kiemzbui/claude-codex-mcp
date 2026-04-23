using ClaudeCodexMcp.Backend;
using ClaudeCodexMcp.Configuration;
using ClaudeCodexMcp.Discovery;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Storage;
using Microsoft.Extensions.Options;

namespace ClaudeCodexMcp.Tools;

public sealed class CodexToolService
{
    private const int DefaultStatusWaitSeconds = 20;
    private const int MaxStatusWaitSeconds = 25;
    private readonly ManagerOptions options;
    private readonly IProfilePolicyValidator policyValidator;
    private readonly JobStore jobStore;
    private readonly QueueStore queueStore;
    private readonly ICodexBackend backend;
    private readonly CodexCapabilityDiscovery discovery;

    public CodexToolService(
        IOptions<ManagerOptions> options,
        IProfilePolicyValidator policyValidator,
        JobStore jobStore,
        QueueStore queueStore,
        ICodexBackend backend,
        CodexCapabilityDiscovery discovery)
    {
        this.options = options.Value;
        this.policyValidator = policyValidator;
        this.jobStore = jobStore;
        this.queueStore = queueStore;
        this.backend = backend;
        this.discovery = discovery;
    }

    public CodexListProfilesResponse ListProfiles()
    {
        var profiles = new List<CodexProfilePolicySummary>();
        var errors = new List<ToolError>();

        foreach (var profileName in options.Profiles.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var result = policyValidator.GetProfileSummary(profileName);
            if (!result.IsValid || result.Value is null)
            {
                errors.AddRange(result.Errors.Select(ToToolError));
                continue;
            }

            profiles.Add(ToToolProfileSummary(result.Value));
        }

        return new CodexListProfilesResponse
        {
            Profiles = profiles,
            Errors = errors
        };
    }

    public Task<DiscoveryBucketedResponse> ListSkillsAsync(
        bool forceRefresh = false,
        string? repo = null,
        CancellationToken cancellationToken = default) =>
        discovery.ListSkillsAsync(forceRefresh, repo, cancellationToken);

    public Task<DiscoveryBucketedResponse> ListAgentsAsync(
        bool forceRefresh = false,
        string? repo = null,
        CancellationToken cancellationToken = default) =>
        discovery.ListAgentsAsync(forceRefresh, repo, cancellationToken);

    public Task<DiscoveryDetailResponse> GetSkillAsync(
        string? name,
        string? sourceScope = null,
        string? sourcePath = null,
        bool includeBody = false,
        bool forceRefresh = false,
        string? repo = null,
        int maxBytes = 32768,
        CancellationToken cancellationToken = default) =>
        discovery.GetSkillAsync(name, sourceScope, sourcePath, includeBody, forceRefresh, repo, maxBytes, cancellationToken);

    public Task<DiscoveryDetailResponse> GetAgentAsync(
        string? name,
        string? sourceScope = null,
        string? sourcePath = null,
        bool includePrompt = false,
        bool forceRefresh = false,
        string? repo = null,
        int maxBytes = 32768,
        CancellationToken cancellationToken = default) =>
        discovery.GetAgentAsync(name, sourceScope, sourcePath, includePrompt, forceRefresh, repo, maxBytes, cancellationToken);

    public async Task<CodexStartTaskResponse> StartTaskAsync(
        string? profile,
        string? workflow,
        string? title,
        string? repo,
        string? prompt,
        string? model = null,
        string? effort = null,
        bool? fastMode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new CodexStartTaskResponse
            {
                Errors = [new ToolError("blank_prompt", "A task prompt is required.", "prompt")]
            };
        }

        var validation = policyValidator.ValidateStartDispatch(new StartDispatchRequest(
            profile,
            workflow,
            title,
            repo,
            new DispatchOptions(model, effort, fastMode)));
        if (!validation.IsValid || validation.Value is null)
        {
            return new CodexStartTaskResponse { Errors = validation.Errors.Select(ToToolError).ToArray() };
        }

        var activeLimitError = await ValidateConcurrentLimitAsync(validation.Value, cancellationToken);
        if (activeLimitError is not null)
        {
            return new CodexStartTaskResponse { Errors = [activeLimitError] };
        }

        var now = DateTimeOffset.UtcNow;
        var jobId = $"job_{now:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
        var initialJob = CreateInitialJob(jobId, validation.Value, prompt!, now);
        await jobStore.SaveAsync(initialJob, cancellationToken);

        try
        {
            var start = await backend.StartAsync(new CodexBackendStartRequest
            {
                JobId = jobId,
                Title = validation.Value.Title,
                Repo = validation.Value.Repo,
                Prompt = ComposePrompt(validation.Value, prompt!),
                Workflow = validation.Value.Workflow,
                TaskPrefix = validation.Value.TaskPrefix,
                Options = CodexBackendDispatchOptions.FromSelected(validation.Value.Options),
                LaunchPolicy = CreateLaunchPolicy(validation.Value)
            }, cancellationToken);

            var updated = ApplyBackendStatus(initialJob, start.Status);
            await jobStore.SaveAsync(updated, cancellationToken);

            return new CodexStartTaskResponse
            {
                Accepted = true,
                Job = ToCompact(updated)
            };
        }
        catch (Exception exception)
        {
            var failed = initialJob with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                Status = JobState.Failed,
                LastError = ProjectionSanitizer.ToSummary(exception.Message)
            };
            await jobStore.SaveAsync(failed, cancellationToken);
            return new CodexStartTaskResponse
            {
                Accepted = true,
                Job = ToCompact(failed),
                Errors = [new ToolError("backend_start_failed", "The job was persisted but backend dispatch failed.", null)]
            };
        }
    }

    public async Task<CodexStatusResponse> StatusAsync(
        string? jobId,
        bool wait = false,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var waitTimeout = wait ? Math.Clamp(timeoutSeconds ?? DefaultStatusWaitSeconds, 0, MaxStatusWaitSeconds) : 0;
        var job = await ReadJobOrNullAsync(jobId, cancellationToken);
        if (job is null)
        {
            return new CodexStatusResponse
            {
                WaitRequested = wait,
                WaitTimeoutSeconds = waitTimeout,
                Errors = [MissingJobError(jobId)]
            };
        }

        if (!IsTerminal(job.Status))
        {
            var status = await backend.ObserveStatusAsync(new CodexBackendObserveRequest
            {
                JobId = job.JobId,
                BackendIds = ToBackendIds(job)
            }, cancellationToken);
            job = ApplyBackendStatus(job, status);
            await jobStore.SaveAsync(job, cancellationToken);
        }

        return new CodexStatusResponse
        {
            Job = ToCompact(job),
            WaitRequested = wait,
            WaitTimeoutSeconds = waitTimeout
        };
    }

    public async Task<CodexResultResponse> ResultAsync(
        string? jobId,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        var job = await ReadJobOrNullAsync(jobId, cancellationToken);
        if (job is null)
        {
            return new CodexResultResponse { Errors = [MissingJobError(jobId)] };
        }

        var includeFull = string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase);
        if (job.Status == JobState.Completed)
        {
            var output = await backend.ReadFinalOutputAsync(new CodexBackendOutputRequest
            {
                JobId = job.JobId,
                BackendIds = ToBackendIds(job)
            }, cancellationToken);
            var summary = ProjectionSanitizer.ToOptionalSummary(output.Summary ?? output.FinalText, 2048);
            job = job with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                ResultSummary = summary,
                CodexThreadId = output.BackendIds.ThreadId ?? job.CodexThreadId,
                CodexTurnId = output.BackendIds.TurnId ?? job.CodexTurnId,
                CodexSessionId = output.BackendIds.SessionId ?? job.CodexSessionId
            };
            await jobStore.SaveAsync(job, cancellationToken);
        }

        return new CodexResultResponse
        {
            Job = ToCompact(job),
            Summary = job.ResultSummary,
            FullOutputIncluded = includeFull && false
        };
    }

    public async Task<CodexSendInputResponse> SendInputAsync(
        string? jobId,
        string? prompt,
        string? model = null,
        string? effort = null,
        bool? fastMode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new CodexSendInputResponse
            {
                Errors = [new ToolError("blank_prompt", "A continuation prompt is required.", "prompt")]
            };
        }

        var job = await ReadJobOrNullAsync(jobId, cancellationToken);
        if (job is null)
        {
            return new CodexSendInputResponse { Errors = [MissingJobError(jobId)] };
        }

        if (IsTerminal(job.Status))
        {
            return new CodexSendInputResponse
            {
                Errors = [new ToolError("terminal_job", "Cannot send input to a terminal job.", "jobId")]
            };
        }

        var validation = policyValidator.ValidateStartDispatch(new StartDispatchRequest(
            job.Profile,
            job.Workflow,
            job.Title,
            job.Repo,
            new DispatchOptions(model, effort, fastMode)));
        if (!validation.IsValid || validation.Value is null)
        {
            return new CodexSendInputResponse { Errors = validation.Errors.Select(ToToolError).ToArray() };
        }

        var selected = validation.Value.Options;
        var persistedOptions = job with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Model = selected.Model,
            Effort = selected.Effort,
            FastMode = selected.FastMode,
            ServiceTier = selected.ServiceTier
        };
        await jobStore.SaveAsync(persistedOptions, cancellationToken);

        var status = await backend.SendInputAsync(new CodexBackendSendInputRequest
        {
            JobId = job.JobId,
            BackendIds = ToBackendIds(persistedOptions),
            Prompt = prompt.Trim(),
            Options = CodexBackendDispatchOptions.FromSelected(selected),
            LaunchPolicy = CreateLaunchPolicy(validation.Value)
        }, cancellationToken);

        var updated = ApplyBackendStatus(persistedOptions, status);
        await jobStore.SaveAsync(updated, cancellationToken);

        return new CodexSendInputResponse
        {
            Accepted = true,
            Job = ToCompact(updated)
        };
    }

    public async Task<CodexCancelResponse> CancelAsync(
        string? jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await ReadJobOrNullAsync(jobId, cancellationToken);
        if (job is null)
        {
            return new CodexCancelResponse { Errors = [MissingJobError(jobId)] };
        }

        if (IsTerminal(job.Status))
        {
            return new CodexCancelResponse
            {
                Accepted = true,
                Job = ToCompact(job)
            };
        }

        var status = await backend.CancelAsync(new CodexBackendCancelRequest
        {
            JobId = job.JobId,
            BackendIds = ToBackendIds(job)
        }, cancellationToken);
        var updated = ApplyBackendStatus(job, status) with { Status = JobState.Cancelled };
        await jobStore.SaveAsync(updated, cancellationToken);

        return new CodexCancelResponse
        {
            Accepted = true,
            Job = ToCompact(updated)
        };
    }

    public async Task<CodexListJobsResponse> ListJobsAsync(
        int limit = 50,
        bool includeTerminal = true,
        CancellationToken cancellationToken = default)
    {
        var index = await jobStore.ReadIndexAsync(cancellationToken);
        var jobs = index.Jobs
            .Where(job => includeTerminal || !IsTerminal(job.Status))
            .Take(Math.Clamp(limit, 1, 200))
            .Select(ToCompact)
            .ToArray();

        return new CodexListJobsResponse { Jobs = jobs };
    }

    private async Task<ToolError?> ValidateConcurrentLimitAsync(
        ValidatedDispatchPolicy policy,
        CancellationToken cancellationToken)
    {
        var index = await jobStore.ReadIndexAsync(cancellationToken);
        var activeCount = index.Jobs.Count(job =>
            string.Equals(job.Profile, policy.Profile, StringComparison.OrdinalIgnoreCase)
            && !IsTerminal(job.Status));

        return activeCount >= policy.MaxConcurrentJobs
            ? new ToolError("max_concurrent_jobs_exceeded", "The selected profile has reached its active job limit.", "profile")
            : null;
    }

    private CodexJobRecord CreateInitialJob(
        string jobId,
        ValidatedDispatchPolicy policy,
        string prompt,
        DateTimeOffset now) => new()
    {
        JobId = jobId,
        CreatedAt = now,
        UpdatedAt = now,
        Profile = policy.Profile,
        Workflow = policy.Workflow,
        Repo = policy.Repo,
        Title = policy.Title,
        Status = JobState.Queued,
        PromptSummary = ProjectionSanitizer.ToSummary(prompt),
        Model = policy.Options.Model,
        Effort = policy.Options.Effort,
        FastMode = policy.Options.FastMode,
        ServiceTier = policy.Options.ServiceTier,
        InputQueue = queueStore.CreateEmptySummary(jobId),
        LogPath = Path.Combine(".codex-manager", "logs", $"{jobId}.jsonl"),
        NotificationMode = "disabled",
        NotificationLogPath = Path.Combine(".codex-manager", "notifications", $"{jobId}.jsonl")
    };

    private static CodexBackendLaunchPolicy CreateLaunchPolicy(ValidatedDispatchPolicy policy)
    {
        var sandbox = policy.Permissions.TryGetValue("sandbox", out var configuredSandbox)
            ? configuredSandbox
            : policy.ReadOnly ? "read-only" : "workspace-write";

        return new CodexBackendLaunchPolicy
        {
            ApprovalPolicy = policy.Permissions.TryGetValue("approvalPolicy", out var approvalPolicy)
                ? approvalPolicy
                : "never",
            ApprovalsReviewer = policy.Permissions.TryGetValue("approvalsReviewer", out var reviewer)
                ? reviewer
                : "user",
            Sandbox = sandbox
        };
    }

    private static string ComposePrompt(ValidatedDispatchPolicy policy, string prompt)
    {
        var trimmedPrompt = prompt.Trim();
        return string.IsNullOrWhiteSpace(policy.TaskPrefix)
            ? trimmedPrompt
            : $"{policy.TaskPrefix.Trim()}{Environment.NewLine}{Environment.NewLine}{trimmedPrompt}";
    }

    private async Task<CodexJobRecord?> ReadJobOrNullAsync(string? jobId, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(jobId)
            ? null
            : await jobStore.ReadAsync(jobId.Trim(), cancellationToken);

    private static CodexJobRecord ApplyBackendStatus(CodexJobRecord job, CodexBackendStatus status) => job with
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        Status = status.State,
        CodexThreadId = status.BackendIds.ThreadId ?? job.CodexThreadId,
        CodexTurnId = status.BackendIds.TurnId ?? job.CodexTurnId,
        CodexSessionId = status.BackendIds.SessionId ?? job.CodexSessionId,
        WaitingForInput = status.WaitingForInput,
        LastError = ProjectionSanitizer.ToOptionalSummary(status.LastError)
    };

    private static CodexBackendIds ToBackendIds(CodexJobRecord job) => new()
    {
        ThreadId = job.CodexThreadId,
        TurnId = job.CodexTurnId,
        SessionId = job.CodexSessionId
    };

    private static CodexJobCompactResponse ToCompact(CodexJobRecord job) => new()
    {
        JobId = job.JobId,
        Title = ProjectionSanitizer.ToSummary(job.Title, 160),
        Profile = job.Profile,
        Workflow = job.Workflow,
        Repo = job.Repo,
        Status = job.Status,
        CodexThreadId = job.CodexThreadId,
        CodexTurnId = job.CodexTurnId,
        CodexSessionId = job.CodexSessionId,
        Model = job.Model,
        Effort = job.Effort,
        FastMode = job.FastMode,
        ServiceTier = job.ServiceTier,
        WaitingForInput = job.WaitingForInput,
        ResultSummary = ProjectionSanitizer.ToOptionalSummary(job.ResultSummary, 2048),
        LastError = ProjectionSanitizer.ToOptionalSummary(job.LastError),
        InputQueue = job.InputQueue,
        LogRef = job.LogPath,
        NotificationLogRef = job.NotificationLogPath
    };

    private static CodexJobCompactResponse ToCompact(JobIndexEntry job) => new()
    {
        JobId = job.JobId,
        Title = ProjectionSanitizer.ToSummary(job.Title, 160),
        Profile = job.Profile,
        Workflow = job.Workflow,
        Repo = job.Repo,
        Status = job.Status,
        CodexThreadId = job.CodexThreadId,
        CodexTurnId = job.CodexTurnId,
        CodexSessionId = job.CodexSessionId,
        ResultSummary = ProjectionSanitizer.ToOptionalSummary(job.ResultSummary),
        LastError = ProjectionSanitizer.ToOptionalSummary(job.LastError),
        InputQueue = job.InputQueue,
        LogRef = Path.Combine(".codex-manager", "logs", $"{job.JobId}.jsonl"),
        NotificationLogRef = Path.Combine(".codex-manager", "notifications", $"{job.JobId}.jsonl")
    };

    private static ToolError MissingJobError(string? jobId) =>
        string.IsNullOrWhiteSpace(jobId)
            ? new ToolError("blank_job_id", "A jobId is required.", "jobId")
            : new ToolError("job_not_found", $"Job '{jobId.Trim()}' was not found.", "jobId");

    private static CodexProfilePolicySummary ToToolProfileSummary(ProfilePolicySummary summary) => new()
    {
        Name = summary.Name,
        TaskPrefix = summary.TaskPrefix,
        Backend = summary.Backend,
        ReadOnly = summary.ReadOnly,
        Permissions = summary.Permissions,
        ChannelNotifications = summary.ChannelNotifications.Enabled,
        DefaultModel = summary.DefaultModel,
        DefaultEffort = summary.DefaultEffort,
        FastMode = summary.FastMode,
        DefaultServiceTier = summary.DefaultServiceTier,
        DefaultWorkflow = summary.DefaultWorkflow,
        AllowedWorkflows = summary.AllowedWorkflows,
        MaxConcurrentJobs = summary.MaxConcurrentJobs
    };

    private static ToolError ToToolError(PolicyValidationError error) =>
        new(error.Code, error.Message, error.Field);

    private static bool IsTerminal(JobState state) =>
        state is JobState.Completed or JobState.Failed or JobState.Cancelled;
}
