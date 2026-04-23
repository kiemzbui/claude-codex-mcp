using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using ClaudeCodexMcp.Backend;
using ClaudeCodexMcp.Backend.AppServerProtocol.CSharp;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Storage;

namespace ClaudeCodexMcp.Tests.Backend;

public sealed class CodexAppServerBackendTests
{
    [Fact]
    public async Task StartPreservesBackendIdsAndWritesOutputEvents()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var client = new RecordingAppServerClient();
        client.EnqueueResponse(OkResponse());
        client.EnqueueResponse(ThreadStartResponse("thread-1", @"C:\Users\tester\.codex\sessions\thread-1.json"));
        client.EnqueueResponse(TurnStartResponse("turn-1"));
        var backend = CreateBackend(workspace, client);

        var result = await backend.StartAsync(new CodexBackendStartRequest
        {
            JobId = "job_backend_ids",
            Title = "Backend ids",
            Repo = workspace.Root,
            Workflow = "direct",
            Prompt = "Say ok.",
            Options = new CodexBackendDispatchOptions
            {
                Model = "gpt-5.4-codex",
                Effort = "medium",
                ServiceTier = "normal"
            }
        });

        Assert.Equal(JobState.Running, result.Status.State);
        Assert.Equal("thread-1", result.BackendIds.ThreadId);
        Assert.Equal("turn-1", result.BackendIds.TurnId);
        Assert.Equal(@"C:\Users\tester\.codex\sessions\thread-1.json", result.BackendIds.SessionId);

        var threadStart = client.Requests.Single(request => request.Method == AppServerProtocolNames.ThreadStart).Parameters as AppServerThreadStartParams;
        Assert.NotNull(threadStart);
        Assert.Equal("never", threadStart.ApprovalPolicy);
        Assert.Equal("user", threadStart.ApprovalsReviewer);
        Assert.Equal("read-only", threadStart.Sandbox);

