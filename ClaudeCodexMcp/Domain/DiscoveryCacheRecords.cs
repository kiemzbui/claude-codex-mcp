using System.Collections.Generic;

namespace ClaudeCodexMcp.Domain;

public enum DiscoveryCacheKind
{
    Skills,
    Agents
}

public sealed record DiscoveryCacheRecord
{
    public DiscoveryCacheKind Kind { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string? RepoRoot { get; init; }

    public IReadOnlyList<DiscoveryRootSnapshot> Roots { get; init; } = [];

    public IReadOnlyList<DiscoveryCacheItem> Items { get; init; } = [];
}

public sealed record DiscoveryRootSnapshot
{
    public string SourceScope { get; init; } = string.Empty;

    public string RootPath { get; init; } = string.Empty;

    public bool Exists { get; init; }

    public long MaxLastWriteTimeUtcTicks { get; init; }

    public int FileCount { get; init; }
}

public sealed record DiscoveryCacheItem
{
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string SourceScope { get; init; } = string.Empty;

    public string SourcePath { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public IReadOnlyList<string> ConflictsWith { get; init; } = [];

    public string? BodyPath { get; init; }
}
