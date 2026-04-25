using ClaudeCodexMcp.Backend;
using ClaudeCodexMcp.Configuration;
using ClaudeCodexMcp.Discovery;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Storage;
using ClaudeCodexMcp.Supervisor;
using ClaudeCodexMcp.Tools;
using ClaudeCodexMcp.Usage;
using ClaudeCodexMcp.Workflows;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeCodexMcp.Tests.Tools;

public sealed class CodexToolServiceTests
{
    private static readonly SemaphoreSlim SessionEnvironmentGate = new(1, 1);

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
    public async Task StartTaskPersistsWakeSessionIdIntoJobAndIndex()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var service = workspace.CreateService();

        var start = await service.StartTaskAsync(
            "implementation",
            "direct",
            "Wake me",
            workspace.RepoRoot,
            "Prompt",
            wakeSessionId: "claude-session-123");

        var stored = await workspace.JobStore.ReadAsync(start.Job!.JobId);
        var index = await workspace.JobStore.ReadIndexAsync();

        Assert.Equal("claude-session-123", stored?.WakeSessionId);
        Assert.Equal(
            "claude-session-123",
            Assert.Single(index.Jobs, job => job.JobId == start.Job.JobId).WakeSessionId);
    }

    [Fact]
    public async Task StartTaskBlocksWhenSameSessionAlreadyHasActiveJob()
    {
        using var workspace = TemporaryToolWorkspace.Create(maxConcurrentJobs: 1);
        var service = workspace.CreateService();

        var first = await service.StartTaskAsync(
            "implementation",
            "direct",
            "Same session one",
            workspace.RepoRoot,
            "Prompt",
            wakeSessionId: "claude-session-1");
        var second = await service.StartTaskAsync(
            "implementation",
            "direct",
            "Same session two",
            workspace.RepoRoot,
            "Prompt",
            wakeSessionId: "claude-session-1");

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Contains(second.Errors, error => error.Code == "max_concurrent_jobs_exceeded");
    }

    [Fact]
    public async Task StartTaskAllowsDifferentSessionsToUseSameProfileConcurrently()
    {
        using var workspace = TemporaryToolWorkspace.Create(maxConcurrentJobs: 1);
        var service = workspace.CreateService();

        var first = await service.StartTaskAsync(
            "implementation",
            "direct",
            "Session one",
            workspace.RepoRoot,
            "Prompt",
            wakeSessionId: "claude-session-1");
        var second = await service.StartTaskAsync(
            "implementation",
            "direct",
            "Session two",
            workspace.RepoRoot,
            "Prompt",
            wakeSessionId: "claude-session-2");

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.NotEqual(first.Job?.JobId, second.Job?.JobId);
    }

    [Theory]
    [InlineData(null, "claude-session-1")]
    [InlineData("claude-session-1", null)]
    [InlineData(null, null)]
    public async Task StartTaskFallsBackToSharedProfileGateWhenAnySessionIdIsMissing(
        string? firstWakeSessionId,
        string? secondWakeSessionId)
    {
        using var workspace = TemporaryToolWorkspace.Create(maxConcurrentJobs: 1);
        var service = workspace.CreateService();

        var first = await service.StartTaskAsync(
            "implementation",
            "direct",
            "First job",
            workspace.RepoRoot,
            "Prompt",
            wakeSessionId: firstWakeSessionId);
        var second = await service.StartTaskAsync(
            "implementation",
            "direct",
            "Second job",
            workspace.RepoRoot,
            "Prompt",
            wakeSessionId: secondWakeSessionId);

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Contains(second.Errors, error => error.Code == "max_concurrent_jobs_exceeded");
    }

    [Fact]
    public async Task CodexStartTaskFallsBackToClaudeCodeSessionIdEnvironmentVariable()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var service = workspace.CreateService();
        var tools = new CodexTools(service);

        await SessionEnvironmentGate.WaitAsync();
        try
        {
            var originalClaudeCodeSessionId = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");
            var originalClaudeSessionId = Environment.GetEnvironmentVariable("CLAUDE_SESSION_ID");
            var userRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var currentSessionPath = Path.Combine(userRoot, ".codex-manager", "current-session-id.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(currentSessionPath)!);
            var originalCurrentSession = File.Exists(currentSessionPath)
                ? await File.ReadAllTextAsync(currentSessionPath)
                : null;

            try
            {
                if (File.Exists(currentSessionPath))
                {
                    File.Delete(currentSessionPath);
                }

                Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", "claude-env-session-456");
                Environment.SetEnvironmentVariable("CLAUDE_SESSION_ID", null);

                var start = await tools.codex_start_task(
                    "implementation",
                    "direct",
                    "Wake from env",
                    workspace.RepoRoot,
                    "Prompt");

                var stored = await workspace.JobStore.ReadAsync(start.Job!.JobId);
                Assert.Equal("claude-env-session-456", stored?.WakeSessionId);
            }
            finally
            {
                if (originalCurrentSession is null)
                {
                    if (File.Exists(currentSessionPath))
                    {
                        File.Delete(currentSessionPath);
                    }
                }
                else
                {
                    await File.WriteAllTextAsync(currentSessionPath, originalCurrentSession);
                }

                Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", originalClaudeCodeSessionId);
                Environment.SetEnvironmentVariable("CLAUDE_SESSION_ID", originalClaudeSessionId);
            }
        }
        finally
        {
            SessionEnvironmentGate.Release();
        }
    }

    [Fact]
    public async Task CodexStartTaskUsesCurrentSessionIdFileWhenOtherSourcesAreUnavailable()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var service = workspace.CreateService();
        var tools = new CodexTools(service);

        await SessionEnvironmentGate.WaitAsync();
        try
        {
            var originalClaudeCodeSessionId = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");
            var originalClaudeSessionId = Environment.GetEnvironmentVariable("CLAUDE_SESSION_ID");
            Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", null);
            Environment.SetEnvironmentVariable("CLAUDE_SESSION_ID", null);

            var userRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var currentSessionPath = Path.Combine(userRoot, ".codex-manager", "current-session-id.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(currentSessionPath)!);

            string? originalCurrentSession = File.Exists(currentSessionPath)
                ? await File.ReadAllTextAsync(currentSessionPath)
                : null;

            try
            {
                await File.WriteAllTextAsync(currentSessionPath, "legacy-current-session");

                var start = await tools.codex_start_task(
                    "implementation",
                    "direct",
                    "Wake from current session file",
                    workspace.RepoRoot,
                    "Prompt");

                var stored = await workspace.JobStore.ReadAsync(start.Job!.JobId);
                Assert.Equal("legacy-current-session", stored?.WakeSessionId);
            }
            finally
            {
                if (originalCurrentSession is null)
                {
                    if (File.Exists(currentSessionPath))
                    {
                        File.Delete(currentSessionPath);
                    }
                }
                else
                {
                    await File.WriteAllTextAsync(currentSessionPath, originalCurrentSession);
                }

                Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", originalClaudeCodeSessionId);
                Environment.SetEnvironmentVariable("CLAUDE_SESSION_ID", originalClaudeSessionId);
            }
        }
        finally
        {
            SessionEnvironmentGate.Release();
        }
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
        Assert.Null(result.FullOutput);
        Assert.Empty(result.ArtifactRefs);
        Assert.Equal("compact summary", result.Summary);
        Assert.DoesNotContain("FULL_OUTPUT_SECRET", result.Job?.ResultSummary ?? string.Empty);
    }

    [Fact]
    public async Task ReadOutputReturnsOffsetsLimitsEndMarkersAndMissingOutput()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var service = workspace.CreateService();
        var start = await service.StartTaskAsync("implementation", "direct", "Read output", workspace.RepoRoot, "Prompt");

        var missing = await service.ReadOutputAsync(start.Job!.JobId);

        Assert.Contains(missing.Errors, error => error.Code == "output_not_found");
        Assert.True(missing.EndOfOutput);

        await workspace.OutputStore.AppendAsync(new OutputLogEntry
        {
            JobId = start.Job.JobId,
            ThreadId = "thread-1",
            TurnId = "turn-1",
            AgentId = "agent-a",
            Message = "first"
        });
        await workspace.OutputStore.AppendAsync(new OutputLogEntry
        {
            JobId = start.Job.JobId,
            ThreadId = "thread-1",
            TurnId = "turn-2",
            AgentId = "agent-a",
            Message = "second"
        });

        var firstPage = await service.ReadOutputAsync(
            start.Job.JobId,
            threadId: "thread-1",
            agentId: "agent-a",
            offset: 0,
            limit: 1);
        var secondPage = await service.ReadOutputAsync(
            start.Job.JobId,
            threadId: "thread-1",
            agentId: "agent-a",
            offset: firstPage.NextOffset,
            limit: 1,
            format: "text");

        Assert.False(firstPage.EndOfOutput);
        Assert.True(firstPage.Truncated);
        Assert.Equal(1, firstPage.NextOffset);
        Assert.Equal("first", Assert.Single(firstPage.Entries).Message);
        Assert.True(secondPage.EndOfOutput);
        Assert.Null(secondPage.NextOffset);
        Assert.Contains("second", secondPage.Text);
        Assert.Equal("text", secondPage.Format);
    }

    [Fact]
    public async Task ReadOutputUsesBackendFinalOutputWhenLocalLogIsMissing()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var backend = new InspectingBackend(workspace.StateDirectory)
        {
            StartStatus = new CodexBackendStatus
            {
                State = JobState.Completed,
                BackendIds = new CodexBackendIds { ThreadId = "thread-complete", TurnId = "turn-complete" }
            },
            Output = new CodexBackendOutput
            {
                Summary = "done",
                FinalText = "backend final text"
            }
        };
        var service = workspace.CreateService(backend);
        var start = await service.StartTaskAsync("implementation", "direct", "Backend output", workspace.RepoRoot, "Prompt");

        var output = await service.ReadOutputAsync(start.Job!.JobId, format: "text");

        Assert.Empty(output.Errors);
        Assert.Contains("backend final text", output.Text);
        Assert.Contains(output.ArtifactRefs, artifact => artifact.Kind == "backendThread" && artifact.Ref == "thread-complete");
    }

    [Fact]
    public async Task ReadOutputTruncatesBeforeSerializationAndKeepsJsonValid()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var service = workspace.CreateService();
        var start = await service.StartTaskAsync("implementation", "direct", "Huge output", workspace.RepoRoot, "Prompt");
        await workspace.OutputStore.AppendAsync(new OutputLogEntry
        {
            JobId = start.Job!.JobId,
            Message = "prefix " + new string('x', OutputResponseLimits.PaginatedChunkBytes * 2)
        });

        var output = await service.ReadOutputAsync(start.Job.JobId, limit: 10);
        var json = JsonSerializer.Serialize(output);

        using var document = JsonDocument.Parse(json);
        Assert.True(output.Truncated);
        Assert.Contains("[truncated]", Assert.Single(output.Entries).Message);
        Assert.True(OutputStoreBudget.SerializedByteCount(output) <= OutputResponseLimits.PaginatedChunkBytes);
        Assert.Equal("prefix ", document.RootElement.GetProperty("Entries")[0].GetProperty("Message").GetString()?.Substring(0, 7));
    }

    [Fact]
    public async Task ReadOutputFieldTruncationKeepsRecoveryRefsWithoutContinuation()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var service = workspace.CreateService();
        var start = await service.StartTaskAsync("implementation", "direct", "Field truncation", workspace.RepoRoot, "Prompt");
        await workspace.OutputStore.AppendAsync(new OutputLogEntry
        {
            JobId = start.Job!.JobId,
            Message = "field " + new string('x', OutputResponseLimits.PaginatedChunkBytes * 2)
        });

        var output = await service.ReadOutputAsync(start.Job.JobId, limit: 10);

        Assert.True(output.Truncated);
        Assert.True(output.EndOfOutput);
        Assert.Null(output.NextOffset);
        Assert.Null(output.NextCursor);
        Assert.Contains("[truncated]", Assert.Single(output.Entries).Message);
        Assert.False(string.IsNullOrWhiteSpace(output.LogRef));
        Assert.Contains(output.ArtifactRefs, artifact => artifact.Kind == "log" && !string.IsNullOrWhiteSpace(artifact.Ref));
    }

    [Fact]
    public async Task ReadOutputCombinesPageContinuationWithFieldRecoveryRefs()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var service = workspace.CreateService();
        var start = await service.StartTaskAsync("implementation", "direct", "Combined truncation", workspace.RepoRoot, "Prompt");
        await workspace.OutputStore.AppendAsync(new OutputLogEntry
        {
            JobId = start.Job!.JobId,
            Message = "field " + new string('x', OutputResponseLimits.PaginatedChunkBytes * 2)
        });
        await workspace.OutputStore.AppendAsync(new OutputLogEntry
        {
            JobId = start.Job.JobId,
            Message = "second page"
        });

        var output = await service.ReadOutputAsync(start.Job.JobId, offset: 0, limit: 1);

        Assert.True(output.Truncated);
        Assert.False(output.EndOfOutput);
        Assert.Equal(1, output.NextOffset);
        Assert.Contains("[truncated]", Assert.Single(output.Entries).Message);
        Assert.Contains(output.ArtifactRefs, artifact => artifact.Kind == "log" && !string.IsNullOrWhiteSpace(artifact.Ref));
    }

    [Fact]
    public async Task ResultFullIncludesBudgetedOutputTruncationAndArtifactRefs()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var backend = new InspectingBackend(workspace.StateDirectory)
        {
            StartStatus = new CodexBackendStatus
            {
                State = JobState.Completed,
                BackendIds = new CodexBackendIds { ThreadId = "thread-full" }
            },
            Output = new CodexBackendOutput
            {
                Summary = "full summary",
                FinalText = "full output " + new string('y', OutputResponseLimits.FullBytes * 2)
            }
        };
        var service = workspace.CreateService(backend);
        var start = await service.StartTaskAsync("implementation", "direct", "Full result", workspace.RepoRoot, "Prompt");

        var result = await service.ResultAsync(start.Job!.JobId, detail: "full");
        var json = JsonSerializer.Serialize(result);

        using var _ = JsonDocument.Parse(json);
        Assert.True(result.FullOutputIncluded);
        Assert.True(result.Truncated);
        Assert.Null(result.NextOffset);
        Assert.Null(result.NextCursor);
        Assert.Contains("[truncated]", result.FullOutput);
        Assert.Contains(result.ArtifactRefs, artifact => artifact.Kind == "log");
        Assert.Contains(result.ArtifactRefs, artifact => artifact.Kind == "backendThread" && artifact.Ref == "thread-full");
        Assert.True(OutputStoreBudget.SerializedByteCount(result) <= OutputResponseLimits.FullBytes);
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

    [Fact]
    public async Task UsageRefreshPersistsBackendDataAndReturnsNormalizedFields()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var backend = new InspectingBackend(workspace.StateDirectory)
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
        var service = workspace.CreateService(backend);
        var start = await service.StartTaskAsync("implementation", "direct", "Usage", workspace.RepoRoot, "Prompt");

        var usage = await service.UsageAsync(start.Job!.JobId);

        Assert.Equal(75, usage.ContextRemainingPercentEstimate);
        Assert.Equal(80, usage.WeeklyUsageRemainingPercent);
        Assert.Equal(60, usage.FiveHourUsageRemainingPercent);
        Assert.Equal("[codex status: context 75% estimate | weekly 80% | 5h 60%]", usage.Statusline);
        Assert.Equal(usage.Statusline, usage.Job?.Statusline);
        var stored = await workspace.JobStore.ReadAsync(start.Job.JobId);
        Assert.Equal(25, stored?.UsageSnapshot?.TokenUsage?.TotalTokens);
    }

    [Fact]
    public async Task StatusResultAndSendInputIncludePersistedStatusline()
    {
        using var workspace = TemporaryToolWorkspace.Create();
        var backend = new InspectingBackend(workspace.StateDirectory)
        {
            StartStatus = new CodexBackendStatus
            {
                State = JobState.Running,
                BackendIds = new CodexBackendIds { ThreadId = "thread-start", TurnId = "turn-start", SessionId = "session-start" },
                UsageSnapshot = new CodexBackendUsageSnapshot
                {
                    TokenUsage = new CodexBackendTokenUsage
                    {
                        TotalTokens = 10,
                        ContextWindowTokens = 100
                    },
                    RateLimits = new CodexBackendRateLimits
                    {
                        Primary = new CodexBackendRateLimitWindow
                        {
                            UsedPercent = 30,
                            WindowDurationMinutes = 300
                        }
                    }
                }
            }
        };
        var service = workspace.CreateService(backend);
        var start = await service.StartTaskAsync("implementation", "direct", "Statusline", workspace.RepoRoot, "Prompt");

        var status = await service.StatusAsync(start.Job!.JobId);
        var input = await service.SendInputAsync(start.Job.JobId, "Follow up");
        var result = await service.ResultAsync(start.Job.JobId);

        Assert.Equal("[codex status: context 90% estimate | weekly ? | 5h 70%]", start.Job.Statusline);
        Assert.Equal(start.Job.Statusline, status.Job?.Statusline);
        Assert.Equal(start.Job.Statusline, input.Job?.Statusline);
        Assert.Equal(start.Job.Statusline, result.Job?.Statusline);
    }

    [Fact]
    public async Task ProfileSelectedCliFallbackRoutesToolDispatchOnlyWhenProfileRequestsIt()
    {
        using var cliWorkspace = TemporaryToolWorkspace.Create(backendName: CodexBackendNames.Cli);
        var cliAppBackend = new InspectingBackend(cliWorkspace.StateDirectory, CodexBackendNames.AppServer);
        var cliFallbackBackend = new InspectingBackend(cliWorkspace.StateDirectory, CodexBackendNames.Cli)
        {
            StartStatus = new CodexBackendStatus
            {
                State = JobState.Completed,
                BackendIds = new CodexBackendIds { SessionId = "cli:job" },
                ResultSummary = "cli fallback completed"
            },
            Output = new CodexBackendOutput
            {
                Summary = "cli final output",
                FinalText = "cli final output"
            }
        };
        var cliService = cliWorkspace.CreateService(
            cliAppBackend,
            new TestBackendSelector(cliWorkspace.Options, cliAppBackend, cliFallbackBackend));

        var cliStart = await cliService.StartTaskAsync(
            "implementation",
            "direct",
            "CLI fallback",
            cliWorkspace.RepoRoot,
            "Prompt");
        var cliResult = await cliService.ResultAsync(cliStart.Job!.JobId);
        var cliUsage = await cliService.UsageAsync(cliStart.Job.JobId);

        Assert.True(cliStart.Accepted);
        Assert.Equal(JobState.Completed, cliStart.Job.Status);
        Assert.Equal("cli fallback completed", cliStart.Job.ResultSummary);
        Assert.Single(cliFallbackBackend.StartRequests);
        Assert.Empty(cliAppBackend.StartRequests);
        Assert.Equal("cli final output", cliResult.Summary);
        Assert.Equal(UsageReporter.UnknownStatusline, cliUsage.Statusline);

        using var appWorkspace = TemporaryToolWorkspace.Create(backendName: CodexBackendNames.AppServer);
        var appBackend = new InspectingBackend(appWorkspace.StateDirectory, CodexBackendNames.AppServer);
        var disallowedCliBackend = new InspectingBackend(appWorkspace.StateDirectory, CodexBackendNames.Cli);
        var appService = appWorkspace.CreateService(
            appBackend,
            new TestBackendSelector(appWorkspace.Options, appBackend, disallowedCliBackend));

        var appStart = await appService.StartTaskAsync(
            "implementation",
            "direct",
            "App server",
            appWorkspace.RepoRoot,
            "Prompt");

        Assert.True(appStart.Accepted);
        Assert.Single(appBackend.StartRequests);
        Assert.Empty(disallowedCliBackend.StartRequests);
    }

    private sealed class TemporaryToolWorkspace : IDisposable
    {
        private TemporaryToolWorkspace(string root, bool allowOverrides, string backendName, int maxConcurrentJobs)
        {
            Root = root;
            RepoRoot = Path.Combine(root, "repo");
            StateDirectory = Path.Combine(root, ".codex-manager");
            Directory.CreateDirectory(RepoRoot);
            Paths = new ManagerStatePaths(StateDirectory);
            JobStore = new JobStore(Paths);
            QueueStore = new QueueStore(Paths);
            OutputStore = new OutputStore(Paths);
            Options = CreateOptions(RepoRoot, allowOverrides, backendName, maxConcurrentJobs);
        }

        public string Root { get; }

        public string RepoRoot { get; }

        public string StateDirectory { get; }

        public ManagerStatePaths Paths { get; }

        public JobStore JobStore { get; }

        public QueueStore QueueStore { get; }

        public OutputStore OutputStore { get; }

        public ManagerOptions Options { get; }

        public static TemporaryToolWorkspace Create(
            bool allowOverrides = false,
            string backendName = "fake",
            int maxConcurrentJobs = 1)
        {
            var root = Path.Combine(Path.GetTempPath(), "claude-codex-mcp-tool-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TemporaryToolWorkspace(root, allowOverrides, backendName, maxConcurrentJobs);
        }

        public CodexToolService CreateService(
            ICodexBackend? backend = null,
            ICodexBackendSelector? backendSelector = null)
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
                OutputStore,
                backend ?? new InspectingBackend(StateDirectory),
                discovery,
                new CodexJobLockRegistry(),
                new UsageReporter(),
                backendSelector: backendSelector);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static ManagerOptions CreateOptions(
            string repoRoot,
            bool allowOverrides,
            string backendName,
            int maxConcurrentJobs)
        {
            var profile = new ProfileOptions
            {
                Repo = repoRoot,
                AllowedRepos = [repoRoot],
                TaskPrefix = "Use repo instructions.",
                Backend = backendName,
                ReadOnly = true,
                Permissions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sandbox"] = "read-only",
                    ["approvalPolicy"] = "never"
                },
                DefaultWorkflow = CanonicalWorkflows.Direct,
                AllowedWorkflows = [CanonicalWorkflows.Direct],
                MaxConcurrentJobs = maxConcurrentJobs,
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

        public InspectingBackend(string stateDirectory, string backendKind = CodexBackendNames.Fake)
        {
            this.stateDirectory = stateDirectory;
            Capabilities = new CodexBackendCapabilities
            {
                BackendId = backendKind,
                BackendKind = backendKind,
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

        public CodexBackendUsageSnapshot Usage { get; init; } = new();

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
            Task.FromResult(Usage);

        public Task<CodexBackendStatus> ResumeAsync(
            CodexBackendResumeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexBackendStatus
            {
                State = JobState.Running,
                BackendIds = request.BackendIds
            });
    }

    private sealed class TestBackendSelector : ICodexBackendSelector
    {
        private readonly ManagerOptions options;
        private readonly ICodexBackend appServerBackend;
        private readonly ICodexBackend cliBackend;

        public TestBackendSelector(
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
}
