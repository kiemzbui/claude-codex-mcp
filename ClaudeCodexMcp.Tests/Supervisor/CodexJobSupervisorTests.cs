using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using ClaudeCodexMcp.Backend;
using ClaudeCodexMcp.Configuration;
using ClaudeCodexMcp.Discovery;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Notifications;
using ClaudeCodexMcp.Storage;
using ClaudeCodexMcp.Supervisor;
using ClaudeCodexMcp.Tools;
using ClaudeCodexMcp.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClaudeCodexMcp.Tests.Supervisor;

public sealed class CodexJobSupervisorTests
{
    [Fact]
    public async Task StartupRecoveryRebuildsIndexAndResumesActiveJobs()
    {
        using var workspace = SupervisorWorkspace.Create();
        await workspace.SaveJobAsync("job_running", JobState.Running, threadId: "thread-running");
        await workspace.SaveJobAsync("job_waiting", JobState.WaitingForInput, threadId: "thread-waiting");
        await workspace.SaveJobAsync("job_done", JobState.Completed, threadId: "thread-done");
        File.Delete(workspace.Paths.JobIndexPath);
        var backend = new ScriptedSupervisorBackend();
        backend.EnqueueResume(new CodexBackendStatus { State = JobState.Running, BackendIds = new CodexBackendIds { ThreadId = "thread-running" } });
        backend.EnqueueResume(new CodexBackendStatus { State = JobState.Running, BackendIds = new CodexBackendIds { ThreadId = "thread-waiting" } });
        var supervisor = workspace.CreateSupervisor(backend);

        var result = await supervisor.RecoverActiveJobsAsync();

        Assert.True(File.Exists(workspace.Paths.JobIndexPath));
        Assert.Equal(2, result.ActiveJobsScanned);
        Assert.Equal(["job_waiting", "job_running"], backend.ResumeRequests.Select(request => request.JobId).OrderDescending().ToArray());
    }

    [Fact]
    public async Task RefreshUsesEventObservationWhenAvailableAndPollingFallbackOtherwise()
    {
        using var observedWorkspace = SupervisorWorkspace.Create();
        await observedWorkspace.SaveJobAsync("job_observe", JobState.Running, threadId: "thread-observe");
        var observedBackend = new ScriptedSupervisorBackend();
        observedBackend.EnqueueObserve(new CodexBackendStatus { State = JobState.Completed, BackendIds = new CodexBackendIds { ThreadId = "thread-observe" } });

        await observedWorkspace.CreateSupervisor(observedBackend).RefreshActiveJobsOnceAsync();

        Assert.Equal(1, observedBackend.ObserveCount);
        Assert.Equal(0, observedBackend.PollCount);
        Assert.Equal(JobState.Completed, (await observedWorkspace.JobStore.ReadAsync("job_observe"))?.Status);

        using var polledWorkspace = SupervisorWorkspace.Create();
        await polledWorkspace.SaveJobAsync("job_poll", JobState.Running, threadId: "thread-poll");
        var polledBackend = new ScriptedSupervisorBackend(new CodexBackendCapabilities
        {
            BackendId = "poll",
            BackendKind = CodexBackendNames.Fake,
            SupportsStart = true,
            SupportsObserveStatus = false,
            SupportsStatusPolling = true,
            SupportsCancel = true,
            SupportsReadFinalOutput = true,
            SupportsReadUsage = true,
            SupportsResume = true
        });
        polledBackend.EnqueuePoll(new CodexBackendStatus { State = JobState.Completed, BackendIds = new CodexBackendIds { ThreadId = "thread-poll" } });

        await polledWorkspace.CreateSupervisor(polledBackend).RefreshActiveJobsOnceAsync();

        Assert.Equal(0, polledBackend.ObserveCount);
        Assert.Equal(1, polledBackend.PollCount);
        Assert.Equal(JobState.Completed, (await polledWorkspace.JobStore.ReadAsync("job_poll"))?.Status);
    }