        var outputStore = new OutputStore(new ManagerStatePaths(workspace.StateDirectory));
        var entries = await outputStore.ReadAsync("job_backend_ids", limit: 20);
        Assert.Contains(entries.Entries, entry => entry.Message == "app-server request thread/start");
        Assert.Contains(entries.Entries, entry => entry.Message == "Codex app-server thread and turn started.");
    }

    [Fact]
    public async Task ObserveMapsLifecycleNotificationsToNormalizedStates()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var client = new RecordingAppServerClient();
        client.EnqueueResponse(OkResponse());
        client.EnqueueResponse(ThreadStartResponse("thread-life", "session-life"));
        client.EnqueueResponse(TurnStartResponse("turn-life"));
        client.EnqueueNotification("""
            {"method":"thread/status/changed","params":{"threadId":"thread-life","status":{"type":"active","activeFlags":["waitingOnUserInput"]}}}
            """);
        var backend = CreateBackend(workspace, client);
        var start = await StartAsync(backend, workspace, "job_lifecycle");

        var waiting = await backend.ObserveStatusAsync(new CodexBackendObserveRequest
        {
            JobId = "job_lifecycle",
            BackendIds = start.BackendIds
        });

        Assert.Equal(JobState.WaitingForInput, waiting.State);
        Assert.NotNull(waiting.WaitingForInput);

        client.EnqueueNotification("""{"method":"turn/completed","params":{"threadId":"thread-life","turnId":"turn-life"}}""");
        var completed = await backend.ObserveStatusAsync(new CodexBackendObserveRequest
        {
            JobId = "job_lifecycle",
            BackendIds = start.BackendIds
        });

        Assert.Equal(JobState.Completed, completed.State);
        Assert.Equal("thread-life", completed.BackendIds.ThreadId);
        Assert.Equal("turn-life", completed.BackendIds.TurnId);
    }

    [Theory]
    [InlineData("completed", JobState.Completed)]
    [InlineData("failed", JobState.Failed)]
    [InlineData("interrupted", JobState.Cancelled)]
    [InlineData("inProgress", JobState.Running)]
    public async Task PollingThreadReadMapsTurnStatusToNormalizedJobState(string turnStatus, JobState expected)
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var client = new RecordingAppServerClient();
        client.EnqueueResponse(OkResponse());
        client.EnqueueResponse(ThreadStartResponse("thread-poll", "session-poll"));
        client.EnqueueResponse(TurnStartResponse("turn-poll"));
        client.EnqueueResponse(ThreadReadResponse("thread-poll", "session-poll", "turn-poll", turnStatus, "poll final text"));
        var backend = CreateBackend(workspace, client);
        var start = await StartAsync(backend, workspace, "job_polling");

        var status = await backend.ObserveStatusAsync(new CodexBackendObserveRequest
        {
            JobId = "job_polling",
            BackendIds = start.BackendIds
        });

        Assert.Equal(expected, status.State);
        Assert.Equal("turn-poll", status.BackendIds.TurnId);
    }

    [Fact]
    public async Task ReadFinalOutputUsesThreadReadAgentMessages()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var client = new RecordingAppServerClient();
        client.EnqueueResponse(OkResponse());
        client.EnqueueResponse(ThreadStartResponse("thread-output", "session-output"));
        client.EnqueueResponse(TurnStartResponse("turn-output"));
        client.EnqueueResponse(ThreadReadResponse("thread-output", "session-output", "turn-output", "completed", "FINAL_FROM_THREAD_READ"));
        var backend = CreateBackend(workspace, client);
        var start = await StartAsync(backend, workspace, "job_output");

        var output = await backend.ReadFinalOutputAsync(new CodexBackendOutputRequest
        {
            JobId = "job_output",
            BackendIds = start.BackendIds
        });

        Assert.Equal("FINAL_FROM_THREAD_READ", output.FinalText);
    }

    [Fact]
    public async Task ContinuationDispatchOptionsPassThroughToBackendTurnStart()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var client = new RecordingAppServerClient();
        client.EnqueueResponse(OkResponse());
        client.EnqueueResponse(ThreadStartResponse("thread-input", "session-input"));
        client.EnqueueResponse(TurnStartResponse("turn-start"));
        client.EnqueueResponse(TurnStartResponse("turn-follow-up"));
        var backend = CreateBackend(workspace, client);
        var start = await StartAsync(backend, workspace, "job_input");

        var status = await backend.SendInputAsync(new CodexBackendSendInputRequest
        {
            JobId = "job_input",
            BackendIds = start.BackendIds,
            Prompt = "Follow up.",
            Options = new CodexBackendDispatchOptions
            {
                Model = "gpt-5.4-codex",
                Effort = "high",
                FastMode = true,
                ServiceTier = "fast"
            }
        });

        Assert.Equal(JobState.Running, status.State);
        var followUp = client.Requests
            .Where(request => request.Method == AppServerProtocolNames.TurnStart)
            .Select(request => request.Parameters)
            .OfType<AppServerTurnStartParams>()
            .Last();
        Assert.Equal("thread-input", followUp.ThreadId);
        Assert.Equal("Follow up.", followUp.Input.Single().Text);
        Assert.Equal("gpt-5.4-codex", followUp.Model);
        Assert.Equal("high", followUp.Effort);
        Assert.Equal("fast", followUp.ServiceTier);
    }

    [Fact]
    public async Task CancelUsesBackendThreadAndTurnIds()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var client = new RecordingAppServerClient();
        client.EnqueueResponse(OkResponse());
        client.EnqueueResponse(ThreadStartResponse("thread-cancel", "session-cancel"));
        client.EnqueueResponse(TurnStartResponse("turn-cancel"));
        client.EnqueueResponse(OkResponse());
        var backend = CreateBackend(workspace, client);
        var start = await StartAsync(backend, workspace, "job_cancel");

        var cancelled = await backend.CancelAsync(new CodexBackendCancelRequest
        {
            JobId = "job_cancel",
            BackendIds = start.BackendIds
        });

        Assert.Equal(JobState.Cancelled, cancelled.State);
        var interrupt = Assert.IsType<AppServerTurnInterruptParams>(
            client.Requests.Single(request => request.Method == AppServerProtocolNames.TurnInterrupt).Parameters);
        Assert.Equal("thread-cancel", interrupt.ThreadId);
        Assert.Equal("turn-cancel", interrupt.TurnId);
    }

    [Fact]
    public async Task UsageAndRateLimitDataCanBeReadAfterBackendNotifications()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var client = new RecordingAppServerClient();
        client.EnqueueResponse(OkResponse());
        client.EnqueueResponse(ThreadStartResponse("thread-usage", "session-usage"));
        client.EnqueueResponse(TurnStartResponse("turn-usage"));
        client.EnqueueNotification("""
            {"method":"thread/tokenUsage/updated","params":{"threadId":"thread-usage","turnId":"turn-usage","tokenUsage":{"total":{"totalTokens":1000,"inputTokens":600,"cachedInputTokens":10,"outputTokens":400,"reasoningOutputTokens":100},"last":{"totalTokens":100,"inputTokens":60,"cachedInputTokens":0,"outputTokens":40,"reasoningOutputTokens":10},"modelContextWindow":258400}}}
            """);
        client.EnqueueNotification("""{"method":"turn/completed","params":{"threadId":"thread-usage","turnId":"turn-usage"}}""");
        client.EnqueueResponse("""
            {"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":42.5,"windowDurationMins":300,"resetsAt":1770000000},"secondary":null,"credits":null,"planType":null,"rateLimitReachedType":null},"rateLimitsByLimitId":null}}
            """);
        var backend = CreateBackend(workspace, client);
        var start = await StartAsync(backend, workspace, "job_usage");

        await backend.ObserveStatusAsync(new CodexBackendObserveRequest
        {
            JobId = "job_usage",
            BackendIds = start.BackendIds
        });
        var usage = await backend.ReadUsageAsync(new CodexBackendUsageRequest
        {
            JobId = "job_usage",
            BackendIds = start.BackendIds
        });

        Assert.Equal(1000, usage.TokenUsage?.TotalTokens);
        Assert.Equal(258400, usage.TokenUsage?.ContextWindowTokens);
        Assert.Equal("codex", usage.RateLimits?.LimitId);
        Assert.Equal(42.5, usage.RateLimits?.Primary?.UsedPercent);
    }

    [Fact]
    public void CapabilityReportsIncludeDegradedCliShape()
    {
        var appServer = CodexBackendCapabilities.AppServer();
        var cli = CodexBackendCapabilities.CliFallbackShape();

        Assert.Empty(appServer.DegradedCapabilities);
        Assert.Contains(cli.DegradedCapabilities, gap => gap.Capability == CodexBackendCapabilityNames.ReadUsage);
        Assert.Contains(cli.DegradedCapabilities, gap => gap.Capability == CodexBackendCapabilityNames.Resume);
        Assert.False(cli.SupportsReadUsage);
        Assert.False(cli.SupportsResume);
    }

    [Fact]
    public void ApprovalAndSandboxPolicyAreNotDispatchOptions()
    {
        var dispatchProperties = typeof(CodexBackendDispatchOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(nameof(CodexBackendDispatchOptions.Model), dispatchProperties);
        Assert.Contains(nameof(CodexBackendDispatchOptions.Effort), dispatchProperties);
        Assert.Contains(nameof(CodexBackendDispatchOptions.FastMode), dispatchProperties);
        Assert.DoesNotContain("ApprovalPolicy", dispatchProperties);
        Assert.DoesNotContain("ApprovalsReviewer", dispatchProperties);
        Assert.DoesNotContain("Sandbox", dispatchProperties);
    }

    [Fact]
    public async Task FakeBackendRecordsDeterministicLifecycleCalls()
    {
        var fake = new FakeCodexBackend();
        fake.EnqueueStatus(new CodexBackendStatus
        {
            State = JobState.Completed,
            BackendIds = new CodexBackendIds { ThreadId = "fake-thread" }
        });

        var start = await fake.StartAsync(new CodexBackendStartRequest
        {
            JobId = "job_fake",
            Title = "Fake",
            Repo = Directory.GetCurrentDirectory(),
            Workflow = "direct",
            Prompt = "prompt"
        });
        var observed = await fake.ObserveStatusAsync(new CodexBackendObserveRequest
        {
            JobId = "job_fake",
            BackendIds = start.BackendIds
        });
        await fake.SendInputAsync(new CodexBackendSendInputRequest
        {
            JobId = "job_fake",
            BackendIds = start.BackendIds,
            Prompt = "continue",
            Options = new CodexBackendDispatchOptions { Model = "model", Effort = "low", FastMode = true, ServiceTier = "fast" }
        });

        Assert.Equal(JobState.Completed, observed.State);
        Assert.Single(fake.StartRequests);
        Assert.Single(fake.SendInputRequests);
        Assert.Equal("model", fake.SendInputRequests.Single().Options.Model);
        Assert.True(fake.SendInputRequests.Single().Options.FastMode);
    }

    private static CodexAppServerBackend CreateBackend(TemporaryStateWorkspace workspace, RecordingAppServerClient client)
    {
        var paths = new ManagerStatePaths(workspace.StateDirectory);
        return new CodexAppServerBackend(
            new RecordingAppServerClientFactory(client),
            new OutputStore(paths),
            new CodexAppServerBackendOptions { NotificationDrainTimeout = TimeSpan.Zero });
    }

    private static Task<CodexBackendStartResult> StartAsync(
        CodexAppServerBackend backend,
        TemporaryStateWorkspace workspace,
        string jobId) =>
        backend.StartAsync(new CodexBackendStartRequest
        {
            JobId = jobId,
            Title = "Test job",
            Repo = workspace.Root,
            Workflow = "direct",
            Prompt = "Say ok."
        });

    private static JsonDocument OkResponse() => Json("""{"result":{}}""");

    private static JsonDocument ThreadStartResponse(string threadId, string? path) => Json(JsonSerializer.Serialize(new
    {
        result = new
        {
            thread = new
            {
                id = threadId,
                path,
                status = new { type = "active", activeFlags = Array.Empty<string>() },
                turns = Array.Empty<object>()
            }
        }
    }));

    private static JsonDocument TurnStartResponse(string turnId) => Json(JsonSerializer.Serialize(new
    {
        result = new
        {
            turn = new
            {
                id = turnId,
                status = "inProgress",
                items = Array.Empty<object>()
            }
        }
    }));

    private static JsonDocument ThreadReadResponse(
        string threadId,
        string? path,
        string turnId,
        string turnStatus,
        string agentText) => Json(JsonSerializer.Serialize(new
        {
            result = new
            {
                thread = new
                {
                    id = threadId,
                    path,
                    status = new { type = "idle" },
                    turns = new object[]
                    {
                        new
                        {
                            id = turnId,
                            status = turnStatus,
                            items = new object[]
                            {
                                new
                                {
                                    type = "agentMessage",
                                    id = "item-1",
                                    text = agentText
                                }
                            }
                        }
                    }
                }
            }
        }));

    private static JsonDocument Json(string json) => JsonDocument.Parse(json);

    private sealed record RecordedAppServerRequest(string Method, object? Parameters);

    private sealed class RecordingAppServerClientFactory : IAppServerJsonRpcClientFactory
    {
        private readonly RecordingAppServerClient client;

        public RecordingAppServerClientFactory(RecordingAppServerClient client)
        {
            this.client = client;
        }

        public Task<IAppServerJsonRpcClient> CreateAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppServerJsonRpcClient>(client);
    }

    private sealed class RecordingAppServerClient : IAppServerJsonRpcClient
    {
        private readonly ConcurrentQueue<JsonDocument> responses = new();
        private readonly ConcurrentQueue<JsonDocument> notifications = new();
        private readonly List<RecordedAppServerRequest> requests = [];

        public IReadOnlyList<RecordedAppServerRequest> Requests => requests;

        public void EnqueueResponse(JsonDocument response) => responses.Enqueue(response);

        public void EnqueueResponse(string responseJson) => responses.Enqueue(Json(responseJson));

        public void EnqueueNotification(string notificationJson) => notifications.Enqueue(Json(notificationJson));

        public Task<JsonDocument> SendRequestAsync(
            string method,
            object? parameters,
            CancellationToken cancellationToken = default)
        {
            requests.Add(new RecordedAppServerRequest(method, parameters));
            if (!responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException($"No fake app-server response queued for {method}.");
            }

            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<JsonDocument> ReadAvailableNotificationsAsync(
            TimeSpan quietPeriod,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (notifications.TryDequeue(out var notification))
            {
                yield return notification;
            }

            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TemporaryStateWorkspace : IDisposable
    {
        private TemporaryStateWorkspace(string root)
        {
            Root = root;
            StateDirectory = Path.Combine(root, ".codex-manager");
        }

        public string Root { get; }

        public string StateDirectory { get; }

        public static TemporaryStateWorkspace Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "claude-codex-mcp-backend-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TemporaryStateWorkspace(root);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
