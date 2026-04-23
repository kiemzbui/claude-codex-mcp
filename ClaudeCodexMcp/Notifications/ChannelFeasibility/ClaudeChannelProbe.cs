using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeCodexMcp.Notifications.ChannelFeasibility;

public static partial class ClaudeChannelProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Version MinimumSupportedVersion = new(2, 1, 80);
    private static readonly Version TargetVersion = new(2, 1, 117);

    public static ClaudeChannelDeclaration CreateDeclaration(bool includePermissionRelay = false)
    {
        var experimental = new Dictionary<string, EmptyCapabilityOptions>
        {
            [ClaudeChannelProtocolNames.ChannelCapability] = new()
        };

        if (includePermissionRelay)
        {
            experimental[ClaudeChannelProtocolNames.PermissionRelayCapability] = new();
        }

        return new ClaudeChannelDeclaration
        {
            Capabilities = new ClaudeChannelCapabilities
            {
                Experimental = experimental
            },
            Instructions = "Claude Codex MCP channel probe only. Channel messages are wake-up signals; call MCP status/result tools for authoritative state."
        };
    }

    public static ClaudeChannelJsonRpcNotification CreateProbeNotification(
        string probeId,
        DateTimeOffset timestamp,
        string jobId = "channel_probe",
        string status = "probe")
    {
        return new ClaudeChannelJsonRpcNotification
        {
            Params = new ClaudeChannelNotificationParams
            {
                Content = "Claude Codex MCP channel feasibility probe.",
                Meta = new ClaudeChannelProbeMetadata
                {
                    JobId = jobId,
                    Status = status,
                    ProbeId = probeId,
                    Timestamp = timestamp.ToUniversalTime().ToString("O")
                }
            }
        };
    }

    public static string Serialize(object payload) => JsonSerializer.Serialize(payload, JsonOptions);

    public static int GetUtf8SizeBytes(object payload) => Encoding.UTF8.GetByteCount(Serialize(payload));

    public static bool IsWithinChannelBudget(object payload) =>
        GetUtf8SizeBytes(payload) <= ClaudeChannelProtocolNames.ChannelEventHardCapBytes;

    public static ClaudeCodeVersionCheck CheckVersion(string rawVersion)
    {
        var parsed = TryParseVersion(rawVersion);
        return new ClaudeCodeVersionCheck(
            rawVersion,
            parsed,
            parsed is not null && parsed >= MinimumSupportedVersion,
            parsed is not null &&
                parsed.Major == TargetVersion.Major &&
                parsed.Minor == TargetVersion.Minor &&
                parsed.Build == TargetVersion.Build);
    }

    public static Version? TryParseVersion(string rawVersion)
    {
        var match = VersionPattern().Match(rawVersion);
        return match.Success
            ? new Version(
                int.Parse(match.Groups["major"].Value),
                int.Parse(match.Groups["minor"].Value),
                int.Parse(match.Groups["patch"].Value))
            : null;
    }

    [GeneratedRegex(@"(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
