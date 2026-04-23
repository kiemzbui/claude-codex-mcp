using System.IO;
using System.Linq;
using System.Text.Json;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Notifications;
using ClaudeCodexMcp.Storage;
using ClaudeCodexMcp.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodexMcp.Tests.Notifications;

public sealed class NotificationDispatcherTests
{
    [Theory]
    [InlineData(NotificationEventNames.JobWaitingForInput, JobState.WaitingForInput)]
    [InlineData(NotificationEventNames.JobCompleted, JobState.Completed)]
    [InlineData(NotificationEventNames.JobFailed, JobState.Failed)]
    [InlineData(NotificationEventNames.JobCancelled, JobState.Cancelled)]
    public async Task DispatcherPersistsRequiredLifecycleEventsAndCompactChannelPayload(
        string eventName,
        JobState state)
    {
        using var workspace = TemporaryNotificationWorkspace.Create();
        var transport = new RecordingClaudeChannelTransport();
        var dispatcher = workspace.CreateDispatcher(transport);
        var job = workspace.CreateJob("job_notify", state) with
        {
            NotificationMode = NotificationModes.Channel,
            ResultSummary = "FULL_OUTPUT_SHOULD_NOT_BE_INCLUDED " + new string('x', 2048),
            LastError = "token=secret-token failed"
        };

        var result = await dispatcher.DispatchAsync(new NotificationDispatchRequest
        {
            EventName = eventName,
            Job = job,
            ChannelEnabled = true
        });

        var records = await workspace.NotificationStore.ReadAsync(job.JobId);
        var attempted = records.Single(record => record.DeliveryState == NotificationDeliveryState.Attempted);
        var delivered = records.Single(record => record.DeliveryState == NotificationDeliveryState.Delivered);

        Assert.True(result.Attempted);
        Assert.True(result.Delivered);
        Assert.Equal(eventName, attempted.EventName);
        Assert.Equal(NotificationChannels.ClaudeChannel, attempted.Channel);
        Assert.Equal(NotificationChannels.ClaudeChannel, delivered.Channel);
        Assert.Equal(attempted.PayloadJson, Assert.Single(transport.Payloads));
        Assert.DoesNotContain("FULL_OUTPUT_SHOULD_NOT_BE_INCLUDED", attempted.PayloadJson);
        Assert.DoesNotContain("secret-token", attempted.PayloadJson);
        Assert.DoesNotContain("do not notify this prompt", attempted.PayloadJson);

        using var document = JsonDocument.Parse(attempted.PayloadJson!);
        var root = document.RootElement;
        Assert.Equal(ClaudeChannelProtocol.ChannelNotificationMethod, root.GetProperty("method").GetString());
        var meta = root.GetProperty("params").GetProperty("meta");
        Assert.Equal(eventName, meta.GetProperty("event").GetString());
        Assert.Equal(job.JobId, meta.GetProperty("job_id").GetString());
        Assert.Equal(UsageReporter.UnknownStatusline, meta.GetProperty("statusline").GetString());
        Assert.True(ClaudeChannelNotifier.IsWithinChannelBudget(
            workspace.Notifier.CreateNotification(new NotificationDispatchRequest
            {
                EventName = eventName,
                Job = job,
                ChannelEnabled = true
            }, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task QueueItemFailurePayloadUsesIdentifiersAndSanitizedErrorOnly()
    {
        using var workspace = TemporaryNotificationWorkspace.Create();
        var dispatcher = workspace.CreateDispatcher(new RecordingClaudeChannelTransport());
        var job = workspace.CreateJob("job_queue_failed", JobState.Completed) with
        {
            NotificationMode = NotificationModes.Channel
        };
        var item = new QueueItemSummary
        {
            JobId = job.JobId,
            QueueItemId = "queue_1",
            Status = QueueItemState.Failed,
            PromptSummary = "do not leak prompt token=prompt-secret",
            LastError = "password=queue-secret delivery failed"
        };

        await dispatcher.DispatchQueueItemFailedAsync(job, item, channelEnabled: true);

        var attempted = (await workspace.NotificationStore.ReadAsync(job.JobId))
            .Single(record => record.DeliveryState == NotificationDeliveryState.Attempted);
        Assert.Contains("queue_1", attempted.PayloadJson);
        Assert.DoesNotContain("prompt-secret", attempted.PayloadJson);
        Assert.DoesNotContain("queue-secret", attempted.PayloadJson);
        Assert.Contains("[redacted]", attempted.PayloadJson);
    }

    [Fact]
    public async Task DisabledFallbackModeRecordsPollingAttemptAndDoesNotUseChannel()
    {
        using var workspace = TemporaryNotificationWorkspace.Create();
        var transport = new RecordingClaudeChannelTransport();
        var dispatcher = workspace.CreateDispatcher(transport);
        var job = workspace.CreateJob("job_disabled", JobState.Completed);

        var result = await dispatcher.DispatchAsync(new NotificationDispatchRequest
        {
            EventName = NotificationEventNames.JobCompleted,
            Job = job,
            ChannelEnabled = false
        });

        var record = Assert.Single(await workspace.NotificationStore.ReadAsync(job.JobId));
        Assert.True(result.Attempted);
        Assert.False(result.Delivered);
        Assert.Equal(NotificationChannels.PollingFallback, record.Channel);
        Assert.Equal(NotificationDeliveryState.Attempted, record.DeliveryState);
        Assert.Null(record.PayloadJson);
        Assert.Empty(transport.Payloads);
    }

    [Fact]
    public async Task ChannelFailureIsObservableAndDoesNotThrow()
    {
        using var workspace = TemporaryNotificationWorkspace.Create();
        var dispatcher = workspace.CreateDispatcher(new RecordingClaudeChannelTransport
        {
            Failure = "transport token=failure-secret failed"
        });
        var job = workspace.CreateJob("job_failed_delivery", JobState.Completed) with
        {
            NotificationMode = NotificationModes.Channel
        };

        var result = await dispatcher.DispatchAsync(new NotificationDispatchRequest
        {
            EventName = NotificationEventNames.JobCompleted,
            Job = job,
            ChannelEnabled = true
        });

        var records = await workspace.NotificationStore.ReadAsync(job.JobId);
        Assert.True(result.Failed);
        var failed = records.Single(record => record.DeliveryState == NotificationDeliveryState.Failed);
        Assert.Contains("[redacted]", failed.Error);
        Assert.DoesNotContain("failure-secret", failed.Error);
    }

    private sealed class TemporaryNotificationWorkspace : IDisposable
    {
        private TemporaryNotificationWorkspace(string root)
        {
            Root = root;
            Paths = new ManagerStatePaths(Path.Combine(root, ".codex-manager"));
            QueueStore = new QueueStore(Paths);
            NotificationStore = new NotificationStore(Paths);
        }

        public string Root { get; }

        public ManagerStatePaths Paths { get; }

        public QueueStore QueueStore { get; }

        public NotificationStore NotificationStore { get; }

        public ClaudeChannelNotifier Notifier { get; private set; } =
            new(new DisabledClaudeChannelTransport());

        public static TemporaryNotificationWorkspace Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "claude-codex-mcp-notification-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TemporaryNotificationWorkspace(root);
        }

        public NotificationDispatcher CreateDispatcher(IClaudeChannelTransport transport)
        {
            Notifier = new ClaudeChannelNotifier(transport);
            return new NotificationDispatcher(
                NotificationStore,
                Notifier,
                NullLogger<NotificationDispatcher>.Instance);
        }

        public CodexJobRecord CreateJob(string jobId, JobState state) => new()
        {
            JobId = jobId,
            CreatedAt = DateTimeOffset.Parse("2026-04-23T12:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-04-23T12:00:00Z"),
            Profile = "implementation",
            Workflow = "direct",
            Repo = Root,
            Title = "Notification test",
            Status = state,
            PromptSummary = "prompt token=prompt-secret",
            CodexThreadId = "thread-1",
            CodexTurnId = "turn-1",
            CodexSessionId = "session-1",
            WaitingForInput = state == JobState.WaitingForInput
                ? new WaitingForInputRecord { RequestId = "request-1", Prompt = "do not notify this prompt" }
                : null,
            LogPath = Paths.GetRelativeLogPath(jobId),
            InputQueue = QueueStore.CreateEmptySummary(jobId),
            NotificationMode = NotificationModes.Disabled,
            NotificationLogPath = Paths.GetRelativeNotificationLogPath(jobId)
        };

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
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
