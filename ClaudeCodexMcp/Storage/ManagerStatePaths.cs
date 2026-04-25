using System.IO;

namespace ClaudeCodexMcp.Storage;

public sealed class ManagerStatePaths
{
    public ManagerStatePaths(string stateDirectory)
    {
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(stateDirectory) ? ".codex-manager" : stateDirectory);
        JobsDirectory = Path.Combine(Root, "jobs");
        QueuesDirectory = Path.Combine(Root, "queues");
        LogsDirectory = Path.Combine(Root, "logs");
        NotificationsDirectory = Path.Combine(Root, "notifications");
        WakeSignalsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex-manager",
            "wake-signals");
        CacheDirectory = Path.Combine(Root, "cache");
    }

    public string Root { get; }

    public string JobsDirectory { get; }

    public string QueuesDirectory { get; }

    public string LogsDirectory { get; }

    public string NotificationsDirectory { get; }

    public string WakeSignalsDirectory { get; }

    public string CacheDirectory { get; }

    public string JobIndexPath => Path.Combine(JobsDirectory, "index.json");

    public string SkillsCachePath => Path.Combine(CacheDirectory, "skills.json");

    public string AgentsCachePath => Path.Combine(CacheDirectory, "agents.json");

    public string GetJobPath(string jobId) => Path.Combine(JobsDirectory, $"{NormalizeFileName(jobId)}.json");

    public string GetQueuePath(string jobId) => Path.Combine(QueuesDirectory, $"{NormalizeFileName(jobId)}.json");

    public string GetLogPath(string jobId) => Path.Combine(LogsDirectory, $"{NormalizeFileName(jobId)}.jsonl");

    public string GetNotificationLogPath(string jobId) => Path.Combine(NotificationsDirectory, $"{NormalizeFileName(jobId)}.jsonl");

    public string GetWakeSessionDirectory(string wakeSessionId) =>
        Path.Combine(WakeSignalsDirectory, NormalizeFileName(wakeSessionId));

    public string GetWakeSignalPath(string wakeSessionId, string jobId) =>
        Path.Combine(GetWakeSessionDirectory(wakeSessionId), $"{NormalizeFileName(jobId)}.json");

    public string GetRelativeQueuePath(string jobId) => Path.Combine(".codex-manager", "queues", $"{NormalizeFileName(jobId)}.json");

    public string GetRelativeLogPath(string jobId) => Path.Combine(".codex-manager", "logs", $"{NormalizeFileName(jobId)}.jsonl");

    public string GetRelativeNotificationLogPath(string jobId) =>
        Path.Combine(".codex-manager", "notifications", $"{NormalizeFileName(jobId)}.jsonl");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(JobsDirectory);
        Directory.CreateDirectory(QueuesDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(NotificationsDirectory);
        Directory.CreateDirectory(WakeSignalsDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }

    private static string NormalizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty id is required.", nameof(value));
        }

        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidCharacter, '_');
        }

        return value.Trim();
    }
}
