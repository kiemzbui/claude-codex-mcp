using ClaudeCodexMcp.Backend;
using ClaudeCodexMcp.Configuration;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Notifications;
using ClaudeCodexMcp.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaudeCodexMcp.Supervisor;

public sealed class CodexJobSupervisor : BackgroundService
{
    private readonly JobStore jobStore;
    private readonly QueueStore queueStore;
    private readonly OutputStore outputStore;
    private readonly ICodexBackend backend;
    private readonly CodexJobLockRegistry jobLocks;
    private readonly ILogger<CodexJobSupervisor> logger;
    private readonly CodexJobSupervisorOptions options;
    private readonly NotificationDispatcher? notificationDispatcher;
    private readonly ManagerOptions? managerOptions;
    private readonly Dictionary<string, int> transientFailures = new(StringComparer.Ordinal);

    public CodexJobSupervisor(
        JobStore jobStore,
        QueueStore queueStore,
        OutputStore outputStore,
        ICodexBackend backend,
        CodexJobLockRegistry jobLocks,
        ILogger<CodexJobSupervisor> logger,
        CodexJobSupervisorOptions? options = null,
        NotificationDispatcher? notificationDispatcher = null,
        IOptions<ManagerOptions>? managerOptions = null)
    {
        this.jobStore = jobStore;
        this.queueStore = queueStore;
        this.outputStore = outputStore;
        this.backend = backend;
        this.jobLocks = jobLocks;
        this.logger = logger;
        this.options = options ?? new CodexJobSupervisorOptions();
        this.notificationDispatcher = notificationDispatcher;
        this.managerOptions = managerOptions?.Value;
    }

    public async Task<CodexJobSupervisorResult> RecoverActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        var index = await jobStore.RebuildIndexAsync(cancellationToken);
        var activeJobs = index.Jobs
            .Where(ShouldRefresh)
            .ToArray();

        var updated = 0;
        var failed = 0;
        foreach (var job in activeJobs)
        {
            var refreshed = await ResumeJobAsync(job.JobId, cancellationToken);
            if (refreshed is null)
            {
                continue;
            }

            updated++;
            if (refreshed.Status == JobState.Failed)
            {
                failed++;
            }
        }

