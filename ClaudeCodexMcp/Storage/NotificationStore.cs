using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodexMcp.Domain;

namespace ClaudeCodexMcp.Storage;

public sealed class NotificationStore
{
    private readonly ManagerStatePaths paths;

    public NotificationStore(ManagerStatePaths paths)
    {
        this.paths = paths;
        this.paths.EnsureDirectories();
    }

    public async Task AppendAsync(NotificationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(record.JobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.EventName);
        var normalized = record.CreatedAt == default ? record with { CreatedAt = DateTimeOffset.UtcNow } : record;
        var path = paths.GetNotificationLogPath(normalized.JobId);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync(StorageJson.SerializeLine(normalized).AsMemory(), cancellationToken);
    }

    public Task<IReadOnlyList<NotificationRecord>> ReadAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return OutputStore.ReadJsonLinesAsync<NotificationRecord>(paths.GetNotificationLogPath(jobId), cancellationToken);
    }
}
