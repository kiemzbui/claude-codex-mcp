using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodexMcp.Backend;
using ClaudeCodexMcp.Configuration;
using ClaudeCodexMcp.Discovery;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Notifications;
using ClaudeCodexMcp.Storage;
using ClaudeCodexMcp.Supervisor;
using ClaudeCodexMcp.Tools;
using ClaudeCodexMcp.Usage;
using ClaudeCodexMcp.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClaudeCodexMcp.Tests.Smoke;

public sealed class MvpEndToEndSmokeTests
{
    [Fact]
    public async Task ReadOnlyDiscoveryProfilesDirectExecutionAndWorkflowRoutingSmoke()
    {
        using var workspace = SmokeWorkspace.Create();
        workspace.WriteGlobalSkill("smoke-readonly", "Read-only discovery smoke skill.");
        workspace.WriteGlobalAgent("smoke-agent", "Read-only discovery smoke agent.");
        var backend = new ScriptedSmokeBackend
        {
            Output = new CodexBackendOutput
            {
                Summary = "direct smoke completed",
                FinalText = "direct smoke completed"
            }
        };
        backend.EnqueueStatus(new CodexBackendStatus
        {
            State = JobState.Completed,
            BackendIds = new CodexBackendIds
            {
                ThreadId = "thread-direct",
                TurnId = "turn-direct",
                SessionId = "session-direct"
            },
            ResultSummary = "direct smoke completed"
        });
        var service = workspace.CreateService(backend);

        var profiles = service.ListProfiles();
        var profile = Assert.Single(profiles.Profiles, item => item.Name == SmokeWorkspace.PrimaryProfileName);
        Assert.Equal("Smoke read-only task prefix.", profile.TaskPrefix);
        Assert.Equal(CodexBackendNames.AppServer, profile.Backend);
        Assert.True(profile.ReadOnly);
        Assert.Equal("read-only", profile.Permissions["sandbox"]);
        Assert.Equal("never", profile.Permissions["approvalPolicy"]);
        Assert.Equal("gpt-5.4", profile.DefaultModel);
        Assert.Equal(CodexEfforts.Medium, profile.DefaultEffort);
        Assert.False(profile.FastMode);
        Assert.Equal("normal", profile.DefaultServiceTier);
        Assert.Contains(CanonicalWorkflows.Direct, profile.AllowedWorkflows);
        Assert.Contains(CanonicalWorkflows.SubagentManager, profile.AllowedWorkflows);
        Assert.Equal(1, profile.MaxConcurrentJobs);

        var skills = await service.ListSkillsAsync(forceRefresh: true, repo: workspace.RepoRoot);
        var agents = await service.ListAgentsAsync(forceRefresh: true, repo: workspace.RepoRoot);
        Assert.Contains(skills.Global, item => item.Name == "smoke-readonly" && item.SourceScope == CodexCapabilityDiscovery.GlobalSourceScope);
        Assert.Contains(agents.Global, item => item.Name == "smoke-agent" && item.SourceScope == CodexCapabilityDiscovery.GlobalSourceScope);
        var skillDetail = await service.GetSkillAsync("smoke-readonly", includeBody: false, repo: workspace.RepoRoot);
        var agentDetail = await service.GetAgentAsync("smoke-agent", includePrompt: false, repo: workspace.RepoRoot);
        Assert.True(skillDetail.Found);
        Assert.False(skillDetail.BodyIncluded);
        Assert.Null(skillDetail.Body);
        Assert.True(agentDetail.Found);
        Assert.False(agentDetail.BodyIncluded);
        Assert.Null(agentDetail.Body);

        var start = await service.StartTaskAsync(
            SmokeWorkspace.PrimaryProfileName,
            CanonicalWorkflows.Direct,
            "Direct read-only smoke",
            workspace.RepoRoot,
            "Reply with a compact read-only smoke result.",
            model: "gpt-5.4-codex",
            effort: CodexEfforts.High,
            fastMode: true);
        Assert.True(start.Accepted);
        Assert.NotNull(start.Job);
        Assert.Equal(JobState.Running, start.Job.Status);
        var directRequest = Assert.Single(backend.StartRequests);
        Assert.Equal(CanonicalWorkflows.Direct, directRequest.Workflow);
        Assert.Equal("read-only", directRequest.LaunchPolicy.Sandbox);
        Assert.Equal("never", directRequest.LaunchPolicy.ApprovalPolicy);
        Assert.Equal("gpt-5.4-codex", directRequest.Options.Model);
        Assert.Equal(CodexEfforts.High, directRequest.Options.Effort);
        Assert.True(directRequest.Options.FastMode);
        Assert.StartsWith("Smoke read-only task prefix.", directRequest.Prompt);

        var firstWait = await service.StatusAsync(start.Job.JobId, wait: true, timeoutSeconds: 20);
        var secondWait = await service.StatusAsync(start.Job.JobId, wait: true, timeoutSeconds: 20);
        Assert.Equal(20, firstWait.WaitTimeoutSeconds);
        Assert.Equal(20, secondWait.WaitTimeoutSeconds);
        Assert.Equal(JobState.Completed, firstWait.Job?.Status);
        Assert.Equal(JobState.Completed, secondWait.Job?.Status);

        var result = await service.ResultAsync(start.Job.JobId);
        Assert.Equal("direct smoke completed", result.Summary);
        Assert.False(result.FullOutputIncluded);
        Assert.Empty(result.ArtifactRefs);

        var routed = await service.StartTaskAsync(
            SmokeWorkspace.PrimaryProfileName,
            CanonicalWorkflows.SubagentManager,
            "Read-only subagent-manager route",
            workspace.RepoRoot,
            "$subagent-manager no-op read-only routing smoke. Do not delegate.");
        Assert.True(routed.Accepted);
        var routedRequest = backend.StartRequests.Last();
        Assert.Equal(CanonicalWorkflows.SubagentManager, routedRequest.Workflow);
        Assert.Contains("$subagent-manager no-op read-only routing smoke", routedRequest.Prompt);
    }

