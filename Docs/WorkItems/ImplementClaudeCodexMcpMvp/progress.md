# ImplementClaudeCodexMcpMvp - Progress

## Stage Status

- [x] Stage 1 - Scaffold, Options, And Logging
- [x] Stage 2 - Profile And Workflow Validation
- [x] Stage 3 - Durable Job, Queue, Output, And Notification Storage
- [x] Stage 4 - App-Server Feasibility Gate
- [x] Manual Smoke Gate A - App-Server Feasibility Review
- [x] Stage 5 - Channel Feasibility Gate
- [x] Manual Smoke Gate B - Channel Feasibility Review
- [x] Stage 6 - Backend Abstraction And Minimal Lifecycle
- [ ] Stage 7 - Core MCP Tool Surface
- [ ] Stage 8 - Background Supervisor
- [ ] Stage 9 - Queued Input And Cancellation
- [ ] Stage 10 - Usage And Statusline
- [ ] Stage 11 - Full Output Pagination
- [ ] Stage 12 - Channel Notifications
- [ ] Stage 13 - CLI Fallback
- [ ] Stage 14 - End-To-End Smoke Tests And Acceptance
- [ ] Manual Smoke Gate C - MVP Smoke Review

## Current Checkpoint

- Latest completed point: Stage 6 - Backend Abstraction And Minimal Lifecycle.
- Active reality: app-server-first backend behavior is approved for continuation based on `app_server_feasibility.md`; CLI fallback remains required for degraded environments. Channel protocol prerequisites are present, so production channel notification work should still be implemented later as best-effort and fallback-aware, disabled by default until Manual Smoke Gate C verifies live delivery with a real `claude --channels server:claude-codex-mcp` receiver.
- Next executable step: Stage 7 - Core MCP Tool Surface.
- Next executor command: `$orchestrate execute Docs/WorkItems/ImplementClaudeCodexMcpMvp`.

## Checkpoint Notes

- No parallel batch is currently next.
- Runtime state must be created under root `.codex-manager/`.
- Implementation must use the root layout `ClaudeCodexMcp.sln`, `ClaudeCodexMcp/ClaudeCodexMcp.csproj`, and `ClaudeCodexMcp.Tests/ClaudeCodexMcp.Tests.csproj`.
- Stage 1 verification passed with `dotnet build ClaudeCodexMcp.sln`, `dotnet test ClaudeCodexMcp.sln`, and stdout-discipline checks.
- Stage 2 verification passed with `dotnet build ClaudeCodexMcp.sln`, `dotnet test ClaudeCodexMcp.sln`, Roslyn diagnostics, and whitespace checks after global-usings adjustments.
- Stage 3 verification passed with Roslyn error diagnostics, `dotnet build ClaudeCodexMcp.sln`, `dotnet test ClaudeCodexMcp.sln --no-build`, and independent storage-scope checks.
- Stage 4 verification passed with Roslyn error diagnostics, `dotnet build ClaudeCodexMcp.sln --nologo`, `dotnet test ClaudeCodexMcp.sln --nologo --no-build`, generated protocol artifact checks, and live app-server probe evidence.
- Manual Smoke Gate A passed on review of `app_server_feasibility.md`; continue with app-server-first behavior while preserving CLI fallback for degraded environments.
- Stage 5 verification passed with `dotnet build ClaudeCodexMcp.sln`, `dotnet test ClaudeCodexMcp.sln --filter FullyQualifiedName~Notifications`, `dotnet test ClaudeCodexMcp.sln`, and channel feasibility scope checks.
- Manual Smoke Gate B passed on review of `channel_feasibility.md` with the condition that Stage 12 still implements channel notification support as best-effort and fallback-aware, disabled by default until Manual Smoke Gate C verifies live delivery through a real channel-enabled Claude receiver.
- Stage 6 verification passed with `dotnet build ClaudeCodexMcp.sln --nologo`, `dotnet test ClaudeCodexMcp.Tests\ClaudeCodexMcp.Tests.csproj --nologo --filter FullyQualifiedName~ClaudeCodexMcp.Tests.Backend`, `dotnet test ClaudeCodexMcp.sln --nologo`, and Stage 6 scope checks.
