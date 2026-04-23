using ClaudeCodexMcp.Backend;
using ClaudeCodexMcp.Configuration;
using ClaudeCodexMcp.Discovery;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Notifications;
using ClaudeCodexMcp.Storage;
using ClaudeCodexMcp.Supervisor;
using ClaudeCodexMcp.Usage;
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
    private readonly OutputStore? outputStore;
    private readonly ICodexBackend backend;
    private readonly CodexCapabilityDiscovery discovery;
    private readonly CodexJobLockRegistry jobLocks;
    private readonly UsageReporter usageReporter;
    private readonly NotificationDispatcher? notificationDispatcher;

    public CodexToolService(
        IOptions<ManagerOptions> options,
        IProfilePolicyValidator policyValidator,
        JobStore jobStore,
        QueueStore queueStore,
        ICodexBackend backend,
        CodexCapabilityDiscovery discovery,
        CodexJobLockRegistry jobLocks)
        : this(
            options,
            policyValidator,
            jobStore,
            queueStore,
            outputStore: null,
            backend,
            discovery,
            jobLocks,
            new UsageReporter())
    {
    }

    public CodexToolService(
        IOptions<ManagerOptions> options,
        IProfilePolicyValidator policyValidator,
        JobStore jobStore,
        QueueStore queueStore,
        OutputStore? outputStore,
        ICodexBackend backend,
        CodexCapabilityDiscovery discovery,
        CodexJobLockRegistry jobLocks)
        : this(
            options,
            policyValidator,
            jobStore,
            queueStore,
            outputStore,
            backend,
            discovery,
            jobLocks,
            new UsageReporter())
    {
    }

    public CodexToolService(
        IOptions<ManagerOptions> options,
        IProfilePolicyValidator policyValidator,
        JobStore jobStore,
        QueueStore queueStore,
        OutputStore? outputStore,
        ICodexBackend backend,
        CodexCapabilityDiscovery discovery,
        CodexJobLockRegistry jobLocks,
        UsageReporter usageReporter,
        NotificationDispatcher? notificationDispatcher = null)
    {
        this.options = options.Value;
        this.policyValidator = policyValidator;
        this.jobStore = jobStore;
        this.queueStore = queueStore;
        this.outputStore = outputStore;
        this.backend = backend;
        this.discovery = discovery;
        this.jobLocks = jobLocks;
        this.usageReporter = usageReporter;
        this.notificationDispatcher = notificationDispatcher;
    }

    private OutputStore OutputStore => outputStore
        ?? throw new InvalidOperationException("OutputStore is required for output pagination tools.");

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
            await DispatchJobStateChangeAsync(initialJob, updated, cancellationToken);

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
            await DispatchJobStateChangeAsync(initialJob, failed, cancellationToken);
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
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return new CodexStatusResponse
            {
                WaitRequested = wait,
                WaitTimeoutSeconds = waitTimeout,
                Errors = [MissingJobError(jobId)]
            };
        }

        await using var lease = await jobLocks.AcquireAsync(jobId.Trim(), cancellationToken);
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
            var previous = job;
            var status = await backend.ObserveStatusAsync(new CodexBackendObserveRequest
            {
                JobId = job.JobId,
                BackendIds = ToBackendIds(job)
            }, cancellationToken);
            job = CodexJobRecordUpdater.ApplyStatus(job, status);
            await jobStore.SaveAsync(job, cancellationToken);
            await DispatchJobStateChangeAsync(previous, job, cancellationToken);
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
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return new CodexResultResponse { Errors = [MissingJobError(jobId)] };
        }

        await using var lease = await jobLocks.AcquireAsync(jobId.Trim(), cancellationToken);
        var job = await ReadJobOrNullAsync(jobId, cancellationToken);
        if (job is null)
        {
            return new CodexResultResponse { Errors = [MissingJobError(jobId)] };
        }

        var normalizedDetail = string.IsNullOrWhiteSpace(detail)
            ? "summary"
            : detail.Trim().ToLowerInvariant();
        var includeFull = normalizedDetail == "full";
        var budget = normalizedDetail == "normal"
            ? OutputResponseLimits.NormalBytes
            : includeFull ? OutputResponseLimits.FullBytes : OutputResponseLimits.SummaryBytes;
        var output = await RefreshFinalOutputIfAvailableAsync(job, cancellationToken);
        job = output.Job;

        var artifactRefs = CreateOutputArtifactRefs(job);
        string? fullOutput = null;
        bool truncated = false;
        int? nextOffset = null;
        string? nextCursor = null;
        if (includeFull)
        {
            fullOutput = output.FinalOutput?.FinalText;
            if (string.IsNullOrEmpty(fullOutput))
            {
                var page = await OutputStore.ReadAsync(job.JobId, offset: 0, limit: int.MaxValue, cancellationToken);
                var pageResponse = OutputStoreBudget.CreateReadOutputResponse(
                    job.JobId,
                    threadId: null,
                    turnId: null,
                    agentId: null,
                    requestedOffset: 0,
                    requestedLimit: int.MaxValue,
                    format: "text",
                    page,
                    job.LogPath,
                    artifactRefs,
                    errors: []);
                fullOutput = pageResponse.Text;
                truncated = pageResponse.Truncated;
                nextOffset = pageResponse.NextOffset;
                nextCursor = pageResponse.NextCursor;
            }
            else
            {
                fullOutput = OutputStoreBudget.TruncateUtf8(
                    fullOutput,
                    OutputResponseLimits.FullBytes,
                    out truncated);
            }
        }

        var result = OutputStoreBudget.EnforceResultBudget(new CodexResultResponse
        {
            Job = ToCompact(job),
            Summary = job.ResultSummary,
            FullOutput = includeFull ? fullOutput : null,
            FullOutputIncluded = includeFull && fullOutput is not null,
            Truncated = truncated,
            NextOffset = nextOffset,
            NextCursor = nextCursor,
            ArtifactRefs = truncated || includeFull ? artifactRefs : []
        }, budget);

        if (result.Truncated && result.ArtifactRefs.Count == 0)
        {
            result = OutputStoreBudget.EnforceResultBudget(result with
            {
                NextCursor = null,
                ArtifactRefs = artifactRefs
            }, budget);
        }

        return result;
    }

    public async Task<CodexReadOutputResponse> ReadOutputAsync(
        string? jobId,
        string? threadId = null,
        string? turnId = null,
        string? agentId = null,
        int? offset = null,
        int? limit = null,
        string? format = null,
        CancellationToken cancellationToken = default)
    {
        var requestedOffset = Math.Max(offset ?? 0, 0);
        var requestedLimit = Math.Clamp(limit ?? 100, 1, 1000);
        var normalizedFormat = OutputStoreBudget.NormalizeFormat(format);
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return new CodexReadOutputResponse
            {
                Offset = requestedOffset,
                Limit = requestedLimit,
                Format = normalizedFormat,
                EndOfOutput = true,
                Errors = [MissingJobError(jobId)]
            };
        }

        if (!OutputStoreBudget.IsSupportedFormat(normalizedFormat))
        {
            return new CodexReadOutputResponse
            {
                JobId = jobId.Trim(),
                Offset = requestedOffset,
                Limit = requestedLimit,
                Format = normalizedFormat,
                EndOfOutput = true,
                Errors = [new ToolError("invalid_output_format", "format must be json, text, or jsonl.", "format")]
            };
        }

        await using var lease = await jobLocks.AcquireAsync(jobId.Trim(), cancellationToken);
        var job = await ReadJobOrNullAsync(jobId, cancellationToken);
        if (job is null)
        {
            return new CodexReadOutputResponse
            {
                JobId = jobId.Trim(),
                Offset = requestedOffset,
                Limit = requestedLimit,
                Format = normalizedFormat,
                EndOfOutput = true,
                Errors = [MissingJobError(jobId)]
            };
        }

        job = (await RefreshFinalOutputIfAvailableAsync(job, cancellationToken)).Job;
        var page = await OutputStore.ReadAsync(
            job.JobId,
            threadId,
            turnId,
            agentId,
            requestedOffset,
            requestedLimit,
            cancellationToken);
        IReadOnlyList<ToolError> errors = !OutputStore.Exists(job.JobId) || page.TotalCount == 0
            ? [new ToolError("output_not_found", "No output entries matched the requested job and filters.", "jobId")]
            : [];

        return OutputStoreBudget.CreateReadOutputResponse(
            job.JobId,
            threadId,
            turnId,
            agentId,
            requestedOffset,
            requestedLimit,
            normalizedFormat,
            page,
            job.LogPath,
            CreateOutputArtifactRefs(job),
            errors);
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

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return new CodexSendInputResponse { Errors = [MissingJobError(jobId)] };
        }

        await using var lease = await jobLocks.AcquireAsync(jobId.Trim(), cancellationToken);
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

        var updated = CodexJobRecordUpdater.ApplyStatus(persistedOptions, status);
        await jobStore.SaveAsync(updated, cancellationToken);
        await DispatchJobStateChangeAsync(persistedOptions, updated, cancellationToken);

        return new CodexSendInputResponse
        {
            Accepted = true,
            Job = ToCompact(updated)
        };
    }

    public async Task<CodexQueueInputResponse> QueueInputAsync(
        string? jobId,
        string? prompt,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new CodexQueueInputResponse
            {
                Errors = [new ToolError("blank_prompt", "A queued prompt is required.", "prompt")]
            };
        }

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return new CodexQueueInputResponse { Errors = [MissingJobError(jobId)] };
        }

        var normalizedJobId = jobId.Trim();
        await using var lease = await jobLocks.AcquireAsync(normalizedJobId, cancellationToken);
        var job = await ReadJobOrNullAsync(normalizedJobId, cancellationToken);
        if (job is null)
        {
            return new CodexQueueInputResponse { Errors = [MissingJobError(jobId)] };
        }

        if (IsTerminal(job.Status))
        {
            return new CodexQueueInputResponse
            {
                Errors = [new ToolError("terminal_job", "Cannot queue input for a terminal job.", "jobId")]
            };
        }

        var item = await queueStore.AddAsync(
            job.JobId,
            prompt.Trim(),
            title,
            cancellationToken: cancellationToken);
        var queue = await queueStore.ReadAsync(job.JobId, cancellationToken);
        var summary = queueStore.CreateSummary(queue);
        var updated = job with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            InputQueue = summary
        };
        await jobStore.SaveAsync(updated, cancellationToken);
        var queuePosition = summary.Items
            .Where(queueItem => queueItem.Status == QueueItemState.Pending)
            .OrderBy(queueItem => queueItem.CreatedAt)
            .ThenBy(queueItem => queueItem.QueueItemId, StringComparer.Ordinal)
            .TakeWhile(queueItem => !string.Equals(queueItem.QueueItemId, item.QueueItemId, StringComparison.Ordinal))
            .Count() + 1;

        return new CodexQueueInputResponse
        {
            Accepted = true,
            QueueItem = QueueStore.ToSummary(item),
            QueuePosition = queuePosition,
            Job = ToCompact(updated)
        };
    }

    public async Task<CodexCancelQueuedInputResponse> CancelQueuedInputAsync(
        string? jobId,
        string? queueItemId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return new CodexCancelQueuedInputResponse { Errors = [MissingJobError(jobId)] };
        }

        if (string.IsNullOrWhiteSpace(queueItemId))
        {
            return new CodexCancelQueuedInputResponse
            {
                Errors = [new ToolError("blank_queue_item_id", "A queueItemId is required.", "queueItemId")]
            };
        }

        var normalizedJobId = jobId.Trim();
        var normalizedQueueItemId = queueItemId.Trim();
        await using var lease = await jobLocks.AcquireAsync(normalizedJobId, cancellationToken);
        var job = await ReadJobOrNullAsync(normalizedJobId, cancellationToken);
        if (job is null)
        {
            return new CodexCancelQueuedInputResponse { Errors = [MissingJobError(jobId)] };
        }

        var queue = await queueStore.ReadAsync(job.JobId, cancellationToken);
        var currentItem = queue.Items.SingleOrDefault(item =>
            string.Equals(item.QueueItemId, normalizedQueueItemId, StringComparison.Ordinal));
        if (currentItem is null)
        {
            return new CodexCancelQueuedInputResponse
            {
                Job = ToCompact(job),
                Errors = [new ToolError("queue_item_not_found", $"Queue item '{normalizedQueueItemId}' was not found.", "queueItemId")]
            };
        }

        if (currentItem.Status != QueueItemState.Pending)
        {
            return new CodexCancelQueuedInputResponse
            {
                Job = ToCompact(job),
                QueueItem = QueueStore.ToSummary(currentItem),
                Errors =
                [
                    new ToolError(
                        "queue_item_not_pending",
                        "Only pending queue items can be cancelled.",
                        "queueItemId")
                ]
            };
        }

        var (updatedQueue, updatedItem) = await queueStore.CancelPendingAsync(
            job.JobId,
            normalizedQueueItemId,
            cancellationToken: cancellationToken);
        var updated = job with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            InputQueue = queueStore.CreateSummary(updatedQueue)
        };
        await jobStore.SaveAsync(updated, cancellationToken);

        return new CodexCancelQueuedInputResponse
        {
            Accepted = true,
            QueueItem = updatedItem is null ? null : QueueStore.ToSummary(updatedItem),
            Job = ToCompact(updated)
        };
    }

    public async Task<CodexCancelResponse> CancelAsync(
        string? jobId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return new CodexCancelResponse { Errors = [MissingJobError(jobId)] };
        }

        await using var lease = await jobLocks.AcquireAsync(jobId.Trim(), cancellationToken);
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
        var updated = CodexJobRecordUpdater.ApplyStatus(job, status) with { Status = JobState.Cancelled };
        await jobStore.SaveAsync(updated, cancellationToken);
        await DispatchJobStateChangeAsync(job, updated, cancellationToken);

        return new CodexCancelResponse
        {
            Accepted = true,
            Job = ToCompact(updated)
        };
    }

    public async Task<CodexUsageResponse> UsageAsync(
        string? jobId,
        bool refresh = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            var usage = usageReporter.CreateSummary(null);
            return new CodexUsageResponse
            {
                Usage = usage,
                ContextRemainingPercentEstimate = usage.ContextRemainingPercentEstimate,
                WeeklyUsageRemainingPercent = usage.WeeklyUsageRemainingPercent,
                FiveHourUsageRemainingPercent = usage.FiveHourUsageRemainingPercent,
                Statusline = usage.Statusline,
                Errors = [MissingJobError(jobId)]
            };
        }

        await using var lease = await jobLocks.AcquireAsync(jobId.Trim(), cancellationToken);
        var job = await ReadJobOrNullAsync(jobId, cancellationToken);
        if (job is null)
        {
            var usage = usageReporter.CreateSummary(null);
            return new CodexUsageResponse
            {
                JobId = jobId.Trim(),
                Usage = usage,
                ContextRemainingPercentEstimate = usage.ContextRemainingPercentEstimate,
                WeeklyUsageRemainingPercent = usage.WeeklyUsageRemainingPercent,
                FiveHourUsageRemainingPercent = usage.FiveHourUsageRemainingPercent,
                Statusline = usage.Statusline,
                Errors = [MissingJobError(jobId)]
            };
        }

        var errors = new List<ToolError>();
        if (refresh &&
            backend.Capabilities.SupportsReadUsage &&
            !string.IsNullOrWhiteSpace(job.CodexThreadId))
        {
            try
            {
                var usageSnapshot = await backend.ReadUsageAsync(new CodexBackendUsageRequest
                {
                    JobId = job.JobId,
                    BackendIds = ToBackendIds(job)
                }, cancellationToken);
                if (usageSnapshot.TokenUsage is not null || usageSnapshot.RateLimits is not null)
                {
                    job = CodexJobRecordUpdater.ApplyUsage(job, usageSnapshot);
                    await jobStore.SaveAsync(job, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add(new ToolError(
                    "usage_read_failed",
                    ProjectionSanitizer.ToSummary(exception.Message),
                    "jobId"));
            }
        }

        var normalized = usageReporter.CreateSummary(job.UsageSnapshot);
        return new CodexUsageResponse
        {
            JobId = job.JobId,
            Usage = normalized,
            ContextRemainingPercentEstimate = normalized.ContextRemainingPercentEstimate,
            WeeklyUsageRemainingPercent = normalized.WeeklyUsageRemainingPercent,
            FiveHourUsageRemainingPercent = normalized.FiveHourUsageRemainingPercent,
            Statusline = normalized.Statusline,
            Job = ToCompact(job),
            Errors = errors
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

    private async Task<(CodexJobRecord Job, CodexBackendOutput? FinalOutput)> RefreshFinalOutputIfAvailableAsync(
        CodexJobRecord job,
        CancellationToken cancellationToken)
    {
        if (job.Status != JobState.Completed || !backend.Capabilities.SupportsReadFinalOutput)
        {
            return (job, null);
        }

        var output = await backend.ReadFinalOutputAsync(new CodexBackendOutputRequest
        {
            JobId = job.JobId,
            BackendIds = ToBackendIds(job)
        }, cancellationToken);
        var updated = CodexJobRecordUpdater.ApplyOutput(job, output);
        await jobStore.SaveAsync(updated, cancellationToken);

        if (outputStore is not null &&
            !OutputStore.Exists(updated.JobId) &&
            !string.IsNullOrWhiteSpace(output.FinalText))
        {
            await OutputStore.AppendAsync(new OutputLogEntry
            {
                JobId = updated.JobId,
                ThreadId = updated.CodexThreadId,
                TurnId = updated.CodexTurnId,
                Source = "backend_final_output",
                Level = "info",
                Message = output.FinalText
            }, cancellationToken);
        }

        return (updated, output);
    }

    private static IReadOnlyList<OutputArtifactRef> CreateOutputArtifactRefs(CodexJobRecord job)
    {
        var refs = new List<OutputArtifactRef>
        {
            new()
            {
                Kind = "log",
                Ref = job.LogPath,
                Description = "Local JSONL output log for paginated reads."
            }
        };

        if (!string.IsNullOrWhiteSpace(job.CodexThreadId))
        {
            refs.Add(new OutputArtifactRef
            {
                Kind = "backendThread",
                Ref = job.CodexThreadId,
                Description = "Backend thread id for history retrieval when supported."
            });
        }

        return refs;
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
        NotificationMode = policy.ChannelNotifications.Enabled ? NotificationModes.Channel : NotificationModes.Disabled,
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
        UsageSnapshot = status.UsageSnapshot ?? job.UsageSnapshot,
        LastError = ProjectionSanitizer.ToOptionalSummary(status.LastError)
    };

    private static CodexBackendIds ToBackendIds(CodexJobRecord job) => new()
    {
        ThreadId = job.CodexThreadId,
        TurnId = job.CodexTurnId,
        SessionId = job.CodexSessionId
    };

    private CodexJobCompactResponse ToCompact(CodexJobRecord job) => new()
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
        NotificationLogRef = job.NotificationLogPath,
        Statusline = usageReporter.CreateStatusline(job.UsageSnapshot)
    };

    private CodexJobCompactResponse ToCompact(JobIndexEntry job) => new()
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
        NotificationLogRef = Path.Combine(".codex-manager", "notifications", $"{job.JobId}.jsonl"),
        Statusline = UsageReporter.UnknownStatusline
    };

    private static ToolError MissingJobError(string? jobId) =>
        string.IsNullOrWhiteSpace(jobId)
            ? new ToolError("blank_job_id", "A jobId is required.", "jobId")
            : new ToolError("job_not_found", $"Job '{jobId.Trim()}' was not found.", "jobId");

    private async Task DispatchJobStateChangeAsync(
        CodexJobRecord before,
        CodexJobRecord after,
        CancellationToken cancellationToken)
    {
        if (notificationDispatcher is null)
        {
            return;
        }

        await notificationDispatcher.DispatchJobStateChangeAsync(
            before,
            after,
            string.Equals(after.NotificationMode, NotificationModes.Channel, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
    }

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