    [Fact]
    public async Task RecoveryWaitingQueuedInputCancellationSupervisorAndOutputPaginationSmoke()
    {
        using var workspace = SmokeWorkspace.Create(maxConcurrentJobs: 2);
        var backend = new ScriptedSmokeBackend();
        var service = workspace.CreateService(backend);
        var start = await service.StartTaskAsync(
            SmokeWorkspace.PrimaryProfileName,
            CanonicalWorkflows.Direct,
            "Recovery and queue smoke",
            workspace.RepoRoot,
            "Start a recoverable job.");
        Assert.True(start.Accepted);
        Assert.NotNull(start.Job);
        Assert.True(File.Exists(Path.Combine(workspace.StateDirectory, "jobs", $"{start.Job.JobId}.json")));

        var restartedService = workspace.CreateService(backend);
        var recoveredList = await restartedService.ListJobsAsync();
        Assert.Contains(recoveredList.Jobs, job => job.JobId == start.Job.JobId && job.Title == "Recovery and queue smoke");

        backend.EnqueueStatus(new CodexBackendStatus
        {
            State = JobState.WaitingForInput,
            BackendIds = new CodexBackendIds { ThreadId = "thread-wait", TurnId = "turn-wait", SessionId = "session-wait" },
            WaitingForInput = new WaitingForInputRecord
            {
                RequestId = "clarify-smoke",
                Prompt = "Choose a safe read-only option.",
                Metadata = new Dictionary<string, string> { ["kind"] = "clarification" }
            }
        });
        var waiting = await restartedService.StatusAsync(start.Job.JobId, wait: true, timeoutSeconds: 20);
        Assert.Equal(JobState.WaitingForInput, waiting.Job?.Status);
        Assert.Equal("clarify-smoke", waiting.Job?.WaitingForInput?.RequestId);
        Assert.Equal("clarification", waiting.Job?.WaitingForInput?.Metadata["kind"]);

        var input = await restartedService.SendInputAsync(
            start.Job.JobId,
            "Use the read-only option.",
            model: "gpt-5.4-codex",
            effort: CodexEfforts.High,
            fastMode: true);
        Assert.True(input.Accepted);
        Assert.Equal("gpt-5.4-codex", Assert.Single(backend.SendInputRequests).Options.Model);

        var delivered = await restartedService.QueueInputAsync(start.Job.JobId, "queued follow-up one", "Queued one");
        var cancellable = await restartedService.QueueInputAsync(start.Job.JobId, "queued follow-up two", "Queued two");
        var cancelled = await restartedService.CancelQueuedInputAsync(start.Job.JobId, cancellable.QueueItem!.QueueItemId);
        Assert.True(delivered.Accepted);
        Assert.True(cancelled.Accepted);
        Assert.Equal(1, cancelled.Job?.InputQueue.PendingCount);
        Assert.Equal(1, cancelled.Job?.InputQueue.CancelledCount);

        backend.EnqueueStatus(new CodexBackendStatus
        {
            State = JobState.Completed,
            BackendIds = new CodexBackendIds { ThreadId = "thread-done", TurnId = "turn-done", SessionId = "session-done" },
            ResultSummary = "turn completed before queued delivery"
        });
        var supervisor = workspace.CreateSupervisor(backend);
        var refreshed = await supervisor.RefreshJobAsync(start.Job.JobId);
        Assert.Equal(JobState.Running, refreshed?.Status);
        Assert.Equal(2, backend.SendInputRequests.Count);
        Assert.Equal("queued follow-up one", backend.SendInputRequests.Last().Prompt);
        var queue = await workspace.QueueStore.ReadAsync(start.Job.JobId);
        Assert.Contains(queue.Items, item => item.Status == QueueItemState.Delivered && item.Prompt == "queued follow-up one");
        Assert.Contains(queue.Items, item => item.Status == QueueItemState.Cancelled && item.Prompt == "queued follow-up two");

        await workspace.OutputStore.AppendAsync(new OutputLogEntry
        {
            JobId = start.Job.JobId,
            ThreadId = "thread-done",
            TurnId = "turn-1",
            AgentId = "agent-smoke",
            Message = "page one"
        });
        await workspace.OutputStore.AppendAsync(new OutputLogEntry
        {
            JobId = start.Job.JobId,
            ThreadId = "thread-done",
            TurnId = "turn-2",
            AgentId = "agent-smoke",
            Message = "page two"
        });
        var pageOne = await restartedService.ReadOutputAsync(start.Job.JobId, threadId: "thread-done", agentId: "agent-smoke", offset: 0, limit: 1);
        var pageTwo = await restartedService.ReadOutputAsync(start.Job.JobId, threadId: "thread-done", agentId: "agent-smoke", offset: pageOne.NextOffset, limit: 1, format: "text");
        Assert.False(pageOne.EndOfOutput);
        Assert.Equal(1, pageOne.NextOffset);
        Assert.Equal("page one", Assert.Single(pageOne.Entries).Message);
        Assert.True(pageTwo.EndOfOutput);
        Assert.Contains("page two", pageTwo.Text);

        var cancelJob = await restartedService.StartTaskAsync(
            SmokeWorkspace.PrimaryProfileName,
            CanonicalWorkflows.Direct,
            "Active cancellation smoke",
            workspace.RepoRoot,
            "Start a cancellable job.");
        var cancelledJob = await restartedService.CancelAsync(cancelJob.Job!.JobId);
        Assert.True(cancelledJob.Accepted);
        Assert.Equal(JobState.Cancelled, cancelledJob.Job?.Status);
    }

