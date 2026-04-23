using ClaudeCodexMcp.Backend;
using ClaudeCodexMcp.Configuration;
using ClaudeCodexMcp.Discovery;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Storage;
using ClaudeCodexMcp.Supervisor;
using ClaudeCodexMcp.Tools;
using ClaudeCodexMcp.Workflows;
using Microsoft.Extensions.Options;

namespace ClaudeCodexMcp.Tests.Tools;

public sealed class CodexToolServiceTests
{
    [Fact]
    public void ListProfilesReturnsCompactPolicySummary()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var service = workspace.CreateService();

        var result = service.ListProfiles();

        var profile = Assert.Single(result.Profiles);
        Assert.Equal("implementation", profile.Name);
        Assert.Equal("Use repo instructions.", profile.TaskPrefix);
        Assert.Equal("fake", profile.Backend);
        Assert.True(profile.ReadOnly);
        Assert.False(profile.ChannelNotifications);
        Assert.Equal("gpt-5.4", profile.DefaultModel);
        Assert.Equal("medium", profile.DefaultEffort);
        Assert.False(profile.FastMode);
        Assert.Equal("normal", profile.DefaultServiceTier);
        Assert.Contains("sandbox", profile.Permissions.Keys);
        Assert.DoesNotContain(profile.Permissions.Keys, key => key.Contains("command", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartTaskEnforcesTitlePersistsBeforeBackendAndReturnsCompactStatus()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var backend = new InspectingBackend(workspace.StateDirectory);
        var service = workspace.CreateService(backend);

        var rejected = await service.StartTaskAsync("implementation", "direct", " ", workspace.RepoRoot, "Prompt");

        Assert.False(rejected.Accepted);
        Assert.Contains(rejected.Errors, error => error.Code == "blank_title");
        Assert.Empty(backend.StartRequests);

        var accepted = await service.StartTaskAsync("implementation", "direct", "Do work", workspace.RepoRoot, "Prompt");

        Assert.True(accepted.Accepted);
        Assert.NotNull(accepted.Job);
        Assert.Equal(JobState.Running, accepted.Job.Status);
        Assert.True(backend.SawDurableJobBeforeStart);
        Assert.Single(backend.StartRequests);
        Assert.Equal("Prompt", backend.StartRequests.Single().Prompt.Split(Environment.NewLine).Last());
    }

    [Fact]
    public async Task StartTaskRejectsPolicyViolationsBeforeBackendSideEffects()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var backend = new InspectingBackend(workspace.StateDirectory);
        var service = workspace.CreateService(backend);

        var result = await service.StartTaskAsync(
            "implementation",
            CanonicalWorkflows.OrchestrateExecute,
            "Rejected",
            workspace.RepoRoot,
            "Prompt");

        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, error => error.Code == "workflow_not_allowed");
        Assert.Empty(backend.StartRequests);
    }

    [Fact]
    public async Task StatusWaitCapsTimeoutAndIncludesStructuredWaitingRequest()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var backend = new InspectingBackend(workspace.StateDirectory);
        backend.NextObserveStatus = new CodexBackendStatus
        {
            State = JobState.WaitingForInput,
            BackendIds = new CodexBackendIds { ThreadId = "thread-wait" },
            WaitingForInput = new WaitingForInputRecord
            {
                RequestId = "clarify-1",
                Prompt = "Choose one.",
                Metadata = new Dictionary<string, string> { ["kind"] = "clarification" }
            }
        };
        var service = workspace.CreateService(backend);
        var start = await service.StartTaskAsync("implementation", "direct", "Needs input", workspace.RepoRoot, "Prompt");

        var status = await service.StatusAsync(start.Job!.JobId, wait: true, timeoutSeconds: 999);

        Assert.Equal(25, status.WaitTimeoutSeconds);
        Assert.Equal(JobState.WaitingForInput, status.Job?.Status);
        Assert.Equal("clarify-1", status.Job?.WaitingForInput?.RequestId);
        Assert.Equal("clarification", status.Job?.WaitingForInput?.Metadata["kind"]);
    }

    [Fact]
    public async Task ListJobsReturnsReconnectableCompactJobs()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var service = workspace.CreateService();
        var start = await service.StartTaskAsync("implementation", "direct", "Reconnect me", workspace.RepoRoot, "Prompt");

        var jobs = await service.ListJobsAsync();

        Assert.Contains(jobs.Jobs, job => job.JobId == start.Job!.JobId && job.Title == "Reconnect me");
        Assert.All(jobs.Jobs, job => Assert.False(string.IsNullOrWhiteSpace(job.LogRef)));
    }

    [Fact]
    public async Task ContinuationOverridesArePersistedAndPassedToBackendAfterValidation()
    {
        using var workspace = TemporaryToolWorkspace.Create(allowOverrides: true);
        var backend = new InspectingBackend(workspace.StateDirectory);
        var service = workspace.CreateService(backend);
        var start = await service.StartTaskAsync("implementation", "direct", "Continue", workspace.RepoRoot, "Prompt");

        var response = await service.SendInputAsync(
            start.Job!.JobId,
            "Follow up",
            model: "gpt-5.4-codex",
            effort: "high",
            fastMode: true);

        Assert.True(response.Accepted);
        Assert.Equal("gpt-5.4-codex", response.Job?.Model);
        Assert.Equal("high", response.Job?.Effort);
        Assert.True(response.Job?.FastMode);
        Assert.Equal("fast", response.Job?.ServiceTier);
        var request = Assert.Single(backend.SendInputRequests);
        Assert.Equal("gpt-5.4-codex", request.Options.Model);
        Assert.Equal("high", request.Options.Effort);
        Assert.True(request.Options.FastMode);

        var stored = await workspace.JobStore.ReadAsync(start.Job.JobId);
        Assert.Equal("gpt-5.4-codex", stored?.Model);
        Assert.Equal("high", stored?.Effort);
        Assert.True(stored?.FastMode);
    }

    [Fact]
    public async Task ContinuationOverrideRejectionHappensBeforeBackendSideEffects()
    {
        using var workspace = TemporaryToolWorkspace.Create(allowOverrides: false);
        var backend = new InspectingBackend(workspace.StateDirectory);
        var service = workspace.CreateService(backend);
        var start = await service.StartTaskAsync("implementation", "direct", "Reject continuation", workspace.RepoRoot, "Prompt");

        var response = await service.SendInputAsync(
            start.Job!.JobId,
            "Follow up",
            model: "gpt-5.4-codex",
            effort: "high",
            fastMode: true);

        Assert.False(response.Accepted);
        Assert.Contains(response.Errors, error => error.Code == "model_override_disallowed");
        Assert.Contains(response.Errors, error => error.Code == "effort_override_disallowed");
        Assert.Contains(response.Errors, error => error.Code == "fast_mode_override_disallowed");
        Assert.Empty(backend.SendInputRequests);
    }

    [Fact]
    public async Task ResultDoesNotIncludeFullOutputByDefault()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var backend = new InspectingBackend(workspace.StateDirectory)
        {
            StartStatus = new CodexBackendStatus
            {
                State = JobState.Completed,
                BackendIds = new CodexBackendIds { ThreadId = "thread-complete" }
            },
            Output = new CodexBackendOutput
            {
                Summary = "compact summary",
                FinalText = "FULL_OUTPUT_SECRET_" + new string('x', 1000)
            }
        };
        var service = workspace.CreateService(backend);
        var start = await service.StartTaskAsync("implementation", "direct", "Result", workspace.RepoRoot, "Prompt");

        var result = await service.ResultAsync(start.Job!.JobId);

        Assert.False(result.FullOutputIncluded);
        Assert.Equal("compact summary", result.Summary);
        Assert.DoesNotContain("FULL_OUTPUT_SECRET", result.Job?.ResultSummary ?? string.Empty);
    }

    [Fact]
    public async Task QueueInputPersistsPromptReportsPositionAndQueueCounts()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var service = workspace.CreateService();
        var start = await service.StartTaskAsync("implementation", "direct", "Queue", workspace.RepoRoot, "Prompt");

        var first = await service.QueueInputAsync(start.Job!.JobId, "first queued prompt secret=QUEUE_BODY", "First");
        var second = await service.QueueInputAsync(start.Job.JobId, "second queued prompt", "Second");

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(1, first.QueuePosition);
        Assert.Equal(2, second.QueuePosition);
        Assert.Equal(2, second.Job?.InputQueue.PendingCount);
        Assert.Equal(first.QueueItem?.QueueItemId, second.Job?.InputQueue.NextQueueItemId);
        var queue = await workspace.QueueStore.ReadAsync(start.Job.JobId);
        Assert.Equal("first queued prompt secret=QUEUE_BODY", queue.Items.First().Prompt);
        var stored = await workspace.JobStore.ReadAsync(start.Job.JobId);
        Assert.Equal(2, stored?.InputQueue.PendingCount);
        Assert.DoesNotContain("QUEUE_BODY", stored?.InputQueue.Items.First().PromptSummary ?? string.Empty);
    }

    [Fact]
    public async Task CancelQueuedInputCancelsPendingOnlyAndDoesNotCancelActiveJob()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var backend = new InspectingBackend(workspace.StateDirectory);
        var service = workspace.CreateService(backend);
        var start = await service.StartTaskAsync("implementation", "direct", "Cancel queue", workspace.RepoRoot, "Prompt");
        var queued = await service.QueueInputAsync(start.Job!.JobId, "queued prompt");

        var cancelled = await service.CancelQueuedInputAsync(start.Job.JobId, queued.QueueItem!.QueueItemId);

        Assert.True(cancelled.Accepted);
        Assert.Empty(backend.CancelRequests);
        Assert.Equal(JobState.Running, cancelled.Job?.Status);
        Assert.Equal(1, cancelled.Job?.InputQueue.CancelledCount);
        Assert.Equal(0, cancelled.Job?.InputQueue.PendingCount);
        var stored = await workspace.QueueStore.ReadAsync(start.Job.JobId);
        Assert.Equal(QueueItemState.Cancelled, stored.Items.Single().Status);
    }

    [Theory]
    [InlineData(QueueItemState.Delivered)]
    [InlineData(QueueItemState.Failed)]
    [InlineData(QueueItemState.Cancelled)]
    public async Task CancelQueuedInputRejectsNonPendingItems(QueueItemState state)
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var service = workspace.CreateService();
        var start = await service.StartTaskAsync("implementation", "direct", $"Reject {state}", workspace.RepoRoot, "Prompt");
        var queued = await service.QueueInputAsync(start.Job!.JobId, "queued prompt");

        if (state == QueueItemState.Delivered)
        {
            await workspace.QueueStore.MarkDeliveredAsync(start.Job.JobId, queued.QueueItem!.QueueItemId);
        }
        else if (state == QueueItemState.Failed)
        {
            await workspace.QueueStore.MarkFailedAsync(start.Job.JobId, queued.QueueItem!.QueueItemId, "delivery failed");
        }
        else
        {
            await workspace.QueueStore.CancelPendingAsync(start.Job.JobId, queued.QueueItem!.QueueItemId);
        }

        var rejected = await service.CancelQueuedInputAsync(start.Job.JobId, queued.QueueItem!.QueueItemId);

        Assert.False(rejected.Accepted);
        Assert.Contains(rejected.Errors, error => error.Code == "queue_item_not_pending");
        Assert.Equal(state, rejected.QueueItem?.Status);
    }

    private sealed class TemporaryToolWorkspace : IDisposable
    {
        private TemporaryToolWorkspace(string root, bool allowOverrides)
        {
            Root = root;
            RepoRoot = Path.Combine(root, "repo");
            StateDirectory = Path.Combine(root, ".codex-manager");
            Directory.CreateDirectory(RepoRoot);
            Paths = new ManagerStatePaths(StateDirectory);
            JobStore = new JobStore(Paths);
            QueueStore = new QueueStore(Paths);
            Options = CreateOptions(RepoRoot, allowOverrides);
        }

        public string Root { get; }

        public string RepoRoot { get; }

        public string StateDirectory { get; }

        public ManagerStatePaths Paths { get; }

        public JobStore JobStore { get; }

        public QueueStore QueueStore { get; }

        public ManagerOptions Options { get; }

        public static TemporaryToolWorkspace Create(bool allowOverrides = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "claude-codex-mcp-tool-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TemporaryToolWorkspace(root, allowOverrides);
        }

        public CodexToolService CreateService(ICodexBackend? backend = null)
        {
            var options = Microsoft.Extensions.Options.Options.Create(Options);
            var policyValidator = new ProfilePolicyValidator(options);
            var discovery = new CodexCapabilityDiscovery(
                new DiscoveryCacheStore(Paths),
                new CodexDiscoveryOptions { CodexHome = Path.Combine(Root, "codex-home"), RepoRoot = RepoRoot });

            return new CodexToolService(
                options,
                policyValidator,
                JobStore,
                QueueStore,
                backend ?? new InspectingBackend(StateDirectory),
                discovery,
                new CodexJobLockRegistry());
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static ManagerOptions CreateOptions(string repoRoot, bool allowOverrides)
        {
            var profile = new ProfileOptions
            {
                Repo = repoRoot,
                AllowedRepos = [repoRoot],
                TaskPrefix = "Use repo instructions.",
                Backend = "fake",
                ReadOnly = true,
                Permissions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sandbox"] = "read-only",
                    ["approvalPolicy"] = "never"
                },
                DefaultWorkflow = CanonicalWorkflows.Direct,
                AllowedWorkflows = [CanonicalWorkflows.Direct],
                ChannelNotifications = new ChannelNotificationOptions { Enabled = false },
                DefaultModel = "gpt-5.4",
                AllowedModels = ["gpt-5.4", "gpt-5.4-codex"],
                AllowModelOverride = allowOverrides,
                DefaultEffort = CodexEfforts.Medium,
                AllowedEfforts = [CodexEfforts.Medium, CodexEfforts.High],
                AllowEffortOverride = allowOverrides,
                AllowFastModeOverride = allowOverrides
            };

            return new ManagerOptions
            {
                Profiles = new Dictionary<string, ProfileOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["implementation"] = profile
                }
            };
        }
    }

    private sealed class InspectingBackend : ICodexBackend
    {
        private readonly string stateDirectory;

        public InspectingBackend(string stateDirectory)
        {
            this.stateDirectory = stateDirectory;
        }

        public CodexBackendCapabilities Capabilities { get; } = new()
        {
            BackendId = "fake",
            BackendKind = "fake",
            SupportsStart = true,
            SupportsObserveStatus = true,
            SupportsStatusPolling = true,
            SupportsSendInput = true,
            SupportsCancel = true,
            SupportsReadFinalOutput = true,
            SupportsReadUsage = true,
            SupportsResume = true
        };

        public List<CodexBackendStartRequest> StartRequests { get; } = [];

        public List<CodexBackendSendInputRequest> SendInputRequests { get; } = [];

        public List<CodexBackendCancelRequest> CancelRequests { get; } = [];

        public bool SawDurableJobBeforeStart { get; private set; }

        public CodexBackendStatus StartStatus { get; init; } = new()
        {
            State = JobState.Running,
            BackendIds = new CodexBackendIds { ThreadId = "thread-start", TurnId = "turn-start", SessionId = "session-start" }
        };

        public CodexBackendStatus? NextObserveStatus { get; set; }

        public CodexBackendOutput Output { get; init; } = new()
        {
            Summary = "summary",
            FinalText = "final"
        };

        public Task<CodexBackendStartResult> StartAsync(
            CodexBackendStartRequest request,
            CancellationToken cancellationToken = default)
        {
            SawDurableJobBeforeStart = File.Exists(Path.Combine(stateDirectory, "jobs", $"{request.JobId}.json"));
            StartRequests.Add(request);
            return Task.FromResult(new CodexBackendStartResult { Status = StartStatus });
        }

        public Task<CodexBackendStatus> ObserveStatusAsync(
            CodexBackendObserveRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(NextObserveStatus ?? new CodexBackendStatus
            {
                State = JobState.Running,
                BackendIds = request.BackendIds
            });

        public Task<CodexBackendStatus> PollStatusAsync(
            CodexBackendObserveRequest request,
            CancellationToken cancellationToken = default) =>
            ObserveStatusAsync(request, cancellationToken);

        public Task<CodexBackendStatus> SendInputAsync(
            CodexBackendSendInputRequest request,
            CancellationToken cancellationToken = default)
        {
            SendInputRequests.Add(request);
            return Task.FromResult(new CodexBackendStatus
            {
                State = JobState.Running,
                BackendIds = request.BackendIds
            });
        }

        public Task<CodexBackendStatus> CancelAsync(
            CodexBackendCancelRequest request,
            CancellationToken cancellationToken = default)
        {
            CancelRequests.Add(request);
            return Task.FromResult(new CodexBackendStatus
            {
                State = JobState.Cancelled,
                BackendIds = request.BackendIds
            });
        }

        public Task<CodexBackendOutput> ReadFinalOutputAsync(
            CodexBackendOutputRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Output with { BackendIds = request.BackendIds });

        public Task<CodexBackendUsageSnapshot> ReadUsageAsync(
            CodexBackendUsageRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexBackendUsageSnapshot());

        public Task<CodexBackendStatus> ResumeAsync(
            CodexBackendResumeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexBackendStatus
            {
                State = JobState.Running,
                BackendIds = request.BackendIds
            });
    }
}
