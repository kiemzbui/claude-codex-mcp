using System.Collections.Generic;

namespace ClaudeCodexMcp.Domain;

public sealed record OutputLogEntry
{
    public DateTimeOffset CreatedAt { get; init; }

    public string JobId { get; init; } = string.Empty;

    public string? ThreadId { get; init; }

    public string? TurnId { get; init; }

    public string? AgentId { get; init; }

    public string Source { get; init; } = "backend";

    public string Level { get; init; } = "info";

    public string Message { get; init; } = string.Empty;

    public string? PayloadJson { get; init; }
}

public sealed record OutputLogPage
{
    public IReadOnlyList<OutputLogEntry> Entries { get; init; } = [];

    public int Offset { get; init; }

    public int NextOffset { get; init; }

    public bool EndOfLog { get; init; }

    public int TotalCount { get; init; }
}

public static class OutputResponseLimits
{
    public const int SummaryBytes = 8 * 1024;

    public const int NormalBytes = 32 * 1024;

    public const int FullBytes = 128 * 1024;

    public const int PaginatedChunkBytes = 64 * 1024;

    public const int AbsoluteHardCapBytes = 256 * 1024;

    public const int ChannelEventBytes = 4 * 1024;
}

public sealed record OutputArtifactRef
{
    public string Kind { get; init; } = string.Empty;

    public string Ref { get; init; } = string.Empty;

    public string? Description { get; init; }
}