    [Fact]
    public async Task PolicyUsageChannelNotificationAndCliFallbackSmoke()
    {
        using var workspace = SmokeWorkspace.Create(channelEnabled: true);
        var appBackend = new ScriptedSmokeBackend
        {
            Usage = new CodexBackendUsageSnapshot
            {
                TokenUsage = new CodexBackendTokenUsage
                {
                    TotalTokens = 25,
                    ContextWindowTokens = 100
                },
                RateLimits = new CodexBackendRateLimits
                {
                    Primary = new CodexBackendRateLimitWindow
                    {
                        UsedPercent = 40,
                        WindowDurationMinutes = 300
                    },
                    Secondary = new CodexBackendRateLimitWindow
                    {
                        UsedPercent = 20,
                        WindowDurationMinutes = 10_080
                    }
                }
            }
        };
        var cliBackend = new ScriptedSmokeBackend(CodexBackendCapabilities.CliFallbackShape())
        {
            StartStatus = new CodexBackendStatus
            {
                State = JobState.Completed,
                BackendIds = new CodexBackendIds { SessionId = "cli-smoke-session" },
                ResultSummary = "cli fallback completed"
            },
            Output = new CodexBackendOutput
            {
                Summary = "cli final output",
                FinalText = "cli final output"
            }
        };
        var transport = new RecordingChannelTransport();
        var service = workspace.CreateService(
            appBackend,
            new SmokeBackendSelector(workspace.Options, appBackend, cliBackend),
            transport);

        var unknownProfile = await service.StartTaskAsync("missing", CanonicalWorkflows.Direct, "Unknown profile", workspace.RepoRoot, "Prompt");
        var rejectedRepo = await service.StartTaskAsync(SmokeWorkspace.PrimaryProfileName, CanonicalWorkflows.Direct, "Bad repo", workspace.OutsideRepoRoot, "Prompt");
        var rejectedWorkflow = await service.StartTaskAsync(SmokeWorkspace.PrimaryProfileName, CanonicalWorkflows.ManagedPlan, "Bad workflow", workspace.RepoRoot, "Prompt");
        Assert.Contains(unknownProfile.Errors, error => error.Code == "unknown_profile");
        Assert.Contains(rejectedRepo.Errors, error => error.Code == "repo_not_allowed");
        Assert.Contains(rejectedWorkflow.Errors, error => error.Code == "workflow_not_allowed");

        var first = await service.StartTaskAsync(SmokeWorkspace.PrimaryProfileName, CanonicalWorkflows.Direct, "Concurrent one", workspace.RepoRoot, "Prompt");
        var second = await service.StartTaskAsync(SmokeWorkspace.PrimaryProfileName, CanonicalWorkflows.Direct, "Concurrent two", workspace.RepoRoot, "Prompt");
        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Contains(second.Errors, error => error.Code == "max_concurrent_jobs_exceeded");

        var usage = await service.UsageAsync(first.Job!.JobId);
        Assert.Equal(75, usage.ContextRemainingPercentEstimate);
        Assert.Equal(80, usage.WeeklyUsageRemainingPercent);
        Assert.Equal(60, usage.FiveHourUsageRemainingPercent);
        Assert.Equal("[codex status: context 75% estimate | weekly 80% | 5h 60%]", usage.Statusline);

        appBackend.EnqueueStatus(new CodexBackendStatus
        {
            State = JobState.WaitingForInput,
            BackendIds = new CodexBackendIds { ThreadId = "thread-channel-wait" },
            WaitingForInput = new WaitingForInputRecord { RequestId = "channel-wait", Prompt = "Need input." }
        });
        appBackend.EnqueueStatus(new CodexBackendStatus
        {
            State = JobState.Completed,
            BackendIds = new CodexBackendIds { ThreadId = "thread-channel-complete" },
            ResultSummary = "channel completed"
        });
        var waiting = await service.StatusAsync(first.Job.JobId);
        var completed = await service.StatusAsync(first.Job.JobId);
        Assert.Equal(JobState.WaitingForInput, waiting.Job?.Status);
        Assert.Equal(JobState.Completed, completed.Job?.Status);

        var failingBackend = new ScriptedSmokeBackend
        {
            StartStatus = new CodexBackendStatus
            {
                State = JobState.Failed,
                BackendIds = new CodexBackendIds { ThreadId = "thread-failed" },
                LastError = "smoke failure"
            }
        };
        var failingService = workspace.CreateService(failingBackend, null, transport);
        var failed = await failingService.StartTaskAsync(
            SmokeWorkspace.PrimaryProfileName,
            CanonicalWorkflows.Direct,
            "Failed notification smoke",
            workspace.RepoRoot,
            "Fail this deterministic smoke job.");
        Assert.True(failed.Accepted);
        Assert.Equal(JobState.Failed, failed.Job?.Status);

        var notificationEvents = await workspace.NotificationStore.ReadAsync(first.Job.JobId);
        Assert.Contains(notificationEvents, record => record.EventName == NotificationEventNames.JobWaitingForInput);
        Assert.Contains(notificationEvents, record => record.EventName == NotificationEventNames.JobCompleted);
        var failedEvents = await workspace.NotificationStore.ReadAsync(failed.Job!.JobId);
        Assert.Contains(failedEvents, record => record.EventName == NotificationEventNames.JobFailed);
        Assert.Contains(transport.Payloads, payload => payload.Contains(NotificationEventNames.JobWaitingForInput, StringComparison.Ordinal));
        Assert.Contains(transport.Payloads, payload => payload.Contains(NotificationEventNames.JobCompleted, StringComparison.Ordinal));
        Assert.Contains(transport.Payloads, payload => payload.Contains(NotificationEventNames.JobFailed, StringComparison.Ordinal));

        var cliStart = await service.StartTaskAsync(
            SmokeWorkspace.CliProfileName,
            CanonicalWorkflows.Direct,
            "CLI degraded fallback smoke",
            workspace.RepoRoot,
            "Run through CLI fallback.");
        var cliResult = await service.ResultAsync(cliStart.Job!.JobId);
        var cliUsage = await service.UsageAsync(cliStart.Job.JobId);
        Assert.True(cliStart.Accepted);
        Assert.Equal(JobState.Completed, cliStart.Job.Status);
        Assert.Single(cliBackend.StartRequests);
        Assert.Empty(cliBackend.SendInputRequests);
        Assert.Equal("cli final output", cliResult.Summary);
        Assert.Equal(UsageReporter.UnknownStatusline, cliUsage.Statusline);
        Assert.Contains(cliBackend.Capabilities.DegradedCapabilities, item => item.Capability == CodexBackendCapabilityNames.SendInput);
        Assert.Contains(cliBackend.Capabilities.DegradedCapabilities, item => item.Capability == CodexBackendCapabilityNames.ReadUsage);
    }

