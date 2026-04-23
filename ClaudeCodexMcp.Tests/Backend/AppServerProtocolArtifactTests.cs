using System.IO;
using System.Linq;
using ClaudeCodexMcp.Backend.AppServerFeasibility;
using ClaudeCodexMcp.Backend.AppServerProtocol.CSharp;

namespace ClaudeCodexMcp.Tests.Backend;

public sealed class AppServerProtocolArtifactTests
{
    [Fact]
    public void ApprovedMvpMethodsArePresentInGeneratedSchemaAndTypeScript()
    {
        var root = FindRepoRoot();
        var schema = File.ReadAllText(Path.Combine(root, "ClaudeCodexMcp", "Backend", "AppServerProtocol", "Schema", "ClientRequest.json"));
        var typeScript = File.ReadAllText(Path.Combine(root, "ClaudeCodexMcp", "Backend", "AppServerProtocol", "TypeScript", "ClientRequest.ts"));

        foreach (var method in AppServerProtocolNames.ApprovedMvpMethods)
        {
            Assert.Contains($"\"{method}\"", schema);
            Assert.Contains($"\"method\": \"{method}\"", typeScript);
        }
    }

    [Fact]
    public void ApprovedMvpNotificationsArePresentInGeneratedSchemaAndTypeScript()
    {
        var root = FindRepoRoot();
        var schema = File.ReadAllText(Path.Combine(root, "ClaudeCodexMcp", "Backend", "AppServerProtocol", "Schema", "ServerNotification.json"));
        var typeScript = File.ReadAllText(Path.Combine(root, "ClaudeCodexMcp", "Backend", "AppServerProtocol", "TypeScript", "ServerNotification.ts"));

        foreach (var notification in AppServerProtocolNames.ApprovedMvpNotifications)
        {
            Assert.Contains($"\"{notification}\"", schema);
            Assert.Contains($"\"method\": \"{notification}\"", typeScript);
        }
    }

    [Fact]
    public void CSharpBindingSurfaceStaysLimitedToApprovedMvpSubset()
    {
        Assert.Equal(17, AppServerProtocolNames.ApprovedMvpMethods.Length);
        Assert.Equal(13, AppServerProtocolNames.ApprovedMvpNotifications.Length);
        Assert.DoesNotContain("fs/readFile", AppServerProtocolNames.ApprovedMvpMethods);
        Assert.DoesNotContain("command/exec", AppServerProtocolNames.ApprovedMvpMethods);
        Assert.DoesNotContain("plugin/install", AppServerProtocolNames.ApprovedMvpMethods);
    }

    [Fact]
    public void ProbeBuildsCodexStartTaskEquivalentRequests()
    {
        var options = new AppServerProbeOptions
        {
            WorkingDirectory = @"C:\repo",
            Prompt = "say ok"
        };

        var initialize = AppServerProbe.CreateInitializeParams();
        var threadStart = AppServerProbe.CreateThreadStartParams(options);
        var turnStart = AppServerProbe.CreateTurnStartParams("thread-1", options.Prompt);

        Assert.True(initialize.Capabilities?.ExperimentalApi);
        Assert.Equal(@"C:\repo", threadStart.Cwd);
        Assert.Equal("never", threadStart.ApprovalPolicy);
        Assert.Equal("read-only", threadStart.Sandbox);
        Assert.True(threadStart.PersistExtendedHistory);
        Assert.Equal("thread-1", turnStart.ThreadId);
        Assert.Equal("say ok", turnStart.Input.Single().Text);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClaudeCodexMcp.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repo root containing ClaudeCodexMcp.sln.");
    }
}
