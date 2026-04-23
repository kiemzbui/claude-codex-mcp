using System.Text.Json;
using ClaudeCodexMcp.Notifications.ChannelFeasibility;

namespace ClaudeCodexMcp.Tests.Notifications;

public sealed class ClaudeChannelProbeTests
{
    [Fact]
    public void DeclarationAdvertisesRequiredClaudeChannelCapability()
    {
        var declaration = ClaudeChannelProbe.CreateDeclaration();

        Assert.Contains(
            ClaudeChannelProtocolNames.ChannelCapability,
            declaration.Capabilities.Experimental.Keys);
        Assert.DoesNotContain(
            ClaudeChannelProtocolNames.PermissionRelayCapability,
            declaration.Capabilities.Experimental.Keys);
    }

    [Fact]
    public void DeclarationCanOptIntoPermissionRelayOnlyWhenRequested()
    {
        var declaration = ClaudeChannelProbe.CreateDeclaration(includePermissionRelay: true);

        Assert.Contains(
            ClaudeChannelProtocolNames.PermissionRelayCapability,
            declaration.Capabilities.Experimental.Keys);
    }

    [Fact]
    public void ProbeNotificationUsesCompactClaudeChannelShape()
    {
        var notification = ClaudeChannelProbe.CreateProbeNotification(
            "probe-1",
            DateTimeOffset.Parse("2026-04-23T12:00:00Z"));
        var json = ClaudeChannelProbe.Serialize(notification);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal(
            ClaudeChannelProtocolNames.ChannelNotification,
            document.RootElement.GetProperty("method").GetString());
        Assert.False(document.RootElement.TryGetProperty("id", out _));
        Assert.Equal(
            "Claude Codex MCP channel feasibility probe.",
            document.RootElement.GetProperty("params").GetProperty("content").GetString());
        Assert.Equal(
            "probe-1",
            document.RootElement.GetProperty("params").GetProperty("meta").GetProperty("probe_id").GetString());
        Assert.True(ClaudeChannelProbe.IsWithinChannelBudget(notification));
    }

    [Theory]
    [InlineData("2.1.117 (Claude Code)", true, true)]
    [InlineData("2.1.80 (Claude Code)", true, false)]
    [InlineData("2.1.79 (Claude Code)", false, false)]
    [InlineData("not a version", false, false)]
    public void VersionCheckRecordsMinimumAndTargetVersion(string rawVersion, bool expectedMinimum, bool expectedTarget)
    {
        var result = ClaudeChannelProbe.CheckVersion(rawVersion);

        Assert.Equal(expectedMinimum, result.IsAtLeastMinimum);
        Assert.Equal(expectedTarget, result.IsTargetVersion);
    }
}