    private sealed class SmokeWorkspace : IDisposable
    {
        public const string PrimaryProfileName = "implementation";
        public const string CliProfileName = "cli-fallback";

        private SmokeWorkspace(string root, bool channelEnabled, int maxConcurrentJobs)
        {
            Root = root;
            RepoRoot = Path.Combine(root, "repo");
            OutsideRepoRoot = Path.Combine(root, "outside");
            CodexHome = Path.Combine(root, "codex-home");
            StateDirectory = Path.Combine(root, ".codex-manager");
            Directory.CreateDirectory(RepoRoot);
            Directory.CreateDirectory(OutsideRepoRoot);
            Directory.CreateDirectory(CodexHome);
            Paths = new ManagerStatePaths(StateDirectory);
            JobStore = new JobStore(Paths);
            QueueStore = new QueueStore(Paths);
            OutputStore = new OutputStore(Paths);
            NotificationStore = new NotificationStore(Paths);
            Locks = new CodexJobLockRegistry();
            Options = CreateOptions(RepoRoot, StateDirectory, channelEnabled, maxConcurrentJobs);
        }

        public string Root { get; }

        public string RepoRoot { get; }

        public string OutsideRepoRoot { get; }

        public string CodexHome { get; }

        public string StateDirectory { get; }

        public ManagerStatePaths Paths { get; }

