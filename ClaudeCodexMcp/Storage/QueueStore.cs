using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodexMcp.Domain;

namespace ClaudeCodexMcp.Storage;

public sealed class QueueStore
{
    private readonly ManagerStatePaths paths;

    public QueueStore(ManagerStatePaths paths)
    {
        this.paths = paths;
        this.paths.EnsureDirectories();
    }

    public async Task<QueueRecord> ReadAsync(string jobId, CancellationToken cancellationToken = default) =>
        await StorageJson.ReadAsync<QueueRecord>(paths.GetQueuePath(jobId), cancellationToken)
            ?? new QueueRecord { JobId = jobId };

    public async Task SaveAsync(QueueRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(record.JobId);
        var ordered = record.Items
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.QueueItemId, StringComparer.Ordinal)
            .ToArray();

        await StorageJson.WriteAtomicallyAsync(
            paths.GetQueuePath(record.JobId),
            record with { Items = ordered, UpdatedAt = record.UpdatedAt == default ? DateTimeOffset.UtcNow : record.UpdatedAt },
            cancellationToken);
    }

    public async Task<QueueItemRecord> AddAsync(
        string jobId,
        string prompt,
        string? title = null,
        DateTimeOffset? createdAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var record = await ReadAsync(jobId, cancellationToken);
        var now = createdAt ?? DateTimeOffset.UtcNow;
        var itemId = $"queue_{Guid.NewGuid():N}";
        var item = new QueueItemRecord
        {
            QueueItemId = itemId,
            JobId = jobId,
            CreatedAt = now,
            UpdatedAt = now,
            Status = QueueItemState.Pending,
            Title = ProjectionSanitizer.ToOptionalSummary(title, 160),
            Prompt = prompt,
            PromptSummary = ProjectionSanitizer.ToSummary(prompt),
            PromptRef = $"{paths.GetRelativeQueuePath(jobId)}#{itemId}"
        };

        await SaveAsync(record with
        {
            UpdatedAt = now,
            Items = record.Items.Concat([item]).ToArray()
        }, cancellationToken);

        return item;
    }

    public async Task<IReadOnlyList<QueueItemRecord>> ReadPendingInDeliveryOrderAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var record = await ReadAsync(jobId, cancellationToken);
        return record.Items
            .Where(item => item.Status == QueueItemState.Pending)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.QueueItemId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<(QueueRecord Queue, QueueItemRecord? Item)> MarkDeliveryAttemptAsync(
        string jobId,
        string queueItemId,
        DateTimeOffset? attemptedAt = null,
        CancellationToken cancellationToken = default)
    {
        var now = attemptedAt ?? DateTimeOffset.UtcNow;
        return await UpdateItemAsync(
            jobId,
            queueItemId,
            item => item with
            {
                UpdatedAt = now,
                DeliveryAttemptCount = item.DeliveryAttemptCount + 1,
                LastError = null
            },
            now,
            cancellationToken);
    }

    public async Task<(QueueRecord Queue, QueueItemRecord? Item)> MarkDeliveredAsync(
        string jobId,
        string queueItemId,
        DateTimeOffset? deliveredAt = null,
        CancellationToken cancellationToken = default)
    {
        var now = deliveredAt ?? DateTimeOffset.UtcNow;
        return await UpdateItemAsync(
            jobId,
            queueItemId,
            item => item with
            {
                UpdatedAt = now,
                Status = QueueItemState.Delivered,
                DeliveredAt = now,
                LastError = null
            },
            now,
            cancellationToken);
    }

    public async Task<(QueueRecord Queue, QueueItemRecord? Item)> MarkFailedAsync(
        string jobId,
        string queueItemId,
        string error,
        DateTimeOffset? failedAt = null,
        CancellationToken cancellationToken = default)
    {
        var now = failedAt ?? DateTimeOffset.UtcNow;
        return await UpdateItemAsync(
            jobId,
            queueItemId,
            item => item with
            {
                UpdatedAt = now,
                Status = QueueItemState.Failed,
                LastError = ProjectionSanitizer.ToSummary(error)
            },
            now,
            cancellationToken);
    }

    public async Task<(QueueRecord Queue, QueueItemRecord? Item)> CancelPendingAsync(
        string jobId,
        string queueItemId,
        DateTimeOffset? cancelledAt = null,
        CancellationToken cancellationToken = default)
    {
        var now = cancelledAt ?? DateTimeOffset.UtcNow;
        return await UpdateItemAsync(
            jobId,
            queueItemId,
            item => item.Status == QueueItemState.Pending
                ? item with
                {
                    UpdatedAt = now,
                    Status = QueueItemState.Cancelled,
                    CancelledAt = now,
                    LastError = null
                }
                : item,
            now,
            cancellationToken);
    }

    public JobQueueSummary CreateSummary(QueueRecord record)
    {
        var ordered = record.Items
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.QueueItemId, StringComparer.Ordinal)
            .ToArray();
        var next = ordered.FirstOrDefault(item => item.Status == QueueItemState.Pending);

        return new JobQueueSummary
        {
            PendingCount = ordered.Count(item => item.Status == QueueItemState.Pending),
            DeliveredCount = ordered.Count(item => item.Status == QueueItemState.Delivered),
            FailedCount = ordered.Count(item => item.Status == QueueItemState.Failed),
            CancelledCount = ordered.Count(item => item.Status == QueueItemState.Cancelled),
            NextQueueItemId = next?.QueueItemId,
            QueuePath = paths.GetRelativeQueuePath(record.JobId),
            Items = ordered.Select(ToSummary).ToArray()
        };
    }

    public JobQueueSummary CreateEmptySummary(string jobId) => new()
    {
        QueuePath = paths.GetRelativeQueuePath(jobId)
    };

    public static QueueItemSummary ToSummary(QueueItemRecord item) => new()
    {
        QueueItemId = item.QueueItemId,
        JobId = item.JobId,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        Status = item.Status,
        Title = ProjectionSanitizer.ToOptionalSummary(item.Title, 160),
        PromptSummary = ProjectionSanitizer.ToSummary(item.PromptSummary),
        PromptRef = item.PromptRef,
        DeliveryAttemptCount = item.DeliveryAttemptCount,
        DeliveredAt = item.DeliveredAt,
        CancelledAt = item.CancelledAt,
        LastError = ProjectionSanitizer.ToOptionalSummary(item.LastError)
    };

    private async Task<(QueueRecord Queue, QueueItemRecord? Item)> UpdateItemAsync(
        string jobId,
        string queueItemId,
        Func<QueueItemRecord, QueueItemRecord> update,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueItemId);

        var record = await ReadAsync(jobId, cancellationToken);
        QueueItemRecord? updatedItem = null;
        var updatedItems = record.Items
            .Select(item =>
            {
                if (!string.Equals(item.QueueItemId, queueItemId, StringComparison.Ordinal))
                {
                    return item;
                }

                updatedItem = update(item);
                return updatedItem;
            })
            .ToArray();

        if (updatedItem is null)
        {
            return (record, null);
        }

        var updatedRecord = record with
        {
            UpdatedAt = updatedAt,
            Items = updatedItems
        };
        await SaveAsync(updatedRecord, cancellationToken);
        return (updatedRecord, updatedItem);
    }
}
