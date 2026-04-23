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

    public IReadOnlyList<DiscoveryCacheItem> Items { get; init; } = [];
}

public sealed record DiscoveryCacheItem
{
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string SourceScope { get; init; } = string.Empty;

    public string SourcePath { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public IReadOnlyList<string> ConflictsWith { get; init; } = [];
}