        public JobStore JobStore { get; }

        public QueueStore QueueStore { get; }

        public OutputStore OutputStore { get; }

        public NotificationStore NotificationStore { get; }

        public CodexJobLockRegistry Locks { get; }

        public ManagerOptions Options { get; }

        public static SmokeWorkspace Create(bool channelEnabled = false, int maxConcurrentJobs = 1)
        {
            var root = Path.Combine(Path.GetTempPath(), "claude-codex-mcp-smoke-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new SmokeWorkspace(root, channelEnabled, maxConcurrentJobs);
        }

        public void WriteGlobalSkill(string name, string description)
        {
            var skillDirectory = Path.Combine(CodexHome, "skills", name);
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(
                Path.Combine(skillDirectory, "SKILL.md"),
                $"""
                ---
                name: {name}
                description: "{description}"
                ---

                # {name}

                This fixture is metadata-only by default.
                """);
        }

        public void WriteGlobalAgent(string name, string description)
        {
            var agentsDirectory = Path.Combine(CodexHome, "agents");
            Directory.CreateDirectory(agentsDirectory);
            File.WriteAllText(
                Path.Combine(agentsDirectory, $"{name}.md"),
                $"""
                ---
                name: {name}
                description: "{description}"
                ---

                Prompt body should not be returned by default.
                """);
        }

        public CodexToolService CreateService(
            ICodexBackend? backend = null,
            ICodexBackendSelector? backendSelector = null,
            IClaudeChannelTransport? channelTransport = null)
        {
            var options = Microsoft.Extensions.Options.Options.Create(Options);
            var policyValidator = new ProfilePolicyValidator(options);
            var discovery = new CodexCapabilityDiscovery(
                new DiscoveryCacheStore(Paths),
                new CodexDiscoveryOptions
                {
                    CodexHome = CodexHome,
                    RepoRoot = RepoRoot,
                    CacheTtl = TimeSpan.FromMinutes(5)
                });

            return new CodexToolService(
                options,
                policyValidator,
                JobStore,
                QueueStore,
                OutputStore,
                backend ?? new ScriptedSmokeBackend(),
                discovery,
                Locks,
                new UsageReporter(),
                CreateNotificationDispatcher(channelTransport),
                backendSelector);
        }

        public CodexJobSupervisor CreateSupervisor(
            ICodexBackend backend,
            IClaudeChannelTransport? channelTransport = null) =>
            new(
                JobStore,
                QueueStore,
                OutputStore,
                backend,
                Locks,
                NullLogger<CodexJobSupervisor>.Instance,
                new CodexJobSupervisorOptions { PollInterval = TimeSpan.FromMilliseconds(1) },
                CreateNotificationDispatcher(channelTransport),
                Microsoft.Extensions.Options.Options.Create(Options));

        private NotificationDispatcher CreateNotificationDispatcher(IClaudeChannelTransport? channelTransport) =>
            new(
                NotificationStore,
                new ClaudeChannelNotifier(channelTransport ?? new DisabledClaudeChannelTransport()),
                NullLogger<NotificationDispatcher>.Instance);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static ManagerOptions CreateOptions(
            string repoRoot,
            string stateDirectory,
            bool channelEnabled,
            int maxConcurrentJobs)
        {
            var implementation = CreateProfile(repoRoot, CodexBackendNames.AppServer, channelEnabled, maxConcurrentJobs);
            var cli = CreateProfile(repoRoot, "cli-fallback", channelEnabled: false, maxConcurrentJobs);
            return new ManagerOptions
            {
                StateDirectory = stateDirectory,
                Profiles = new Dictionary<string, ProfileOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [PrimaryProfileName] = implementation,
                    [CliProfileName] = cli
                }
            };
        }

