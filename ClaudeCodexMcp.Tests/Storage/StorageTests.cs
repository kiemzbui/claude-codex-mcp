using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Storage;

namespace ClaudeCodexMcp.Tests.Storage;

public sealed class StorageTests
{
    [Fact]
    public async Task JobIndexCanBeReconstructedByScanningJobRecords()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var paths = new ManagerStatePaths(workspace.StateDirectory);
        var queueStore = new QueueStore(paths);
        var store = new JobStore(paths);
        var first = CreateJob(paths, queueStore, "job_first", JobState.Running, DateTimeOffset.Parse("2026-04-22T12:00:00Z"));
        var second = CreateJob(paths, queueStore, "job_second", JobState.Completed, DateTimeOffset.Parse("2026-04-22T13:00:00Z"));

        await store.SaveAsync(first);
        await store.SaveAsync(second);
        File.Delete(paths.JobIndexPath);

        var rebuilt = await store.RebuildIndexAsync();

        Assert.True(File.Exists(paths.JobIndexPath));
        Assert.Equal(["job_second", "job_first"], rebuilt.Jobs.Select(job => job.JobId).ToArray());
        Assert.Contains(rebuilt.Jobs, job => job.JobId == "job_first" && job.Status == JobState.Running);
        Assert.Contains(rebuilt.Jobs, job => job.JobId == "job_second" && job.Title == "Title job_second");
    }

    [Fact]
    public async Task QueueStorePersistsFullPromptBodiesAndJobSummaryOnlyReferencesThem()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var paths = new ManagerStatePaths(workspace.StateDirectory);
        var queueStore = new QueueStore(paths);
        var jobStore = new JobStore(paths);
        const string fullPrompt = "FULL_QUEUE_PROMPT_UNIQUE password=hunter2 Run focused storage tests after this turn.";

        var item = await queueStore.AddAsync("job_queue", fullPrompt, "Follow-up");
        var queue = await queueStore.ReadAsync("job_queue");
        var summary = queueStore.CreateSummary(queue);
        var job = CreateJob(paths, queueStore, "job_queue", JobState.Running, DateTimeOffset.Parse("2026-04-22T12:00:00Z")) with
        {
            InputQueue = summary
        };

        await jobStore.SaveAsync(job);

        Assert.Equal(fullPrompt, queue.Items.Single().Prompt);
        Assert.Equal(item.QueueItemId, summary.NextQueueItemId);
        Assert.DoesNotContain(fullPrompt, summary.Items.Single().PromptSummary);
        Assert.DoesNotContain("hunter2", summary.Items.Single().PromptSummary);

        var jobJson = await File.ReadAllTextAsync(paths.GetJobPath("job_queue"));
        Assert.DoesNotContain(fullPrompt, jobJson);
        Assert.DoesNotContain("hunter2", jobJson);

        var queueJson = await File.ReadAllTextAsync(paths.GetQueuePath("job_queue"));
        Assert.Contains(fullPrompt, queueJson);
    }

    [Fact]
    public async Task QueuePendingItemsAreReturnedInCreatedAtFifoOrder()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var paths = new ManagerStatePaths(workspace.StateDirectory);
        var store = new QueueStore(paths);
        var later = CreateQueueItem("queue_later", "job_fifo", DateTimeOffset.Parse("2026-04-22T12:02:00Z"));
        var earlier = CreateQueueItem("queue_earlier", "job_fifo", DateTimeOffset.Parse("2026-04-22T12:01:00Z"));
        var delivered = CreateQueueItem("queue_delivered", "job_fifo", DateTimeOffset.Parse("2026-04-22T12:00:00Z")) with
        {
            Status = QueueItemState.Delivered
        };

        await store.SaveAsync(new QueueRecord
        {
            JobId = "job_fifo",
            UpdatedAt = DateTimeOffset.Parse("2026-04-22T12:03:00Z"),
            Items = [later, delivered, earlier]
        });

        var pending = await store.ReadPendingInDeliveryOrderAsync("job_fifo");

        Assert.Equal(["queue_earlier", "queue_later"], pending.Select(item => item.QueueItemId).ToArray());
    }

    [Fact]
    public async Task QueueStorePersistsDeliveryAttemptsFailuresCancellationsAndCounts()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var paths = new ManagerStatePaths(workspace.StateDirectory);
        var store = new QueueStore(paths);
        var delivered = await store.AddAsync("job_queue_counts", "deliver this", "Deliver");
        var failed = await store.AddAsync("job_queue_counts", "fail this", "Fail");
        var cancelled = await store.AddAsync("job_queue_counts", "cancel this", "Cancel");

        var attempt = await store.MarkDeliveryAttemptAsync("job_queue_counts", delivered.QueueItemId);
        await store.MarkDeliveredAsync("job_queue_counts", delivered.QueueItemId);
        await store.MarkFailedAsync("job_queue_counts", failed.QueueItemId, "backend rejected queued input");
        await store.CancelPendingAsync("job_queue_counts", cancelled.QueueItemId);
        var queue = await store.ReadAsync("job_queue_counts");
        var summary = store.CreateSummary(queue);

        Assert.Equal(1, attempt.Item?.DeliveryAttemptCount);
        Assert.Equal(QueueItemState.Delivered, queue.Items.Single(item => item.QueueItemId == delivered.QueueItemId).Status);
        Assert.Equal(QueueItemState.Failed, queue.Items.Single(item => item.QueueItemId == failed.QueueItemId).Status);
        Assert.Contains("backend rejected", queue.Items.Single(item => item.QueueItemId == failed.QueueItemId).LastError);
        Assert.Equal(QueueItemState.Cancelled, queue.Items.Single(item => item.QueueItemId == cancelled.QueueItemId).Status);
        Assert.NotNull(queue.Items.Single(item => item.QueueItemId == cancelled.QueueItemId).CancelledAt);
        Assert.Equal(0, summary.PendingCount);
        Assert.Equal(1, summary.DeliveredCount);
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(1, summary.CancelledCount);
    }

    [Fact]
    public async Task OutputStoreAppendsAndReadsJsonlEntries()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var paths = new ManagerStatePaths(workspace.StateDirectory);
        var store = new OutputStore(paths);

        await store.AppendAsync(new OutputLogEntry
        {
            JobId = "job_output",
            CreatedAt = DateTimeOffset.Parse("2026-04-22T12:00:00Z"),
            Message = "first line"
        });
        await store.AppendAsync(new OutputLogEntry
        {
            JobId = "job_output",
            CreatedAt = DateTimeOffset.Parse("2026-04-22T12:01:00Z"),
            Message = "second line"
        });

        var page = await store.ReadAsync("job_output", offset: 0, limit: 10);

        Assert.True(File.Exists(paths.GetLogPath("job_output")));
        Assert.True(page.EndOfLog);
        Assert.Equal(2, page.NextOffset);
        Assert.Equal(["first line", "second line"], page.Entries.Select(entry => entry.Message).ToArray());
    }

    [Fact]
    public async Task OutputStoreAppliesFiltersOffsetsLimitsAndEndMarkers()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var paths = new ManagerStatePaths(workspace.StateDirectory);
        var store = new OutputStore(paths);

        await store.AppendAsync(new OutputLogEntry
        {
            JobId = "job_filtered",
            ThreadId = "thread-a",
            TurnId = "turn-a",
            AgentId = "agent-a",
            Message = "first matching"
        });
        await store.AppendAsync(new OutputLogEntry
        {
            JobId = "job_filtered",
            ThreadId = "thread-b",
            TurnId = "turn-b",
            AgentId = "agent-b",
            Message = "filtered out"
        });
        await store.AppendAsync(new OutputLogEntry
        {
            JobId = "job_filtered",
            ThreadId = "thread-a",
            TurnId = "turn-c",
            AgentId = "agent-a",
            Message = "second matching"
        });

        var firstPage = await store.ReadAsync(
            "job_filtered",
            threadId: "thread-a",
            turnId: null,
            agentId: "agent-a",
            offset: 0,
            limit: 1);
        var secondPage = await store.ReadAsync(
            "job_filtered",
            threadId: "thread-a",
            turnId: null,
            agentId: "agent-a",
            offset: 1,
            limit: 1);

        Assert.False(firstPage.EndOfLog);
        Assert.Equal(1, firstPage.NextOffset);
        Assert.Equal(2, firstPage.TotalCount);
        Assert.Equal("first matching", Assert.Single(firstPage.Entries).Message);
        Assert.True(secondPage.EndOfLog);
        Assert.Equal(2, secondPage.NextOffset);
        Assert.Equal("second matching", Assert.Single(secondPage.Entries).Message);
    }

    [Fact]
    public async Task NotificationStoreAppendsAndReadsJsonlRecords()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var paths = new ManagerStatePaths(workspace.StateDirectory);
        var store = new NotificationStore(paths);

        await store.AppendAsync(new NotificationRecord
        {
            JobId = "job_notify",
            CreatedAt = DateTimeOffset.Parse("2026-04-22T12:00:00Z"),
            EventName = "job_completed",
            DeliveryState = NotificationDeliveryState.Attempted,
            PayloadSummary = "completed"
        });
        await store.AppendAsync(new NotificationRecord
        {
            JobId = "job_notify",
            CreatedAt = DateTimeOffset.Parse("2026-04-22T12:01:00Z"),
            EventName = "job_completed",
            DeliveryState = NotificationDeliveryState.Delivered,
            PayloadSummary = "delivered"
        });

        var records = await store.ReadAsync("job_notify");

        Assert.True(File.Exists(paths.GetNotificationLogPath("job_notify")));
        Assert.Equal([NotificationDeliveryState.Attempted, NotificationDeliveryState.Delivered], records.Select(record => record.DeliveryState).ToArray());
    }

    [Fact]
    public async Task DiscoveryCacheStoreUsesRequiredCachePaths()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var paths = new ManagerStatePaths(workspace.StateDirectory);
        var store = new DiscoveryCacheStore(paths);

        await store.SaveAsync(new DiscoveryCacheRecord
        {
            Kind = DiscoveryCacheKind.Skills,
            UpdatedAt = DateTimeOffset.Parse("2026-04-22T12:00:00Z"),
            Items = [new DiscoveryCacheItem { Name = "dotnet", SourceScope = "global", SourcePath = "skills/dotnet" }]
        });

        var record = await store.ReadAsync(DiscoveryCacheKind.Skills);

        Assert.True(File.Exists(Path.Combine(workspace.StateDirectory, "cache", "skills.json")));
        Assert.Equal(paths.SkillsCachePath, store.GetPath(DiscoveryCacheKind.Skills));
        Assert.Equal(paths.AgentsCachePath, store.GetPath(DiscoveryCacheKind.Agents));
        Assert.NotNull(record);
        Assert.Equal("dotnet", record.Items.Single().Name);
    }

    [Fact]
    public void DefaultProjectionRedactsAndTruncatesSensitiveFields()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var paths = new ManagerStatePaths(workspace.StateDirectory);
        var queueStore = new QueueStore(paths);
        var job = CreateJob(paths, queueStore, "job_projection", JobState.Failed, DateTimeOffset.Parse("2026-04-22T12:00:00Z")) with
        {
            PromptSummary = "Investigate token=abc123 and then " + new string('x', 900),
            LastError = "password=secret-value failed"
        };

        var projection = JobProjection.ToDefault(job);

        Assert.DoesNotContain("abc123", projection.PromptSummary);
        Assert.DoesNotContain("secret-value", projection.LastError);
        Assert.Contains("[redacted]", projection.PromptSummary);
        Assert.Contains("[truncated]", projection.PromptSummary);
    }

    private static CodexJobRecord CreateJob(
        ManagerStatePaths paths,
        QueueStore queueStore,
        string jobId,
        JobState status,
        DateTimeOffset timestamp) => new()
    {
        JobId = jobId,
        CreatedAt = timestamp,
        UpdatedAt = timestamp,
        Profile = "implementation",
        Workflow = "direct",
        Repo = Path.GetTempPath(),
        Title = $"Title {jobId}",
        Status = status,
        PromptSummary = $"Prompt summary {jobId}",
        Model = "gpt-5.4",
        Effort = "medium",
        FastMode = false,
        ServiceTier = "normal",
        LogPath = paths.GetRelativeLogPath(jobId),
        InputQueue = queueStore.CreateEmptySummary(jobId),
        NotificationMode = "polling",
        NotificationLogPath = paths.GetRelativeNotificationLogPath(jobId)
    };

    private static QueueItemRecord CreateQueueItem(string queueItemId, string jobId, DateTimeOffset createdAt) => new()
    {
        QueueItemId = queueItemId,
        JobId = jobId,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        Status = QueueItemState.Pending,
        Title = queueItemId,
        Prompt = $"Full prompt {queueItemId}",
        PromptSummary = $"Prompt summary {queueItemId}",
        PromptRef = $".codex-manager/queues/{jobId}.json#{queueItemId}"
    };

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
            var root = Path.Combine(Path.GetTempPath(), "claude-codex-mcp-storage-tests", Guid.NewGuid().ToString("N"));
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