        return new CodexJobSupervisorResult
        {
            ActiveJobsScanned = activeJobs.Length,
            JobsUpdated = updated,
            JobsFailed = failed
        };
    }

    public async Task<CodexJobSupervisorResult> RefreshActiveJobsOnceAsync(CancellationToken cancellationToken = default)
    {
        var index = await jobStore.ReadIndexAsync(cancellationToken);
        var activeJobs = index.Jobs
            .Where(ShouldRefresh)
            .ToArray();

        var updated = 0;
        var failed = 0;
        foreach (var job in activeJobs)
        {
            var refreshed = await RefreshJobAsync(job.JobId, cancellationToken);
            if (refreshed is null)
            {
                continue;
            }

            updated++;
            if (refreshed.Status == JobState.Failed)
            {
                failed++;
            }
        }

        return new CodexJobSupervisorResult
        {
            ActiveJobsScanned = activeJobs.Length,
            JobsUpdated = updated,
            JobsFailed = failed
        };
    }

    public async Task<CodexJobRecord?> RefreshJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await using var lease = await jobLocks.AcquireAsync(jobId, cancellationToken);
        var job = await jobStore.ReadAsync(jobId, cancellationToken);
        if (job is null)
        {
            return job;
        }

        if (CodexJobRecordUpdater.IsTerminal(job.Status))
        {
            if (job.Status != JobState.Completed)
            {
                return job;
            }

            var updated = await TryDeliverNextQueuedInputLockedAsync(job, cancellationToken);
            await SaveAndNotifyStateChangeAsync(job, updated, cancellationToken);
            return updated;
        }

        return await ObserveOrPollLockedAsync(job, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverActiveJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshActiveJobsOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Codex supervisor refresh failed.");
            }

            await Task.Delay(options.PollInterval, stoppingToken);
        }
    }

    private async Task<CodexJobRecord?> ResumeJobAsync(string jobId, CancellationToken cancellationToken)
    {
        await using var lease = await jobLocks.AcquireAsync(jobId, cancellationToken);
        var job = await jobStore.ReadAsync(jobId, cancellationToken);
        if (job is null)
        {
            return job;
        }

        if (CodexJobRecordUpdater.IsTerminal(job.Status))
        {
            if (job.Status != JobState.Completed)
            {
                return job;
            }

            var updated = await TryDeliverNextQueuedInputLockedAsync(job, cancellationToken);
            await SaveAndNotifyStateChangeAsync(job, updated, cancellationToken);
            return updated;
        }

        if (string.IsNullOrWhiteSpace(job.CodexThreadId))
        {
            return await FailUnrecoverableAsync(job, "active job has no persisted backend thread id", cancellationToken);
        }

        if (!backend.Capabilities.SupportsResume)
        {
            return await FailUnrecoverableAsync(job, "backend does not support resume", cancellationToken);
        }

        try
        {
            var status = await backend.ResumeAsync(new CodexBackendResumeRequest
            {
                JobId = job.JobId,
                Repo = job.Repo,
                BackendIds = CodexJobRecordUpdater.ToBackendIds(job),
                LaunchPolicy = new CodexBackendLaunchPolicy()
            }, cancellationToken);

            transientFailures.Remove(job.JobId);
            var observed = CodexJobRecordUpdater.ApplyStatus(job, status);
            observed = await EnrichLockedAsync(observed, cancellationToken);
            await SaveAndNotifyStateChangeAsync(job, observed, cancellationToken);

            var updated = await TryDeliverNextQueuedInputLockedAsync(observed, cancellationToken);
            await SaveAndNotifyStateChangeAsync(observed, updated, cancellationToken);
            return updated;
        }
        catch (CodexBackendThreadUnrecoverableException exception)
        {
            return await FailUnrecoverableAsync(job, exception.Message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await HandleTransientFailureAsync(job, exception, cancellationToken);
        }
    }

    private async Task<CodexJobRecord> ObserveOrPollLockedAsync(
        CodexJobRecord job,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.CodexThreadId))
        {
            return await FailUnrecoverableAsync(job, "active job has no persisted backend thread id", cancellationToken);
        }

        try
        {
            var request = new CodexBackendObserveRequest
            {
                JobId = job.JobId,
                BackendIds = CodexJobRecordUpdater.ToBackendIds(job)
            };

            CodexBackendStatus status;
            if (backend.Capabilities.SupportsObserveStatus)
            {
                status = await backend.ObserveStatusAsync(request, cancellationToken);
            }
            else if (backend.Capabilities.SupportsStatusPolling)
            {
                status = await backend.PollStatusAsync(request, cancellationToken);
            }
            else
            {
                return await FailUnrecoverableAsync(job, "backend supports neither observation nor polling", cancellationToken);
            }

            transientFailures.Remove(job.JobId);
            var observed = CodexJobRecordUpdater.ApplyStatus(job, status);
            observed = await EnrichLockedAsync(observed, cancellationToken);
            await SaveAndNotifyStateChangeAsync(job, observed, cancellationToken);

            var updated = await TryDeliverNextQueuedInputLockedAsync(observed, cancellationToken);
            await SaveAndNotifyStateChangeAsync(observed, updated, cancellationToken);
            return updated;
        }
        catch (CodexBackendThreadUnrecoverableException exception)
        {
            return await FailUnrecoverableAsync(job, exception.Message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await HandleTransientFailureAsync(job, exception, cancellationToken);
        }
    }

    private async Task<CodexJobRecord> EnrichLockedAsync(
        CodexJobRecord job,
        CancellationToken cancellationToken)
    {
        if (backend.Capabilities.SupportsReadUsage)
        {
            try
            {
                var usage = await backend.ReadUsageAsync(new CodexBackendUsageRequest
                {
                    JobId = job.JobId,
                    BackendIds = CodexJobRecordUpdater.ToBackendIds(job)
                }, cancellationToken);
                if (usage.TokenUsage is not null || usage.RateLimits is not null)
                {
                    job = CodexJobRecordUpdater.ApplyUsage(job, usage);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                job = CodexJobRecordUpdater.ApplyTransientError(job, exception);
                await AppendSupervisorErrorAsync(job.JobId, exception, cancellationToken);
            }
        }

        if (job.Status == JobState.Completed && backend.Capabilities.SupportsReadFinalOutput)
        {
            try
            {
                var output = await backend.ReadFinalOutputAsync(new CodexBackendOutputRequest
                {
                    JobId = job.JobId,
                    BackendIds = CodexJobRecordUpdater.ToBackendIds(job)
                }, cancellationToken);
                job = CodexJobRecordUpdater.ApplyOutput(job, output);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                job = CodexJobRecordUpdater.ApplyTransientError(job, exception);
                await AppendSupervisorErrorAsync(job.JobId, exception, cancellationToken);
            }
        }

        var queue = await queueStore.ReadAsync(job.JobId, cancellationToken);
        return job with { InputQueue = queueStore.CreateSummary(queue) };
    }

    private async Task<CodexJobRecord> TryDeliverNextQueuedInputLockedAsync(
        CodexJobRecord job,
        CancellationToken cancellationToken)
    {
        if (job.Status != JobState.Completed)
        {
            return job;
        }

        var queue = await queueStore.ReadAsync(job.JobId, cancellationToken);
        var pending = queue.Items
            .Where(item => item.Status == QueueItemState.Pending)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.QueueItemId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (pending is null)
        {
            return job with { InputQueue = queueStore.CreateSummary(queue) };
        }

        if (!backend.Capabilities.SupportsSendInput)
        {
            var failed = await MarkQueueDeliveryFailedAsync(
                job,
                pending,
                "backend does not support queued input delivery",
                cancellationToken);
            return failed;
        }

        var attempt = await queueStore.MarkDeliveryAttemptAsync(
            job.JobId,
            pending.QueueItemId,
            cancellationToken: cancellationToken);
        var attemptedItem = attempt.Item ?? pending;
        await outputStore.AppendAsync(new OutputLogEntry
        {
            JobId = job.JobId,
            ThreadId = job.CodexThreadId,
            TurnId = job.CodexTurnId,
            Source = "queue",
            Level = "info",
            Message = $"delivery attempt {attemptedItem.DeliveryAttemptCount} for queued input {attemptedItem.QueueItemId}"
        }, cancellationToken);

        try
        {
            var status = await backend.SendInputAsync(new CodexBackendSendInputRequest
            {
                JobId = job.JobId,
                BackendIds = CodexJobRecordUpdater.ToBackendIds(job),
                Prompt = attemptedItem.Prompt,
                Options = new CodexBackendDispatchOptions
                {
                    Model = job.Model,
                    Effort = job.Effort,
                    FastMode = job.FastMode,
                    ServiceTier = job.ServiceTier ?? "normal"
                },
                LaunchPolicy = new CodexBackendLaunchPolicy()
            }, cancellationToken);
            var delivered = await queueStore.MarkDeliveredAsync(
                job.JobId,
                attemptedItem.QueueItemId,
                cancellationToken: cancellationToken);
            var updated = ApplyQueueDeliveryStatus(job, status) with
            {
                InputQueue = queueStore.CreateSummary(delivered.Queue)
            };
            await outputStore.AppendAsync(new OutputLogEntry
            {
                JobId = job.JobId,
                ThreadId = updated.CodexThreadId,
                TurnId = updated.CodexTurnId,
                Source = "queue",
                Level = "info",
                Message = $"delivered queued input {attemptedItem.QueueItemId}"
            }, cancellationToken);
            return updated;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await MarkQueueDeliveryFailedAsync(job, attemptedItem, exception.Message, cancellationToken);
        }
    }

    private async Task<CodexJobRecord> MarkQueueDeliveryFailedAsync(
        CodexJobRecord job,
        QueueItemRecord item,
        string error,
        CancellationToken cancellationToken)
    {
        var failed = await queueStore.MarkFailedAsync(
            job.JobId,
            item.QueueItemId,
            error,
            cancellationToken: cancellationToken);
        await AppendSupervisorErrorAsync(job.JobId, new InvalidOperationException(error), cancellationToken);
        var updated = job with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            LastError = ProjectionSanitizer.ToSummary(error),
            InputQueue = queueStore.CreateSummary(failed.Queue)
        };
        var failedItem = failed.Item is null ? null : QueueStore.ToSummary(failed.Item);
        if (failedItem is not null && notificationDispatcher is not null)
        {
            await notificationDispatcher.DispatchQueueItemFailedAsync(
                updated,
                failedItem,
                IsChannelEnabled(updated),
                cancellationToken);
        }

        return updated;
    }

    private async Task<CodexJobRecord> HandleTransientFailureAsync(
        CodexJobRecord job,
        Exception exception,
        CancellationToken cancellationToken)
    {
        transientFailures.TryGetValue(job.JobId, out var failures);
        failures++;
        transientFailures[job.JobId] = failures;
        await AppendSupervisorErrorAsync(job.JobId, exception, cancellationToken);

        if (failures >= Math.Max(1, options.MaxTransientFailures))
        {
            return await FailUnrecoverableAsync(
                job,
                $"backend observation failed after {failures} attempts: {exception.Message}",
                cancellationToken);
        }

        var updated = CodexJobRecordUpdater.ApplyTransientError(job, exception);
        await jobStore.SaveAsync(updated, cancellationToken);
        return updated;
    }

    private async Task<CodexJobRecord> FailUnrecoverableAsync(
        CodexJobRecord job,
        string reason,
        CancellationToken cancellationToken)
    {
        transientFailures.Remove(job.JobId);
        var failed = CodexJobRecordUpdater.ApplyUnrecoverableThreadFailure(job, reason);
        await outputStore.AppendAsync(new OutputLogEntry
        {
            JobId = job.JobId,
            ThreadId = job.CodexThreadId,
            TurnId = job.CodexTurnId,
            Source = "supervisor",
            Level = "error",
            Message = failed.LastError ?? "backend_thread_unrecoverable"
        }, cancellationToken);
        await SaveAndNotifyStateChangeAsync(job, failed, cancellationToken);
        return failed;
    }

    private async Task SaveAndNotifyStateChangeAsync(
        CodexJobRecord before,
        CodexJobRecord after,
        CancellationToken cancellationToken)
    {
        await jobStore.SaveAsync(after, cancellationToken);
        if (notificationDispatcher is null)
        {
            return;
        }

        await notificationDispatcher.DispatchJobStateChangeAsync(
            before,
            after,
            IsChannelEnabled(after),
            cancellationToken);
    }

    private Task AppendSupervisorErrorAsync(
        string jobId,
        Exception exception,
        CancellationToken cancellationToken) =>
        outputStore.AppendAsync(new OutputLogEntry
        {
            JobId = jobId,
            Source = "supervisor",
            Level = "warning",
            Message = ProjectionSanitizer.ToSummary(exception.Message)
        }, cancellationToken);

    private static bool ShouldRefresh(JobIndexEntry job) =>
        !CodexJobRecordUpdater.IsTerminal(job.Status)
        || (job.Status == JobState.Completed && job.InputQueue.PendingCount > 0);

    private bool IsChannelEnabled(CodexJobRecord job)
    {
        if (string.Equals(job.NotificationMode, NotificationModes.Channel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(job.NotificationMode, NotificationModes.Disabled, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return managerOptions?.Profiles.TryGetValue(job.Profile, out var profile) == true
            && profile.ChannelNotifications.Enabled;
    }

    private static CodexJobRecord ApplyQueueDeliveryStatus(
        CodexJobRecord job,
        CodexBackendStatus status)
    {
        var nextState = status.WaitingForInput is not null
            ? JobState.WaitingForInput
            : status.State;

        return job with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = nextState,
            CodexThreadId = status.BackendIds.ThreadId ?? job.CodexThreadId,
            CodexTurnId = status.BackendIds.TurnId ?? job.CodexTurnId,
            CodexSessionId = status.BackendIds.SessionId ?? job.CodexSessionId,
            WaitingForInput = nextState == JobState.WaitingForInput ? status.WaitingForInput : null,
            ResultSummary = ProjectionSanitizer.ToOptionalSummary(status.ResultSummary ?? job.ResultSummary, 2048),
            ChangedFiles = status.ChangedFiles.Count > 0 ? status.ChangedFiles : job.ChangedFiles,
            TestSummary = ProjectionSanitizer.ToOptionalSummary(status.TestSummary ?? job.TestSummary, 2048),
            UsageSnapshot = status.UsageSnapshot ?? job.UsageSnapshot,
            LastError = ProjectionSanitizer.ToOptionalSummary(status.LastError)
        };
    }
}
