namespace ClaudeCodexMcp.Notifications;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Server;

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
            "Claude channel transport is disabled."));
}

public sealed class McpClaudeChannelTransport : IClaudeChannelTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private readonly McpServer server;

    public McpClaudeChannelTransport(McpServer server)
    {
        this.server = server;
    }

    public async Task<ClaudeChannelDeliveryResult> SendAsync(
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonNode.Parse(payloadJson);
        if (payload is not JsonObject payloadObject)
        {
            return ClaudeChannelDeliveryResult.Failure("Claude channel payload is not a JSON object.");
        }

        var method = payloadObject["method"]?.GetValue<string>();
        if (!string.Equals(method, ClaudeChannelProtocol.ChannelNotificationMethod, StringComparison.Ordinal))
        {
            return ClaudeChannelDeliveryResult.Failure("Claude channel payload method is invalid.");
        }

        var parameters = payloadObject["params"];
        if (parameters is null)
        {
            return ClaudeChannelDeliveryResult.Failure("Claude channel payload params are missing.");
        }

        await server.SendNotificationAsync(
            ClaudeChannelProtocol.ChannelNotificationMethod,
            parameters,
            JsonOptions,
            cancellationToken);
        return ClaudeChannelDeliveryResult.Success();
    }
}