    [Fact]
    public async Task ClarificationPromptMovesJobToWaitingForInput()
    {
        using var workspace = SupervisorWorkspace.Create();
        await workspace.SaveJobAsync("job_clarify", JobState.Running, threadId: "thread-clarify");
        var backend = new ScriptedSupervisorBackend();
        backend.EnqueueObserve(new CodexBackendStatus
        {
            State = JobState.Running,
            BackendIds = new CodexBackendIds { ThreadId = "thread-clarify" },
            WaitingForInput = new WaitingForInputRecord
            {
                RequestId = "clarify-1",
                Prompt = "Choose the target test suite.",
                Metadata = new Dictionary<string, string> { ["kind"] = "clarification" }
            }
        });

        await workspace.CreateSupervisor(backend).RefreshActiveJobsOnceAsync();

        var stored = await workspace.JobStore.ReadAsync("job_clarify");
        Assert.Equal(JobState.WaitingForInput, stored?.Status);
        Assert.Equal("clarify-1", stored?.WaitingForInput?.RequestId);
        Assert.Equal("clarification", stored?.WaitingForInput?.Metadata["kind"]);
    }

    [Fact]
    public async Task CompletedRefreshPersistsOutputUsageChangedFilesAndTestSummary()
    {
        using var workspace = SupervisorWorkspace.Create();
        await workspace.SaveJobAsync("job_complete", JobState.Running, threadId: "thread-complete");
        var backend = new ScriptedSupervisorBackend
        {
            Output = new CodexBackendOutput
            {
                Summary = "implemented supervisor",
                ChangedFiles = ["ClaudeCodexMcp/Supervisor/CodexJobSupervisor.cs"],
                TestSummary = "dotnet test passed"
            },
            Usage = new CodexBackendUsageSnapshot
            {
                TokenUsage = new CodexBackendTokenUsage { TotalTokens = 123, ContextWindowTokens = 1000 }
            }
        };
        backend.EnqueueObserve(new CodexBackendStatus
        {
            State = JobState.Completed,
            BackendIds = new CodexBackendIds { ThreadId = "thread-complete" }
        });

        await workspace.CreateSupervisor(backend).RefreshActiveJobsOnceAsync();

        var stored = await workspace.JobStore.ReadAsync("job_complete");
        Assert.Equal(JobState.Completed, stored?.Status);
        Assert.Equal("implemented supervisor", stored?.ResultSummary);
        Assert.Equal(["ClaudeCodexMcp/Supervisor/CodexJobSupervisor.cs"], stored?.ChangedFiles);
        Assert.Equal("dotnet test passed", stored?.TestSummary);
        Assert.Equal(123, stored?.UsageSnapshot?.TokenUsage?.TotalTokens);
    }

    [Fact]
    public async Task TerminalJobsAreNotMovedBackToActiveStates()
    {
        using var workspace = SupervisorWorkspace.Create();
        await workspace.SaveJobAsync("job_done", JobState.Completed, threadId: "thread-done");
        var backend = new ScriptedSupervisorBackend();
        backend.EnqueueObserve(new CodexBackendStatus { State = JobState.Running, BackendIds = new CodexBackendIds { ThreadId = "thread-done" } });

        var result = await workspace.CreateSupervisor(backend).RefreshActiveJobsOnceAsync();

        Assert.Equal(0, result.ActiveJobsScanned);
        Assert.Equal(0, backend.ObserveCount);
        Assert.Equal(JobState.Completed, (await workspace.JobStore.ReadAsync("job_done"))?.Status);
    }

