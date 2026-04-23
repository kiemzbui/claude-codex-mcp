# Work Report - Step 10
**Date:** 2026-04-23
**Status:** SUCCESS

## Step executed
*Codex*: Implemented Stage 10 usage normalization, `codex_usage`, and statusline propagation for compact job tool responses.

## Files changed
- ClaudeCodexMcp/Domain/UsageDtos.cs - *Codex*: Added usage response DTOs for normalized context, weekly, 5h, remaining percentages, and statusline output.
- ClaudeCodexMcp/Usage/UsageReporter.cs - *Codex*: Added percentage normalization, reset-time carrying, context remaining estimate calculation, unavailable `?` rendering, and stable statusline formatting.
- ClaudeCodexMcp/Tools/CodexToolService.cs - *Codex*: Added `UsageAsync`, backend usage refresh/persistence, statusline shaping for compact job responses, and preserved the existing constructor overload.
- ClaudeCodexMcp/Tools/CodexTools.cs - *Codex*: Registered the `codex_usage` MCP tool.
- ClaudeCodexMcp/ClaudeCodexMcpHost.cs - *Codex*: Registered `UsageReporter` in DI.
- ClaudeCodexMcp.Tests/Usage/UsageReporterTests.cs - *Codex*: Added full, partial, unavailable, estimate-labeling, remaining-semantics, and stable-format tests.
- ClaudeCodexMcp.Tests/Tools/CodexToolServiceTests.cs - *Codex*: Added focused tool tests for `codex_usage` refresh/persistence and statusline inclusion on status/result/send-input responses.

## Analysis queries run
- *Codex*: `mcp__roslyn__.load_solution` and `mcp__roslyn__.solution_status` -> 2 projects, 64 documents loaded before edits.
- *Codex*: `dotnet build ClaudeCodexMcp.sln --no-restore` baseline -> 0 errors before, 0 errors after.
- *Codex*: `rg -n "Usage|RateLimit|tokenUsage|rateLimits|Statusline|statusline" ClaudeCodexMcp ClaudeCodexMcp.Tests` -> 418 hits reviewed for existing usage/statusline surfaces.
- *Codex*: `execution_recs.md` Stage 10 Pre queries -> 0 defined, 0 run.

## Build result
*Codex*: Before: 0 errors. After: 0 errors.

*Codex*: Focused Stage 10 tests: 7 passed, 0 failed.

*Codex*: Full test suite: 87 passed, 0 failed.

## Surprises / deviations from plan
*Codex*: Roslyn MCP diagnostics reported missing standard types in tests even though `dotnet build` was clean; actual compiler verification was used as the blocking baseline. No scope widening was required, and `progress.md` was not edited.

## Verification queries (for work-verifier)
- *Codex*: `dotnet build ClaudeCodexMcp.sln --no-restore`
- *Codex*: `dotnet test ClaudeCodexMcp.sln --no-build --filter "FullyQualifiedName~UsageReporterTests|FullyQualifiedName~UsageRefreshPersistsBackendDataAndReturnsNormalizedFields|FullyQualifiedName~StatusResultAndSendInputIncludePersistedStatusline"`
- *Codex*: `dotnet test ClaudeCodexMcp.sln --no-build`
- *Codex*: `execution_recs.md` does not define named Stage 10 Post queries; verify the Stage 10 exit criteria for `codex_usage`, `?` rendering, estimate labeling, remaining-percent semantics, and `[codex status: context ? | weekly ? | 5h ?]` format.
