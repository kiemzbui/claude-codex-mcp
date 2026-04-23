# Channel Feasibility Report

## Environment

*Codex*: Feasibility was run from `C:\Users\misterkiem\source\repos\claude-codex-mcp` on Windows with the repo root solution `ClaudeCodexMcp.sln`.

- Timestamp: `2026-04-23T00:55:48-05:00`
- `Get-Command claude` resolved path: `C:\Users\misterkiem\.local\bin\claude.exe`
- `claude --version`: `2.1.117 (Claude Code)`
- Minimum required Claude Code version: `2.1.80`
- Version check: `2.1.117 >= 2.1.80` is `true`
- Target version check: exact target `2.1.117` is `true`
- `claude auth status`: `loggedIn=true`, `authMethod=claude.ai`, `apiProvider=firstParty`
- `claude doctor`: timed out after 124 seconds and was stopped; it did not produce channel evidence
- `~/.claude/settings.json` selected fields: `preferredNotifChannel=terminal_bell`, `allowedChannelPlugins` absent

## Channel Configuration

*Codex*: Local Claude Code 2.1.117 supports a hidden tagged `--channels` launch form. Running `claude --channels --help` returned that entries must be tagged as:

```text
plugin:<name>@<marketplace>
server:<name>
```

*Codex*: The target manager shape is an MCP server declaration with experimental capability:

```json
{
  "capabilities": {
    "tools": {},
    "experimental": {
      "claude/channel": {}
    }
  },
  "instructions": "Claude Codex MCP channel probe only. Channel messages are wake-up signals; call MCP status/result tools for authoritative state."
}
```

*Codex*: The intended channel-capable launch command after the MCP server is configured is:

```powershell
claude --channels server:claude-codex-mcp
```

*Codex*: Local Claude plugin examples under `~/.claude/plugins/marketplaces/claude-plugins-official/external_plugins/fakechat`, `telegram`, and `discord` declare `experimental["claude/channel"]` and send `notifications/claude/channel` notifications. The cached changelog also records `--channels` as research preview.

## Command Used

*Codex*: Prerequisite and configuration commands run:

```powershell
Get-Command claude -ErrorAction SilentlyContinue | Select-Object Source, Version
claude --version
claude --help
claude auth --help
claude auth status
claude mcp --help
claude mcp serve --help
claude --channels --help
Get-CimInstance Win32_Process -Filter "Name = 'claude.exe'" | Select-Object ProcessId, CommandLine
```

*Codex*: No live delivery command was run because no active `claude --channels server:claude-codex-mcp` session was observable and this stage must not edit Claude MCP registration or start an unattended interactive Claude session as a substitute for a user-visible receiver.

## Payload Shape

*Codex*: Stage 5 defines this compact JSON-RPC notification shape:

```json
{
  "jsonrpc": "2.0",
  "method": "notifications/claude/channel",
  "params": {
    "content": "Claude Codex MCP channel feasibility probe.",
    "meta": {
      "source": "claude-codex-mcp",
      "event": "channel_feasibility_probe",
      "job_id": "channel_probe",
      "status": "probe",
      "statusline": "[codex status: context ? | weekly ? | 5h ?]",
      "probe_id": "probe-1",
      "ts": "2026-04-23T05:00:00.0000000Z"
    }
  }
}
```

*Codex*: The unit-tested payload is below the 4 KB channel event hard cap and contains only compact identifiers/status data. It does not include raw logs, transcripts, prompts, diffs, secrets, or full output.

## Observed Result

*Codex*: Channel declaration and event shape are feasible for the observed Claude Code build:

- Claude Code version is exact target `2.1.117`.
- Claude Code is logged in through `claude.ai`.
- The local CLI accepts the hidden `--channels` option syntax when entries are tagged.
- Local channel plugin examples confirm the required `claude/channel` declaration and `notifications/claude/channel` method shape.

*Codex*: Live delivery was not verified. After stopping the timed-out `claude doctor` probe, no active `claude.exe` session was observable, so there was no channel-enabled receiver to confirm event display.

## Fallback Decision

*Codex*: Channel support remains disabled by default for production work until a later smoke gate verifies delivery through an active `claude --channels server:claude-codex-mcp` session.

*Codex*: Polling remains the active path. Future production notification work may implement channel support as best-effort and disabled-by-default, with channel events treated only as wake-up signals and never as lifecycle-authoritative state.
