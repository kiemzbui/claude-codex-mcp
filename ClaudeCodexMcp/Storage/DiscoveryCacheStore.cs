using System.Threading;
using System.Threading.Tasks;
using ClaudeCodexMcp.Domain;

namespace ClaudeCodexMcp.Storage;

public sealed class DiscoveryCacheStore
{
    private readonly ManagerStatePaths paths;

    public DiscoveryCacheStore(ManagerStatePaths paths)
    {
        this.paths = paths;
        this.paths.EnsureDirectories();
    }

    public Task SaveAsync(DiscoveryCacheRecord record, CancellationToken cancellationToken = default) =>
        StorageJson.WriteAtomicallyAsync(GetPath(record.Kind), record, cancellationToken);

    public Task<DiscoveryCacheRecord?> ReadAsync(
        DiscoveryCacheKind kind,
        CancellationToken cancellationToken = default) =>
        StorageJson.ReadAsync<DiscoveryCacheRecord>(GetPath(kind), cancellationToken);

    public string GetPath(DiscoveryCacheKind kind) => kind switch
    {
        DiscoveryCacheKind.Skills => paths.SkillsCachePath,
        DiscoveryCacheKind.Agents => paths.AgentsCachePath,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported discovery cache kind.")
    };
}
