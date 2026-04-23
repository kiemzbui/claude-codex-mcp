using ClaudeCodexMcp.Backend;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    private readonly Dictionary<string, int> transientFailures = new(StringComparer.Ordinal);

    public CodexJobSupervisor(
        JobStore jobStore,
        QueueStore queueStore,
        OutputStore outputStore,
        ICodexBackend backend,
        CodexJobLockRegistry jobLocks,
        ILogger<CodexJobSupervisor> logger,
        CodexJobSupervisorOptions? options = null)
    {
        this.jobStore = jobStore;
        this.queueStore = queueStore;
        this.outputStore = outputStore;
        this.backend = backend;
        this.jobLocks = jobLocks;
        this.logger = logger;
        this.options = options ?? new CodexJobSupervisorOptions();
    }

    public async Task<CodexJobSupervisorResult> RecoverActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        var index = await jobStore.RebuildIndexAsync(cancellationToken);
        var activeJobs = index.Jobs
            .Where(job => !CodexJobRecordUpdater.IsTerminal(job.Status))
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
            .Where(job => !CodexJobRecordUpdater.IsTerminal(job.Status))
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
        if (job is null || CodexJobRecordUpdater.IsTerminal(job.Status))
        {
            return job;
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
        if (job is null || CodexJobRecordUpdater.IsTerminal(job.Status))
        {
            return job;
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
            var updated = CodexJobRecordUpdater.ApplyStatus(job, status);
            updated = await EnrichLockedAsync(updated, cancellationToken);
            await jobStore.SaveAsync(updated, cancellationToken);
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
            var updated = CodexJobRecordUpdater.ApplyStatus(job, status);
            updated = await EnrichLockedAsync(updated, cancellationToken);
            await jobStore.SaveAsync(updated, cancellationToken);
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
        await jobStore.SaveAsync(failed, cancellationToken);
        return failed;
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
}
