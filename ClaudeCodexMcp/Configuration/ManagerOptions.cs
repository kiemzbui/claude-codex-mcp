using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace ClaudeCodexMcp.Configuration;

public sealed class ManagerOptions
{
    public const string SectionName = "manager";

    public string StateDirectory { get; set; } = ".codex-manager";

    public ManagerLoggingOptions Logging { get; set; } = new();

    public Dictionary<string, ProfileOptions> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ResolveStateDirectory(string contentRootPath)
    {
        var configuredPath = string.IsNullOrWhiteSpace(StateDirectory)
            ? ".codex-manager"
            : StateDirectory;

        return Path.GetFullPath(Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.Combine(contentRootPath, configuredPath));
    }

    public string ResolveLogFilePath(string contentRootPath)
    {
        var configuredPath = string.IsNullOrWhiteSpace(Logging.FilePath)
            ? "logs/server.log"
            : Logging.FilePath;

        return Path.GetFullPath(Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.Combine(ResolveStateDirectory(contentRootPath), configuredPath));
    }
}

public sealed class ManagerLoggingOptions
{
    public bool EnableFileLogging { get; set; } = true;

    public string FilePath { get; set; } = "logs/server.log";

    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}

public sealed class ProfileOptions
{
    public string? Repo { get; set; }

    public List<string> AllowedRepos { get; set; } = [];

    public string? TaskPrefix { get; set; }

    public string? Backend { get; set; }

    public bool ReadOnly { get; set; }

    public Dictionary<string, string> Permissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? DefaultWorkflow { get; set; }

    public List<string> AllowedWorkflows { get; set; } = [];

    public int MaxConcurrentJobs { get; set; } = 1;

    public ChannelNotificationOptions ChannelNotifications { get; set; } = new();

    public string? DefaultModel { get; set; }

    public string? DefaultEffort { get; set; }

    public bool FastMode { get; set; }
}

public sealed class ChannelNotificationOptions
{
    public bool Enabled { get; set; }
}
