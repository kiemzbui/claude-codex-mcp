namespace ClaudeCodexMcp.Notifications;

public interface IClaudeChannelTransport
{
    Task<ClaudeChannelDeliveryResult> SendAsync(
        string payloadJson,
        CancellationToken cancellationToken = default);
}

public sealed class DisabledClaudeChannelTransport : IClaudeChannelTransport
{
    public Task<ClaudeChannelDeliveryResult> SendAsync(
        string payloadJson,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ClaudeChannelDeliveryResult.Failure(
            "Claude channel transport is disabled until live --channels delivery is verified."));
}
