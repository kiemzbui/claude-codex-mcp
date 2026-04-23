# Codex App-Server Protocol Provenance

## Generation

*Codex*: Generated on `2026-04-22T22:28:57-05:00` from the locally installed Codex CLI.

- `codex --version`: `codex-cli 0.122.0`
- `Get-Command codex` resolved path: `C:\Users\misterkiem\AppData\Local\Microsoft\WinGet\Links\codex.exe`
- Schema command: `codex app-server generate-json-schema --experimental --out ClaudeCodexMcp/Backend/AppServerProtocol/Schema`
- TypeScript command: `codex app-server generate-ts --experimental --out ClaudeCodexMcp/Backend/AppServerProtocol/TypeScript`
- Schema output path: `ClaudeCodexMcp/Backend/AppServerProtocol/Schema`
- TypeScript output path: `ClaudeCodexMcp/Backend/AppServerProtocol/TypeScript`
- Minimal C# binding path: `ClaudeCodexMcp/Backend/AppServerProtocol/CSharp`

## Approved MVP Methods

*Codex*: The C# binding surface is intentionally limited to these app-server methods.

- `initialize`
- `thread/start`
- `turn/start`
- `turn/steer`
- `turn/interrupt`
- `thread/read`
- `thread/turns/list`
- `thread/list`
- `thread/loaded/list`
- `thread/resume`
- `thread/unsubscribe`
- `skills/list`
- `plugin/list`
- `plugin/read`
- `model/list`
- `account/read`
- `account/rateLimits/read`

## Approved MVP Notifications

*Codex*: The C# binding surface is intentionally limited to these app-server notifications.

- `thread/started`
- `thread/status/changed`
- `turn/started`
- `turn/completed`
- `turn/diff/updated`
- `turn/plan/updated`
- `item/started`
- `item/completed`
- `item/agentMessage/delta`
- `thread/tokenUsage/updated`
- `account/rateLimits/updated`
- `error`
- `warning`

## Schema Gaps

*Codex*: No approved MVP method or notification name was missing from the generated Schema and TypeScript artifacts.

*Codex*: The generated protocol includes broad non-MVP APIs such as `fs/*`, `command/*`, plugin install/uninstall, config mutation, realtime, feedback, Windows sandbox setup, and account login/logout management. These are intentionally excluded from the minimal C# binding surface.

*Codex*: The app-server generator does not emit C# bindings. The C# surface under `CSharp/**` is a vendored minimal binding layer derived from the generated Schema and TypeScript names and request shapes.

*Codex*: Runtime `codex app-server` does not accept `--experimental`; that flag is valid for the generation subcommands used above.
