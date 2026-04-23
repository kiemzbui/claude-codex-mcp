using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Storage;
using Microsoft.Extensions.Logging;

namespace ClaudeCodexMcp.Notifications;

public sealed class NotificationDispatcher
{
    private readonly NotificationStore notificationStore;
    private readonly ClaudeChannelNotifier channelNotifier;
    private readonly ILogger<NotificationDispatcher> logger;

    public NotificationDispatcher(
        NotificationStore notificationStore,
        ClaudeChannelNotifier channelNotifier,
        ILogger<NotificationDispatcher> logger)
    {
        this.notificationStore = notificationStore;
        this.channelNotifier = channelNotifier;
        this.logger = logger;
    }

    public async Task<NotificationDispatchResult> DispatchAsync(
        NotificationDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Job.JobId);

        var timestamp = DateTimeOffset.UtcNow;
        var channel = request.ChannelEnabled
            ? NotificationChannels.ClaudeChannel
            : NotificationChannels.PollingFallback;
        var payload = channelNotifier.CreateNotification(request, timestamp);
        var payloadJson = request.ChannelEnabled ? ClaudeChannelNotifier.Serialize(payload) : null;
        var payloadSummary = CreatePayloadSummary(request, payload);

        await AppendSafelyAsync(new NotificationRecord
        {
            CreatedAt = timestamp,
            JobId = request.Job.JobId,
            EventName = request.EventName,
            DeliveryState = NotificationDeliveryState.Attempted,
            Channel = channel,
            PayloadSummary = payloadSummary,
            PayloadJson = payloadJson
        }, cancellationToken);

        if (!request.ChannelEnabled)
        {
            return new NotificationDispatchResult
            {
                Attempted = true,
                Channel = channel
            };
        }

        var result = await channelNotifier.NotifyAsync(payload, cancellationToken);
        var deliveryState = result.Delivered
            ? NotificationDeliveryState.Delivered
            : NotificationDeliveryState.Failed;
        var error = result.Delivered ? null : ProjectionSanitizer.ToOptionalSummary(result.Error);
        await AppendSafelyAsync(new NotificationRecord
        {
            CreatedAt = DateTimeOffset.UtcNow,
            JobId = request.Job.JobId,
            EventName = request.EventName,
            DeliveryState = deliveryState,
            Channel = channel,
            PayloadSummary = payloadSummary,
            PayloadJson = payloadJson,
            Error = error
        }, cancellationToken);

        return new NotificationDispatchResult
        {
            Attempted = true,
            Delivered = result.Delivered,
            Failed = !result.Delivered,
            Channel = channel,
            Error = error
        };
    }

    public Task<NotificationDispatchResult?> DispatchJobStateChangeAsync(
        CodexJobRecord before,
        CodexJobRecord after,
        bool channelEnabled,
        CancellationToken cancellationToken = default)
    {
        var eventName = GetLifecycleEventName(before.Status, after.Status);
        if (eventName is null)
        {
            return Task.FromResult<NotificationDispatchResult?>(null);
        }

        return DispatchNullableAsync(new NotificationDispatchRequest
        {
            EventName = eventName,
            Job = after,
            ChannelEnabled = channelEnabled
        }, cancellationToken);
    }

    public Task<NotificationDispatchResult> DispatchQueueItemFailedAsync(
        CodexJobRecord job,
        QueueItemSummary queueItem,
        bool channelEnabled,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(new NotificationDispatchRequest
        {
            EventName = NotificationEventNames.QueueItemFailed,
            Job = job,
            QueueItem = queueItem,
            ChannelEnabled = channelEnabled,
            Message = queueItem.LastError
        }, cancellationToken);

    private async Task<NotificationDispatchResult?> DispatchNullableAsync(
        NotificationDispatchRequest request,
        CancellationToken cancellationToken) =>
        await DispatchAsync(request, cancellationToken);

    private static string? GetLifecycleEventName(JobState before, JobState after)
    {
        if (before == after)
        {
            return null;
        }

        return after switch
        {
            JobState.WaitingForInput => NotificationEventNames.JobWaitingForInput,
            JobState.Completed => NotificationEventNames.JobCompleted,
            JobState.Failed => NotificationEventNames.JobFailed,
            JobState.Cancelled => NotificationEventNames.JobCancelled,
            _ => null
        };
    }

    private async Task AppendSafelyAsync(
        NotificationRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            await notificationStore.AppendAsync(record, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Failed to persist notification record for job {JobId} event {EventName}.",
                record.JobId,
                record.EventName);
        }
    }

    private static string CreatePayloadSummary(
        NotificationDispatchRequest request,
        ClaudeChannelNotification notification) =>
        ProjectionSanitizer.ToSummary(
            $"{request.EventName} {request.Job.JobId} {notification.Params.Meta.Status} {notification.Params.Meta.Statusline}",
            512);
}
