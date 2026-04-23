using System.Text;
using System.Text.Json;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Storage;
using ClaudeCodexMcp.Usage;

namespace ClaudeCodexMcp.Notifications;

public sealed class ClaudeChannelNotifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IClaudeChannelTransport transport;
    private readonly UsageReporter usageReporter;

    public ClaudeChannelNotifier(IClaudeChannelTransport transport, UsageReporter? usageReporter = null)
    {
        this.transport = transport;
        this.usageReporter = usageReporter ?? new UsageReporter();
    }

    public async Task<ClaudeChannelDeliveryResult> NotifyAsync(
        ClaudeChannelNotification notification,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = Serialize(notification);
        var payloadBytes = Encoding.UTF8.GetByteCount(payloadJson);
        if (payloadBytes > ClaudeChannelProtocol.ChannelEventHardCapBytes)
        {
            return ClaudeChannelDeliveryResult.Failure(
                $"Claude channel payload exceeded {ClaudeChannelProtocol.ChannelEventHardCapBytes} bytes.");
        }

        try
        {
            return await transport.SendAsync(payloadJson, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ClaudeChannelDeliveryResult.Failure(ProjectionSanitizer.ToSummary(exception.Message));
        }
    }

    public ClaudeChannelNotification CreateNotification(
        NotificationDispatchRequest request,
        DateTimeOffset timestamp)
    {
        var job = request.Job;
        var eventLabel = FormatEventLabel(request.EventName);
        var status = job.Status.ToString().ToLowerInvariant();
        var title = ProjectionSanitizer.ToSummary(job.Title, 160);
        var content = ProjectionSanitizer.ToSummary(
            $"Codex {eventLabel}: {title} ({job.JobId})",
            384);

        return new ClaudeChannelNotification
        {
            Params = new ClaudeChannelNotificationParams
            {
                Content = content,
                Meta = new ClaudeChannelNotificationMetadata
                {
                    Event = request.EventName,
                    JobId = ProjectionSanitizer.ToSummary(job.JobId, 128),
                    Title = title,
                    Status = status,
                    Statusline = usageReporter.CreateStatusline(job.UsageSnapshot),
                    Profile = ProjectionSanitizer.ToOptionalSummary(job.Profile, 96),
                    Workflow = ProjectionSanitizer.ToOptionalSummary(job.Workflow, 96),
                    ThreadId = ProjectionSanitizer.ToOptionalSummary(job.CodexThreadId, 160),
                    TurnId = ProjectionSanitizer.ToOptionalSummary(job.CodexTurnId, 160),
                    SessionId = ProjectionSanitizer.ToOptionalSummary(job.CodexSessionId, 160),
                    QueueItemId = ProjectionSanitizer.ToOptionalSummary(request.QueueItem?.QueueItemId, 128),
                    RequestId = ProjectionSanitizer.ToOptionalSummary(job.WaitingForInput?.RequestId, 128),
                    Message = ProjectionSanitizer.ToOptionalSummary(CreateCompactMessage(request), 256),
                    Timestamp = timestamp.ToUniversalTime().ToString("O")
                }
            }
        };
    }

    public static string Serialize(ClaudeChannelNotification notification) =>
        JsonSerializer.Serialize(notification, JsonOptions);

    public static int GetUtf8SizeBytes(ClaudeChannelNotification notification) =>
        Encoding.UTF8.GetByteCount(Serialize(notification));

    public static bool IsWithinChannelBudget(ClaudeChannelNotification notification) =>
        GetUtf8SizeBytes(notification) <= ClaudeChannelProtocol.ChannelEventHardCapBytes;

    private static string? CreateCompactMessage(NotificationDispatchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Message))
        {
            return request.Message;
        }

        if (request.EventName == NotificationEventNames.QueueItemFailed)
        {
            return request.QueueItem?.LastError;
        }

        if (request.EventName == NotificationEventNames.JobFailed)
        {
            return request.Job.LastError;
        }

        if (request.EventName == NotificationEventNames.JobWaitingForInput)
        {
            return "input requested";
        }

        return null;
    }

    private static string FormatEventLabel(string eventName) =>
        eventName.Replace('_', ' ');
}
