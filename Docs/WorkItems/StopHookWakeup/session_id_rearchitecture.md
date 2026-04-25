# Session ID Resolution Rearchitecture

## Problem

`ResolveWakeSessionId` in `CodexTools.cs` has three resolution paths in priority order:

1. MCP transport session ID — always null for stdio (stdio is single-session, no transport-level ID)
2. Per-process session binding file at `~/.codex-manager/session-bindings/<PID>.json` — this path was attempted later and proved unreliable / unworkable in practice
3. `CLAUDE_CODE_SESSION_ID` / `CLAUDE_SESSION_ID` env vars — Claude Code does not inject these into MCP server subprocess environments

All three fail. `wakeSessionId` ends up null on every job. The signal file is never written.

The only mechanism that actually supplies the Claude session ID is the **Stop hook**, which receives `session_id` in its JSON input on every Claude idle event and writes it to `~/.codex-manager/current-session-id.txt`.

This file is reliably present and correct before any `codex_start_task` call, because the Stop hook fires every time Claude finishes a response — including the response immediately preceding the `codex_start_task` call.

## Current state (broken)

`ReadBoundClaudeSessionId` was redesigned around per-process session binding files. That design proved unreliable / unworkable in live runs and should not be treated as a dependable source of truth.

The working design is simpler: `current-session-id.txt` is the primary path, maintained by the Stop hook. Session-binding logic should be removed rather than preserved as a dead or misleading primary path.

## Required change

### Invert the resolution priority

The session ID resolution should treat `current-session-id.txt` as the **primary** path, not a fallback.

Suggested simplified `ReadBoundClaudeSessionId`:

```csharp
private static string? ReadBoundClaudeSessionId()
{
    try
    {
        var userRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userRoot))
            return null;

        // Primary: Stop hook writes current Claude session id here on every idle event.
        // This is the only reliable source of the caller session id for stdio-based MCP servers.
        var currentSessionPath = Path.Combine(userRoot, ".codex-manager", "current-session-id.txt");
        if (File.Exists(currentSessionPath))
            return NormalizeSessionId(File.ReadAllText(currentSessionPath));
    }
    catch
    {
        return null;
    }

    return null;
}
```

The per-process session-bindings path should be removed. It proved unreliable / unworkable and adds misleading complexity.

## Why the Stop hook path is reliable

- The Stop hook fires after every Claude response, before Claude goes idle
- `session_id` in the hook input is the stable UUID for the current Claude Code session
- The file is written with `-NoNewline` so it contains only the UUID
- The file is always overwritten with the current session, so it reflects the active session at `codex_start_task` call time
- The Stop hook runs in the same session as the `codex_start_task` call, so the session IDs match

## What does NOT need to change

- Active Stop hook logic — must keep writing `current-session-id.txt` correctly
- `WriteWakeSignal` in `CodexJobSupervisor.cs` — already correct once `wakeSessionId` is non-null
- `ManagerStatePaths.WakeSignalsDirectory` — already fixed to use user-profile path
- The FileSystemWatcher logic in the Stop hook — already correct

## Scope

- Remove session-binding logic and scripts.
- Keep `current-session-id.txt` as the primary source.
- Document explicitly that session binding was unreliable / unworkable.
