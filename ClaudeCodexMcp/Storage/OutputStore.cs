using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodexMcp.Domain;

namespace ClaudeCodexMcp.Storage;

public sealed class OutputStore
{
    private readonly ManagerStatePaths paths;

    public OutputStore(ManagerStatePaths paths)
    {
        this.paths = paths;
        this.paths.EnsureDirectories();
    }

    public async Task AppendAsync(OutputLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.JobId);
        var normalized = entry.CreatedAt == default ? entry with { CreatedAt = DateTimeOffset.UtcNow } : entry;
        await AppendJsonLineAsync(paths.GetLogPath(normalized.JobId), normalized, cancellationToken);
    }

    public async Task<OutputLogPage> ReadAsync(
        string jobId,
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return await ReadAsync(
            jobId,
            threadId: null,
            turnId: null,
            agentId: null,
            offset,
            limit,
            cancellationToken);
    }

    public async Task<OutputLogPage> ReadAsync(
        string jobId,
        string? threadId,
        string? turnId,
        string? agentId,
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var entries = await ReadJsonLinesAsync<OutputLogEntry>(paths.GetLogPath(jobId), cancellationToken);
        var filtered = entries
            .Where(entry => Matches(entry.ThreadId, threadId))
            .Where(entry => Matches(entry.TurnId, turnId))
            .Where(entry => Matches(entry.AgentId, agentId))
            .ToArray();
        var pageEntries = filtered.Skip(offset).Take(limit).ToArray();
        var nextOffset = offset + pageEntries.Length;
        return new OutputLogPage
        {
            Entries = pageEntries,
            Offset = offset,
            NextOffset = nextOffset,
            EndOfLog = nextOffset >= filtered.Length,
            TotalCount = filtered.Length
        };
    }

    public bool Exists(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return File.Exists(paths.GetLogPath(jobId));
    }

    public string GetRelativeLogRef(string jobId) => paths.GetRelativeLogPath(jobId);

    private static async Task AppendJsonLineAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync(StorageJson.SerializeLine(value).AsMemory(), cancellationToken);
    }

    internal static async Task<IReadOnlyList<T>> ReadJsonLinesAsync<T>(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var values = new List<T>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var value = StorageJson.Deserialize<T>(line);
            if (value is not null)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static bool Matches(string? actual, string? expected) =>
        string.IsNullOrWhiteSpace(expected)
        || string.Equals(actual, expected.Trim(), StringComparison.Ordinal);
}