    [Fact]
    public async Task SharedJobLockSerializesSupervisorRefreshAndToolCancellation()
    {
        using var workspace = SupervisorWorkspace.Create();
        var job = await workspace.SaveJobAsync("job_lock", JobState.Running, threadId: "thread-lock");
        var backend = new BlockingSupervisorBackend();
        var locks = new CodexJobLockRegistry();
        var supervisor = workspace.CreateSupervisor(backend, locks);
        var service = workspace.CreateToolService(backend, locks);

        var refreshTask = supervisor.RefreshJobAsync(job.JobId);
        await backend.ObserveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelTask = service.CancelAsync(job.JobId);

        await Task.Delay(50);
        Assert.Equal(0, backend.CancelCount);

        backend.ReleaseObserve.SetResult();
        await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        await cancelTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, backend.CancelCount);
    }

    [Fact]
    public async Task TransientBackendFailuresAreRetriedWithoutTerminalFailure()
    {
        using var workspace = SupervisorWorkspace.Create();
        await workspace.SaveJobAsync("job_retry", JobState.Running, threadId: "thread-retry");
        var backend = new ScriptedSupervisorBackend();
        backend.EnqueueObserveException(new InvalidOperationException("temporary backend disconnect"));
        backend.EnqueueObserve(new CodexBackendStatus { State = JobState.Completed, BackendIds = new CodexBackendIds { ThreadId = "thread-retry" } });
        var supervisor = workspace.CreateSupervisor(backend);

        await supervisor.RefreshActiveJobsOnceAsync();
        var afterFailure = await workspace.JobStore.ReadAsync("job_retry");
        Assert.Equal(JobState.Running, afterFailure?.Status);
        Assert.Contains("temporary backend disconnect", afterFailure?.LastError);

        await supervisor.RefreshActiveJobsOnceAsync();
        Assert.Equal(JobState.Completed, (await workspace.JobStore.ReadAsync("job_retry"))?.Status);
    }

    [Fact]
    public async Task UnrecoverableThreadFailureMarksJobFailedWithStableErrorCode()
    {
        using var workspace = SupervisorWorkspace.Create();
        await workspace.SaveJobAsync("job_unrecoverable", JobState.Running, threadId: "thread-lost");
        var backend = new ScriptedSupervisorBackend();
        backend.EnqueueObserveException(new CodexBackendThreadUnrecoverableException("thread no longer exists"));

        await workspace.CreateSupervisor(backend).RefreshActiveJobsOnceAsync();

        var stored = await workspace.JobStore.ReadAsync("job_unrecoverable");
        Assert.Equal(JobState.Failed, stored?.Status);
        Assert.Contains("backend_thread_unrecoverable", stored?.LastError);
        Assert.Contains("thread no longer exists", stored?.LastError);
    }

    [Fact]
    public async Task QueuedInputIsDeliveredInFifoOrderAfterEachSuccessfulCompletion()
    {
        using var workspace = SupervisorWorkspace.Create();
        await workspace.SaveJobAsync("job_queue_fifo", JobState.Running, threadId: "thread-fifo");
        var first = await workspace.QueuePromptAsync(
            "job_queue_fifo",
            "first queued prompt",
            DateTimeOffset.Parse("2026-04-23T12:01:00Z"));
        var second = await workspace.QueuePromptAsync(
            "job_queue_fifo",
            "second queued prompt",
            DateTimeOffset.Parse("2026-04-23T12:02:00Z"));
        var backend = new ScriptedSupervisorBackend();
        backend.EnqueueObserve(new CodexBackendStatus { State = JobState.Completed, BackendIds = new CodexBackendIds { ThreadId = "thread-fifo" } });
        backend.EnqueueObserve(new CodexBackendStatus { State = JobState.Completed, BackendIds = new CodexBackendIds { ThreadId = "thread-fifo" } });
        var supervisor = workspace.CreateSupervisor(backend);

        await supervisor.RefreshActiveJobsOnceAsync();
        await supervisor.RefreshActiveJobsOnceAsync();

        Assert.Equal(["first queued prompt", "second queued prompt"], backend.SendInputRequests.Select(request => request.Prompt).ToArray());
        var queue = await workspace.QueueStore.ReadAsync("job_queue_fifo");
        Assert.Equal(QueueItemState.Delivered, queue.Items.Single(item => item.QueueItemId == first.QueueItemId).Status);
        Assert.Equal(QueueItemState.Delivered, queue.Items.Single(item => item.QueueItemId == second.QueueItemId).Status);
        Assert.All(queue.Items, item => Assert.Equal(1, item.DeliveryAttemptCount));
        var stored = await workspace.JobStore.ReadAsync("job_queue_fifo");
        Assert.Equal(0, stored?.InputQueue.PendingCount);
        Assert.Equal(2, stored?.InputQueue.DeliveredCount);
    }

    [Theory]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Cancelled)]
    [InlineData(JobState.WaitingForInput)]
    public async Task QueuedInputStaysPendingWhenActiveTurnDoesNotCompleteSuccessfully(JobState nextState)
    {
        using var workspace = SupervisorWorkspace.Create();
        await workspace.SaveJobAsync($"job_pending_{nextState}", JobState.Running, threadId: $"thread-{nextState}");
        var item = await workspace.QueuePromptAsync($"job_pending_{nextState}", "queued prompt", DateTimeOffset.Parse("2026-04-23T12:01:00Z"));
        var backend = new ScriptedSupervisorBackend();
        backend.EnqueueObserve(new CodexBackendStatus
        {
            State = nextState,
            BackendIds = new CodexBackendIds { ThreadId = $"thread-{nextState}" },
            WaitingForInput = nextState == JobState.WaitingForInput
                ? new WaitingForInputRecord { RequestId = "clarify", Prompt = "Need clarification." }
                : null
        });

        await workspace.CreateSupervisor(backend).RefreshActiveJobsOnceAsync();

        Assert.Empty(backend.SendInputRequests);
        var queue = await workspace.QueueStore.ReadAsync($"job_pending_{nextState}");
        Assert.Equal(QueueItemState.Pending, queue.Items.Single(itemRecord => itemRecord.QueueItemId == item.QueueItemId).Status);
        var stored = await workspace.JobStore.ReadAsync($"job_pending_{nextState}");
        Assert.Equal(nextState, stored?.Status);
        Assert.Equal(1, stored?.InputQueue.PendingCount);
    }

    [Fact]
    public async Task QueuedDeliveryFailureIsPersistedAndLaterItemsRemainPending()
    {
        using var workspace = SupervisorWorkspace.Create();
        await workspace.SaveJobAsync("job_queue_fail", JobState.Running, threadId: "thread-fail");
        var first = await workspace.QueuePromptAsync("job_queue_fail", "first queued prompt", DateTimeOffset.Parse("2026-04-23T12:01:00Z"));
        var second = await workspace.QueuePromptAsync("job_queue_fail", "second queued prompt", DateTimeOffset.Parse("2026-04-23T12:02:00Z"));
        var backend = new ScriptedSupervisorBackend();
        backend.EnqueueObserve(new CodexBackendStatus { State = JobState.Completed, BackendIds = new CodexBackendIds { ThreadId = "thread-fail" } });
        backend.EnqueueSendInputException(new InvalidOperationException("send input failed"));

        await workspace.CreateSupervisor(backend).RefreshActiveJobsOnceAsync();

        var queue = await workspace.QueueStore.ReadAsync("job_queue_fail");
        var failed = queue.Items.Single(item => item.QueueItemId == first.QueueItemId);
        Assert.Equal(QueueItemState.Failed, failed.Status);
        Assert.Equal(1, failed.DeliveryAttemptCount);
        Assert.Contains("send input failed", failed.LastError);
        Assert.Equal(QueueItemState.Pending, queue.Items.Single(item => item.QueueItemId == second.QueueItemId).Status);
        var stored = await workspace.JobStore.ReadAsync("job_queue_fail");
        Assert.Equal(1, stored?.InputQueue.FailedCount);
        Assert.Equal(1, stored?.InputQueue.PendingCount);
    }

    [Fact]
    public async Task SupervisorEmitsLifecycleNotificationForObservedTerminalState()
    {
        using var workspace = SupervisorWorkspace.Create();
        await workspace.SaveJobAsync(
            "job_notify_completed",
            JobState.Running,
            threadId: "thread-notify",
            notificationMode: NotificationModes.Channel);
        var transport = new RecordingClaudeChannelTransport();
        var dispatcher = workspace.CreateNotificationDispatcher(transport);
        var backend = new ScriptedSupervisorBackend();
        backend.EnqueueObserve(new CodexBackendStatus
        {
            State = JobState.Completed,
            BackendIds = new CodexBackendIds { ThreadId = "thread-notify" }
        });

        await workspace.CreateSupervisor(backend, notificationDispatcher: dispatcher).RefreshActiveJobsOnceAsync();

        var stored = await workspace.JobStore.ReadAsync("job_notify_completed");
        Assert.Equal(JobState.Completed, stored?.Status);
        var records = await workspace.NotificationStore.ReadAsync("job_notify_completed");
        Assert.Contains(records, record =>
            record.EventName == NotificationEventNames.JobCompleted &&
            record.DeliveryState == NotificationDeliveryState.Attempted);
        Assert.Contains(records, record =>
            record.EventName == NotificationEventNames.JobCompleted &&
            record.DeliveryState == NotificationDeliveryState.Delivered);
        Assert.Single(transport.Payloads);
    }

    [Fact]
    public async Task SupervisorEmitsQueueItemFailedNotificationWithoutChangingLifecycleState()
    {
        using var workspace = SupervisorWorkspace.Create();
        await workspace.SaveJobAsync(
            "job_queue_notify_fail",
            JobState.Running,
            threadId: "thread-queue-notify",
            notificationMode: NotificationModes.Channel);
        var first = await workspace.QueuePromptAsync(
            "job_queue_notify_fail",
            "queued prompt token=must-not-notify",
            DateTimeOffset.Parse("2026-04-23T12:01:00Z"));
        var transport = new RecordingClaudeChannelTransport();
        var dispatcher = workspace.CreateNotificationDispatcher(transport);
        var backend = new ScriptedSupervisorBackend();
        backend.EnqueueObserve(new CodexBackendStatus
        {
            State = JobState.Completed,
            BackendIds = new CodexBackendIds { ThreadId = "thread-queue-notify" }
        });
        backend.EnqueueSendInputException(new InvalidOperationException("queued delivery token=queue-failure-secret failed"));

        await workspace.CreateSupervisor(backend, notificationDispatcher: dispatcher).RefreshActiveJobsOnceAsync();

        var stored = await workspace.JobStore.ReadAsync("job_queue_notify_fail");
        Assert.Equal(JobState.Completed, stored?.Status);
        var queue = await workspace.QueueStore.ReadAsync("job_queue_notify_fail");
        Assert.Equal(QueueItemState.Failed, queue.Items.Single(item => item.QueueItemId == first.QueueItemId).Status);
        var records = await workspace.NotificationStore.ReadAsync("job_queue_notify_fail");
        Assert.Contains(records, record => record.EventName == NotificationEventNames.QueueItemFailed);
        Assert.All(transport.Payloads, payload =>
        {
            Assert.DoesNotContain("must-not-notify", payload);
            Assert.DoesNotContain("queue-failure-secret", payload);
        });
    }

    [Fact]
    public async Task ChannelFailureIsNotLifecycleFailure()
    {
        using var workspace = SupervisorWorkspace.Create();
        await workspace.SaveJobAsync(
            "job_channel_fail",
            JobState.Running,
            threadId: "thread-channel-fail",
            notificationMode: NotificationModes.Channel);
        var dispatcher = workspace.CreateNotificationDispatcher(new RecordingClaudeChannelTransport
        {
            Failure = "channel failed"
        });
        var backend = new ScriptedSupervisorBackend();
        backend.EnqueueObserve(new CodexBackendStatus
        {
            State = JobState.Completed,
            BackendIds = new CodexBackendIds { ThreadId = "thread-channel-fail" }
        });

        await workspace.CreateSupervisor(backend, notificationDispatcher: dispatcher).RefreshActiveJobsOnceAsync();

        var stored = await workspace.JobStore.ReadAsync("job_channel_fail");
        Assert.Equal(JobState.Completed, stored?.Status);
        var records = await workspace.NotificationStore.ReadAsync("job_channel_fail");
        Assert.Contains(records, record => record.DeliveryState == NotificationDeliveryState.Failed);
        Assert.DoesNotContain(records, record => record.EventName == NotificationEventNames.JobFailed);
    }

    [Fact]
    public async Task SupervisorRestartRecoveryDeliversPendingQueueForCompletedJob()
    {
        using var workspace = SupervisorWorkspace.Create();
        await workspace.SaveJobAsync("job_queue_restart", JobState.Completed, threadId: "thread-restart");
        await workspace.QueuePromptAsync("job_queue_restart", "queued after completed turn", DateTimeOffset.Parse("2026-04-23T12:01:00Z"));
        File.Delete(workspace.Paths.JobIndexPath);
        var backend = new ScriptedSupervisorBackend();

        var result = await workspace.CreateSupervisor(backend).RecoverActiveJobsAsync();

        Assert.Equal(1, result.ActiveJobsScanned);
        Assert.Empty(backend.ResumeRequests);
        Assert.Single(backend.SendInputRequests);
        Assert.Equal("queued after completed turn", backend.SendInputRequests.Single().Prompt);
        var stored = await workspace.JobStore.ReadAsync("job_queue_restart");
        Assert.Equal(JobState.Running, stored?.Status);
        Assert.Equal(1, stored?.InputQueue.DeliveredCount);
    }

    private sealed class SupervisorWorkspace : IDisposable
    {
        private SupervisorWorkspace(string root)
        {
            Root = root;
            RepoRoot = Path.Combine(root, "repo");
            Directory.CreateDirectory(RepoRoot);
            Paths = new ManagerStatePaths(Path.Combine(root, ".codex-manager"));
            QueueStore = new QueueStore(Paths);
            JobStore = new JobStore(Paths);
            OutputStore = new OutputStore(Paths);
            NotificationStore = new NotificationStore(Paths);
        }

        public string Root { get; }

        public string RepoRoot { get; }

        public ManagerStatePaths Paths { get; }

        public JobStore JobStore { get; }

        public QueueStore QueueStore { get; }

        public OutputStore OutputStore { get; }

        public NotificationStore NotificationStore { get; }

        public static SupervisorWorkspace Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "claude-codex-mcp-supervisor-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new SupervisorWorkspace(root);
        }

        public async Task<CodexJobRecord> SaveJobAsync(
            string jobId,
            JobState state,
            string? threadId,
            string notificationMode = NotificationModes.Disabled)
        {
            var timestamp = DateTimeOffset.Parse("2026-04-23T12:00:00Z");
            var job = new CodexJobRecord
            {
                JobId = jobId,
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                Profile = "implementation",
                Workflow = CanonicalWorkflows.Direct,
                Repo = RepoRoot,
                Title = $"Title {jobId}",
                Status = state,
                PromptSummary = $"Prompt {jobId}",
                CodexThreadId = threadId,
                CodexTurnId = threadId is null ? null : $"turn-{jobId}",
                CodexSessionId = threadId is null ? null : $"session-{jobId}",
                Model = "gpt-5.4",
                Effort = "medium",
                ServiceTier = "normal",
                LogPath = Paths.GetRelativeLogPath(jobId),
                InputQueue = QueueStore.CreateEmptySummary(jobId),
                NotificationMode = notificationMode,
                NotificationLogPath = Paths.GetRelativeNotificationLogPath(jobId)
            };
            await JobStore.SaveAsync(job);
            return job;
        }

        public async Task<QueueItemRecord> QueuePromptAsync(string jobId, string prompt, DateTimeOffset createdAt)
        {
            var item = await QueueStore.AddAsync(jobId, prompt, createdAt: createdAt);
            var job = await JobStore.ReadAsync(jobId);
            if (job is not null)
            {
                var queue = await QueueStore.ReadAsync(jobId);
                await JobStore.SaveAsync(job with { InputQueue = QueueStore.CreateSummary(queue) });
            }

            return item;
        }

        public CodexJobSupervisor CreateSupervisor(
            ICodexBackend backend,
            CodexJobLockRegistry? locks = null,
            CodexJobSupervisorOptions? options = null,
            NotificationDispatcher? notificationDispatcher = null) =>
            new(
                JobStore,
                QueueStore,
                OutputStore,
                backend,
                locks ?? new CodexJobLockRegistry(),
                NullLogger<CodexJobSupervisor>.Instance,
                options ?? new CodexJobSupervisorOptions { PollInterval = TimeSpan.FromMilliseconds(10) },
                notificationDispatcher);

        public CodexToolService CreateToolService(ICodexBackend backend, CodexJobLockRegistry locks)
        {
            var managerOptions = new ManagerOptions
            {
                Profiles = new Dictionary<string, ProfileOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["implementation"] = new()
                    {
                        Repo = RepoRoot,
                        AllowedRepos = [RepoRoot],
                        Backend = "fake",
                        ReadOnly = true,
                        DefaultWorkflow = CanonicalWorkflows.Direct,
                        AllowedWorkflows = [CanonicalWorkflows.Direct],
                        DefaultModel = "gpt-5.4",
                        DefaultEffort = "medium"
                    }
                }
            };
            var options = Options.Create(managerOptions);
            var discovery = new CodexCapabilityDiscovery(
                new DiscoveryCacheStore(Paths),
                new CodexDiscoveryOptions { CodexHome = Path.Combine(Root, "codex-home"), RepoRoot = RepoRoot });

            return new CodexToolService(
                options,
                new ProfilePolicyValidator(options),
                JobStore,
                QueueStore,
                backend,
                discovery,
                locks);
        }

        public NotificationDispatcher CreateNotificationDispatcher(IClaudeChannelTransport transport) =>
            new(
                NotificationStore,
                new ClaudeChannelNotifier(transport),
                NullLogger<NotificationDispatcher>.Instance);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private class ScriptedSupervisorBackend : ICodexBackend
    {
        private readonly ConcurrentQueue<object> observeResults = new();
        private readonly ConcurrentQueue<object> pollResults = new();
        private readonly ConcurrentQueue<object> resumeResults = new();
        private readonly ConcurrentQueue<object> sendInputResults = new();

        public ScriptedSupervisorBackend(CodexBackendCapabilities? capabilities = null)
        {
            Capabilities = capabilities ?? new CodexBackendCapabilities
            {
                BackendId = "scripted",
                BackendKind = CodexBackendNames.Fake,
                SupportsStart = true,
                SupportsObserveStatus = true,
                SupportsStatusPolling = true,
                SupportsSendInput = true,
                SupportsCancel = true,
                SupportsReadFinalOutput = true,
                SupportsReadUsage = true,
                SupportsResume = true
            };
        }

        public CodexBackendCapabilities Capabilities { get; }

        public int ObserveCount { get; private set; }

        public int PollCount { get; private set; }

        public int CancelCount { get; protected set; }

        public List<CodexBackendResumeRequest> ResumeRequests { get; } = [];

        public List<CodexBackendSendInputRequest> SendInputRequests { get; } = [];

        public CodexBackendOutput Output { get; init; } = new();

        public CodexBackendUsageSnapshot Usage { get; init; } = new();

        public void EnqueueObserve(CodexBackendStatus status) => observeResults.Enqueue(status);

        public void EnqueuePoll(CodexBackendStatus status) => pollResults.Enqueue(status);

        public void EnqueueResume(CodexBackendStatus status) => resumeResults.Enqueue(status);

        public void EnqueueObserveException(Exception exception) => observeResults.Enqueue(exception);

        public void EnqueueSendInput(CodexBackendStatus status) => sendInputResults.Enqueue(status);

        public void EnqueueSendInputException(Exception exception) => sendInputResults.Enqueue(exception);

        public Task<CodexBackendStartResult> StartAsync(
            CodexBackendStartRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexBackendStartResult());

        public virtual Task<CodexBackendStatus> ObserveStatusAsync(
            CodexBackendObserveRequest request,
            CancellationToken cancellationToken = default)
        {
            ObserveCount++;
            return ResultFromQueueAsync(observeResults, request.BackendIds);
        }

        public Task<CodexBackendStatus> PollStatusAsync(
            CodexBackendObserveRequest request,
            CancellationToken cancellationToken = default)
        {
            PollCount++;
            return ResultFromQueueAsync(pollResults, request.BackendIds);
        }

        public Task<CodexBackendStatus> SendInputAsync(
            CodexBackendSendInputRequest request,
            CancellationToken cancellationToken = default)
        {
            SendInputRequests.Add(request);
            return ResultFromQueueAsync(sendInputResults, request.BackendIds);
        }

        public virtual Task<CodexBackendStatus> CancelAsync(
            CodexBackendCancelRequest request,
            CancellationToken cancellationToken = default)
        {
            CancelCount++;
            return Task.FromResult(new CodexBackendStatus { State = JobState.Cancelled, BackendIds = request.BackendIds });
        }

        public Task<CodexBackendOutput> ReadFinalOutputAsync(
            CodexBackendOutputRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Output with { BackendIds = request.BackendIds });

        public Task<CodexBackendUsageSnapshot> ReadUsageAsync(
            CodexBackendUsageRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Usage);

        public Task<CodexBackendStatus> ResumeAsync(
            CodexBackendResumeRequest request,
            CancellationToken cancellationToken = default)
        {
            ResumeRequests.Add(request);
            return ResultFromQueueAsync(resumeResults, request.BackendIds);
        }

        private static Task<CodexBackendStatus> ResultFromQueueAsync(
            ConcurrentQueue<object> queue,
            CodexBackendIds ids)
        {
            if (!queue.TryDequeue(out var next))
            {
                return Task.FromResult(new CodexBackendStatus { State = JobState.Running, BackendIds = ids });
            }

            if (next is Exception exception)
            {
                return Task.FromException<CodexBackendStatus>(exception);
            }

            return Task.FromResult((CodexBackendStatus)next);
        }
    }

    private sealed class BlockingSupervisorBackend : ScriptedSupervisorBackend
    {
        public TaskCompletionSource ObserveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseObserve { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<CodexBackendStatus> ObserveStatusAsync(
            CodexBackendObserveRequest request,
            CancellationToken cancellationToken = default)
        {
            ObserveStarted.TrySetResult();
            await ReleaseObserve.Task.WaitAsync(cancellationToken);
            return new CodexBackendStatus { State = JobState.Running, BackendIds = request.BackendIds };
        }
    }

    private sealed class RecordingClaudeChannelTransport : IClaudeChannelTransport
    {
        public List<string> Payloads { get; } = [];

        public string? Failure { get; init; }

        public Task<ClaudeChannelDeliveryResult> SendAsync(
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            Payloads.Add(payloadJson);
            return Task.FromResult(Failure is null
                ? ClaudeChannelDeliveryResult.Success()
                : ClaudeChannelDeliveryResult.Failure(Failure));
        }
    }
}
