using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodexMcp.Domain;

namespace ClaudeCodexMcp.Storage;

public sealed class JobStore
{
    private readonly ManagerStatePaths paths;

    public JobStore(ManagerStatePaths paths)
    {
        this.paths = paths;
        this.paths.EnsureDirectories();
    }

    public async Task SaveAsync(CodexJobRecord job, CancellationToken cancellationToken = default)
    {
        Validate(job);
        await StorageJson.WriteAtomicallyAsync(paths.GetJobPath(job.JobId), job, cancellationToken);
        await RebuildIndexAsync(cancellationToken);
    }

    public Task<CodexJobRecord?> ReadAsync(string jobId, CancellationToken cancellationToken = default) =>
        StorageJson.ReadAsync<CodexJobRecord>(paths.GetJobPath(jobId), cancellationToken);

    public async Task<JobIndexRecord> ReadIndexAsync(CancellationToken cancellationToken = default) =>
        await StorageJson.ReadAsync<JobIndexRecord>(paths.JobIndexPath, cancellationToken)
            ?? await RebuildIndexAsync(cancellationToken);

    public async Task<JobIndexRecord> RebuildIndexAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.JobsDirectory);
        var jobs = new List<CodexJobRecord>();
        foreach (var path in Directory.EnumerateFiles(paths.JobsDirectory, "*.json"))
        {
            if (string.Equals(Path.GetFileName(path), "index.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var job = await StorageJson.ReadAsync<CodexJobRecord>(path, cancellationToken);
            if (job is not null)
            {
                jobs.Add(job);
            }
        }

        var index = new JobIndexRecord
        {
            RebuiltAt = DateTimeOffset.UtcNow,
            Jobs = jobs
                .OrderByDescending(job => job.UpdatedAt)
                .ThenBy(job => job.JobId, StringComparer.Ordinal)
                .Select(ToIndexEntry)
                .ToArray()
        };

        await StorageJson.WriteAtomicallyAsync(paths.JobIndexPath, index, cancellationToken);
        return index;
    }

    private JobIndexEntry ToIndexEntry(CodexJobRecord job) => new()
    {
        JobId = job.JobId,
        CreatedAt = job.CreatedAt,
        UpdatedAt = job.UpdatedAt,
        Profile = job.Profile,
        Workflow = job.Workflow,
        Repo = job.Repo,
        Title = ProjectionSanitizer.ToSummary(job.Title, 160),
        Status = job.Status,
        CodexThreadId = job.CodexThreadId,
        CodexTurnId = job.CodexTurnId,
        CodexSessionId = job.CodexSessionId,
        ResultSummary = ProjectionSanitizer.ToOptionalSummary(job.ResultSummary),
        LastError = ProjectionSanitizer.ToOptionalSummary(job.LastError),
        InputQueue = job.InputQueue,
        JobPath = Path.Combine(".codex-manager", "jobs", $"{job.JobId}.json")
    };

    private static void Validate(CodexJobRecord job)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(job.JobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Repo);
    }
}