        private static ProfileOptions CreateProfile(
            string repoRoot,
            string backend,
            bool channelEnabled,
            int maxConcurrentJobs) =>
            new()
            {
                Repo = repoRoot,
                AllowedRepos = [repoRoot],
                TaskPrefix = "Smoke read-only task prefix.",
                Backend = backend,
                ReadOnly = true,
                Permissions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sandbox"] = "read-only",
                    ["approvalPolicy"] = "never",
                    ["approvalsReviewer"] = "user"
                },
                DefaultWorkflow = CanonicalWorkflows.Direct,
                AllowedWorkflows = [CanonicalWorkflows.Direct, CanonicalWorkflows.SubagentManager],
                MaxConcurrentJobs = maxConcurrentJobs,
                ChannelNotifications = new ChannelNotificationOptions { Enabled = channelEnabled },
                DefaultModel = "gpt-5.4",
                AllowedModels = ["gpt-5.4", "gpt-5.4-codex"],
                AllowModelOverride = true,
                DefaultEffort = CodexEfforts.Medium,
                AllowedEfforts = [CodexEfforts.Medium, CodexEfforts.High],
                AllowEffortOverride = true,
                FastMode = false,
                AllowFastModeOverride = true,
                DefaultServiceTier = "normal"
            };
    }

    private sealed class ScriptedSmokeBackend : ICodexBackend
    {
        private readonly Queue<CodexBackendStatus> statuses = new();

        public ScriptedSmokeBackend(CodexBackendCapabilities? capabilities = null)
        {
            Capabilities = capabilities ?? CodexBackendCapabilities.AppServer("smoke-app-server");
        }

        public CodexBackendCapabilities Capabilities { get; }

        public List<CodexBackendStartRequest> StartRequests { get; } = [];

        public List<CodexBackendSendInputRequest> SendInputRequests { get; } = [];

        public List<CodexBackendCancelRequest> CancelRequests { get; } = [];

        public List<CodexBackendResumeRequest> ResumeRequests { get; } = [];

        public CodexBackendStatus StartStatus { get; init; } = new()
        {
            State = JobState.Running,
            BackendIds = new CodexBackendIds
            {
                ThreadId = "thread-start",
                TurnId = "turn-start",
                SessionId = "session-start"
            }
        };

        public CodexBackendStatus? SendInputStatus { get; init; }

        public CodexBackendOutput Output { get; init; } = new()
        {
            Summary = "smoke final output",
            FinalText = "smoke final output"
        };

        public CodexBackendUsageSnapshot Usage { get; init; } = new();

        public void EnqueueStatus(CodexBackendStatus status) => statuses.Enqueue(status);

        public Task<CodexBackendStartResult> StartAsync(
            CodexBackendStartRequest request,
            CancellationToken cancellationToken = default)
        {
            StartRequests.Add(request);
            return Task.FromResult(new CodexBackendStartResult { Status = WithFallbackIds(StartStatus, request.JobId) });
        }

        public Task<CodexBackendStatus> ObserveStatusAsync(
            CodexBackendObserveRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(statuses.Count == 0
                ? new CodexBackendStatus { State = JobState.Running, BackendIds = request.BackendIds }
                : WithFallbackIds(statuses.Dequeue(), request.JobId));

        public Task<CodexBackendStatus> PollStatusAsync(
            CodexBackendObserveRequest request,
            CancellationToken cancellationToken = default) =>
            ObserveStatusAsync(request, cancellationToken);

        public Task<CodexBackendStatus> SendInputAsync(
            CodexBackendSendInputRequest request,
            CancellationToken cancellationToken = default)
        {
            SendInputRequests.Add(request);
            return Task.FromResult(SendInputStatus ?? new CodexBackendStatus
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
            Task.FromResult(Usage);

        public Task<CodexBackendStatus> ResumeAsync(
            CodexBackendResumeRequest request,
            CancellationToken cancellationToken = default)
        {
            ResumeRequests.Add(request);
            return Task.FromResult(new CodexBackendStatus
            {
                State = JobState.Running,
                BackendIds = request.BackendIds
            });
        }

        private static CodexBackendStatus WithFallbackIds(CodexBackendStatus status, string jobId)
        {
            if (!string.IsNullOrWhiteSpace(status.BackendIds.ThreadId) ||
                !string.IsNullOrWhiteSpace(status.BackendIds.TurnId) ||
                !string.IsNullOrWhiteSpace(status.BackendIds.SessionId))
            {
                return status;
            }

            return status with
            {
                BackendIds = new CodexBackendIds
                {
                    ThreadId = $"thread-{jobId}",
                    TurnId = $"turn-{jobId}",
                    SessionId = $"session-{jobId}"
                }
            };
        }
    }

    private sealed class SmokeBackendSelector : ICodexBackendSelector
    {
        private readonly ManagerOptions options;
        private readonly ICodexBackend appServerBackend;
        private readonly ICodexBackend cliBackend;

        public SmokeBackendSelector(
            ManagerOptions options,
            ICodexBackend appServerBackend,
            ICodexBackend cliBackend)
        {
            this.options = options;
            this.appServerBackend = appServerBackend;
            this.cliBackend = cliBackend;
        }

        public ICodexBackend SelectForPolicy(ValidatedDispatchPolicy policy) =>
            CodexCliBackendSelection.IsCliAllowedByProfile(policy.Backend)
                ? cliBackend
                : appServerBackend;

        public ICodexBackend SelectForJob(CodexJobRecord job) =>
            options.Profiles.TryGetValue(job.Profile, out var profile) &&
            CodexCliBackendSelection.IsCliAllowedByProfile(profile.Backend)
                ? cliBackend
                : appServerBackend;
    }

    private sealed class RecordingChannelTransport : IClaudeChannelTransport
    {
        public List<string> Payloads { get; } = [];

        public Task<ClaudeChannelDeliveryResult> SendAsync(
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            Payloads.Add(payloadJson);
            return Task.FromResult(ClaudeChannelDeliveryResult.Success());
        }
    }
}
