using System;
using System.Collections.Generic;
using System.IO;
using ClaudeCodexMcp;
using ClaudeCodexMcp.Backend;
using ClaudeCodexMcp.Configuration;
using ClaudeCodexMcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClaudeCodexMcp.Tests;

public sealed class HostConfigurationTests
{
    [Fact]
    public void HostBindsManagerOptionsAndWritesLogsOutsideStdout()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "claude-codex-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var originalOut = Console.Out;
        using var stdout = new StringWriter();
        Console.SetOut(stdout);

        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = [],
                ContentRootPath = tempRoot
            });

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["manager:stateDirectory"] = ".state",
                ["manager:logging:enableFileLogging"] = "true",
                ["manager:logging:filePath"] = "logs/test.log",
                ["manager:logging:minimumLevel"] = "Information",
                ["manager:profiles:default:repo"] = tempRoot,
                ["manager:profiles:default:allowedRepos:0"] = tempRoot,
                ["manager:profiles:default:taskPrefix"] = "Use the repository instructions.",
                ["manager:profiles:default:backend"] = "appServer",
                ["manager:profiles:default:readOnly"] = "false",
                ["manager:profiles:default:permissions:sandbox"] = "workspace-write",
                ["manager:profiles:default:defaultWorkflow"] = "direct",
                ["manager:profiles:default:allowedWorkflows:0"] = "direct",
                ["manager:profiles:default:channelNotifications:enabled"] = "false",
                ["manager:profiles:default:defaultModel"] = "codex-default",
                ["manager:profiles:default:defaultEffort"] = "medium",
                ["manager:profiles:default:fastMode"] = "false"
            });

            ClaudeCodexMcpHost.Configure(builder);
            using var host = builder.Build();

            var options = host.Services.GetRequiredService<IOptions<ManagerOptions>>().Value;
            var profile = Assert.Single(options.Profiles);
            Assert.Equal("default", profile.Key);
            Assert.Equal(tempRoot, profile.Value.Repo);
            Assert.Equal(1, profile.Value.MaxConcurrentJobs);
            Assert.NotNull(host.Services.GetRequiredService<ICodexBackendSelector>());
            Assert.NotNull(host.Services.GetRequiredService<CodexToolService>());

            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("smoke");
            logger.LogInformation("stage one smoke log");

            var logPath = Path.Combine(tempRoot, ".state", "logs", "test.log");
            Assert.True(File.Exists(logPath));
            Assert.Contains("stage one smoke log", File.ReadAllText(logPath));
            Assert.Equal(string.Empty, stdout.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
