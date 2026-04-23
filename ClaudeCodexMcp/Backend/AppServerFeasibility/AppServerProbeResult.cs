using System.Collections.Generic;

namespace ClaudeCodexMcp.Backend.AppServerFeasibility;

public sealed record AppServerProbeResult(
    bool Initialized,
    string? UserAgent,
    string? ThreadId,
    string? TurnId,
    bool TurnCompleted,
    bool ThreadReadSucceeded,
    bool TokenUsageObserved,
    bool RateLimitsObserved,
    string? FinalOutput,
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> Errors);
