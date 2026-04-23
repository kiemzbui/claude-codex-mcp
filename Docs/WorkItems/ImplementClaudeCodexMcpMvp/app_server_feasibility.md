# App-Server Feasibility Report

## Environment

*Codex*: Feasibility was run from `C:\Users\misterkiem\source\repos\claude-codex-mcp` on Windows with the repo root solution `ClaudeCodexMcp.sln`.

- Timestamp: `2026-04-22T22:28:57-05:00`
- `codex --version`: `codex-cli 0.122.0`
- `Get-Command codex` resolved path: `C:\Users\misterkiem\AppData\Local\Microsoft\WinGet\Links\codex.exe`
- Codex app-server initialize user agent observed: `claude-codex-mcp-feasibility/0.122.0 (Windows 10.0.26100; x86_64) WezTerm/20240203-110809-5046fc22 (claude-codex-mcp-feasibility; 0.1.0)`
- App-server reported `codexHome`: `C:\Users\misterkiem\.codex`
- App-server reported platform: `platformFamily=windows`, `platformOs=windows`

## Generation Commands

*Codex*: The required protocol evidence commands completed successfully.

```powershell
codex app-server generate-json-schema --experimental --out ClaudeCodexMcp/Backend/AppServerProtocol/Schema
codex app-server generate-ts --experimental --out ClaudeCodexMcp/Backend/AppServerProtocol/TypeScript
```

*Codex*: Outputs were written to:

- `ClaudeCodexMcp/Backend/AppServerProtocol/Schema`
- `ClaudeCodexMcp/Backend/AppServerProtocol/TypeScript`
- `ClaudeCodexMcp/Backend/AppServerProtocol/CSharp`
- `ClaudeCodexMcp/Backend/AppServerProtocol/provenance.md`

## Approved MVP Surface

*Codex*: The approved MVP method subset is:

```text
initialize
thread/start
turn/start
turn/steer
turn/interrupt
thread/read
thread/turns/list
thread/list
thread/loaded/list
thread/resume
thread/unsubscribe
skills/list
plugin/list
plugin/read
model/list
account/read
account/rateLimits/read
```

*Codex*: The approved MVP notification subset is:

```text
thread/started
thread/status/changed
turn/started
turn/completed
turn/diff/updated
turn/plan/updated
item/started
item/completed
item/agentMessage/delta
thread/tokenUsage/updated
account/rateLimits/updated
error
warning
```

## Runtime Probe

*Codex*: Runtime probing used `codex app-server` over stdio with newline-delimited JSON-RPC requests. The generation-only `--experimental` flag is not accepted by the app-server runner itself.

*Codex*: The `codex_start_task` equivalent sequence used:

```text
initialize
thread/start with cwd, approvalPolicy=never, approvalsReviewer=user, sandbox=read-only, persistExtendedHistory=true
turn/start with text input "Reply exactly APP_SERVER_FEASIBILITY_OK. Do not edit files."
observe notifications until turn/completed
thread/read with includeTurns=true
account/rateLimits/read
thread/resume in a fresh app-server process for the saved thread
thread/read with includeTurns=true after resume
```

*Codex*: The successful live probe produced:

- Thread ID: `019db861-8996-7770-84f9-adfa6e5f45fd`
- Turn ID: `019db861-8cdd-7e52-bf98-793197a52d5c`
- Completion notification: `turn/completed`
- Final output read from `thread/read`: `APP_SERVER_FEASIBILITY_OK`
- Token usage notification: `thread/tokenUsage/updated` with `modelContextWindow=258400`
- Account rate-limit notification: `account/rateLimits/updated`
- Account rate-limit read method: `account/rateLimits/read`, result `limitId=codex`
- Resume/read probe: `thread/resume` succeeded in a new app-server process and `thread/read` returned 1 turn with the saved prompt/output

*Codex*: The app-server process was deliberately killed after each successful probe, so the subprocess exit code was non-zero after evidence collection. The observed protocol responses and notifications completed before termination.

## Capability Answers

*Codex*: `start thread or turn`: Supported. `thread/start` returned a persisted thread with path under `C:\Users\misterkiem\.codex\sessions\...`, and `turn/start` returned an in-progress turn.

*Codex*: `stream or poll status`: Supported. Streaming notifications observed `thread/status/changed`, `turn/started`, `item/agentMessage/delta`, and `turn/completed`. Poll/read status is available through `thread/read` and the generated `thread/list` or `thread/turns/list` method names.

*Codex*: `read final output`: Supported. `turn/completed` did not include item content, but `thread/read includeTurns=true` returned the completed turn with an `agentMessage` item containing `APP_SERVER_FEASIBILITY_OK`.

*Codex*: `expose token usage and context window`: Supported. `thread/tokenUsage/updated` exposed total and last token usage plus `modelContextWindow=258400`.

*Codex*: `expose account rate-limit windows`: Supported. `account/rateLimits/updated` and `account/rateLimits/read` exposed primary and secondary usage windows with `usedPercent`, `windowDurationMins`, and `resetsAt`.

*Codex*: `resume or read prior thread state`: Supported. `thread/resume` in a fresh app-server process returned the prior thread, and `thread/read includeTurns=true` returned the saved turn and final output.

## Schema Gaps

*Codex*: No approved MVP method or notification name was missing from generated Schema or TypeScript artifacts.

*Codex*: The generated protocol includes many APIs outside the MVP. Stage 4 C# bindings intentionally do not expose `fs/*`, `command/*`, marketplace mutation, plugin install/uninstall, config mutation, account login/logout, feedback, Windows sandbox setup, or realtime APIs.

*Codex*: Generated `turn/completed` is a lifecycle signal, not sufficient by itself for final answer retrieval because the completed notification's `Turn.items` was empty during the live probe. Backend implementation should call `thread/read includeTurns=true` or consume completed item notifications to capture final output.

*Codex*: Generated Schema and TypeScript are authoritative protocol evidence. The C# bindings are minimal vendored request/name bindings because the installed generator does not emit C#.

## Fallback Implications

*Codex*: App-server-first backend work is feasible in this environment for the required MVP lifecycle, output, usage, rate-limit, and resume capabilities.

*Codex*: CLI fallback remains necessary for environments where `codex` is missing, app-server generation/runtime commands are unavailable, app-server schema changes break the approved subset, authentication is unavailable, or rate limits prevent app-server turns.

*Codex*: Production backend work should treat app-server protocol capabilities as version-proven for `codex-cli 0.122.0` and should surface degraded capabilities rather than silently succeeding when runtime probes or generated names differ.
