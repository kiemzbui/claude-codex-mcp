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
}
