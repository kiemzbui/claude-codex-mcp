# Work Report - Step 13
**Date:** 2026-04-23
**Status:** SUCCESS

## Step executed
*Codex*: Completed Stage 13 CLI fallback, including degraded CLI backend behavior and profile-selected tool/DI routing to CLI only when profile policy explicitly requests it.

## Files changed
- `ClaudeCodexMcp/Backend/CodexCliBackend.cs` - *Codex*: Added degraded `ICodexBackend` implementation for direct `codex exec` execution, final output capture, output-log persistence, best-effort changed-file summaries, test-summary extraction, and explicit unsupported capability handling.
- `ClaudeCodexMcp/Backend/CodexCliBackendSelection.cs` - *Codex*: Added CLI fallback profile-policy helpers plus `ICodexBackendSelector` and `CodexProfileBackendSelector` for runtime profile-selected backend routing.
- `ClaudeCodexMcp/ClaudeCodexMcpHost.cs` - *Codex*: Registered app-server and CLI backends separately, registered the profile backend selector, kept app-server as the default injected `ICodexBackend`, and made CLI available only through selector-driven tool routing.
- `ClaudeCodexMcp/Tools/CodexToolService.cs` - *Codex*: Routed start/status/result/output/send-input/cancel/usage backend calls through the selected profile backend, returned explicit unsupported-capability tool errors, preserved app-server fallback behavior for non-CLI profiles, and persisted CLI start summaries from backend status.
- `ClaudeCodexMcp.Tests/Backend/CodexCliBackendTests.cs` - *Codex*: Added CLI backend tests for degraded capabilities, `?` statusline behavior, direct execution mapping, output capture, changed-file/test summaries, unsupported feature handling, and backend selection policy.
- `ClaudeCodexMcp.Tests/Tools/CodexToolServiceTests.cs` - *Codex*: Added focused coverage proving a CLI profile dispatches through the CLI backend and an app-server profile does not.
- `ClaudeCodexMcp.Tests/HostConfigurationTests.cs` - *Codex*: Added DI smoke coverage that resolves the backend selector and tool service from the host.
- `Docs/WorkItems/ImplementClaudeCodexMcpMvp/cli_fallback.md` - *Codex*: Documented supported degraded behavior, unsupported gaps, `?` usage/statusline behavior, and final profile-selected runtime routing.
- `Docs/WorkItems/ImplementClaudeCodexMcpMvp/workreports/step13_work_report.md` - *Codex*: Replaced the failed prior report with this final Stage 13 report.

## Analysis queries run
- *Codex*: `mcp__roslyn__.load_solution` -> loaded `ClaudeCodexMcp.sln` with 2 projects and 76 documents.
- *Codex*: `mcp__roslyn__.solution_status` -> confirmed 2 projects and nonzero document load.
- *Codex*: `dotnet build ClaudeCodexMcp.sln --no-restore` before edits -> 0 errors, 0 warnings.
- *Codex*: `rg --files ClaudeCodexMcp ClaudeCodexMcp.Tests | rg "Backend|Tools|Configuration"` -> inspected backend, tool, configuration, and focused test surfaces.
- *Codex*: `rg "interface ICodexBackend|class CodexAppServerBackend|CodexBackendNames|Profile.*Backend|backend" ClaudeCodexMcp ClaudeCodexMcp.Tests -g "*.cs"` -> confirmed profile policy already carried backend selection and tool runtime dispatch was the missing path.
- *Codex*: `rg <stale failed-report routing-conflict wording> Docs/WorkItems/ImplementClaudeCodexMcpMvp` -> 1 stale failed-report/doc statement before update, 0 hits after update.
- *Codex*: `mcp__roslyn__.get_diagnostics --min_severity Error` -> Roslyn workspace reported 686 diagnostics caused by existing implicit-using/test workspace resolution issues; MSBuild remained authoritative and clean.
- *Codex*: `execution_recs.md` contained no explicit Pre query block, so there were no prescribed before/after search counts to reduce to zero.

## Build result
Before: 0 errors. After: 0 errors.

## Surprises / deviations from plan
*Codex*: The stage prompt requested reading `progress.md`, but worker rules prohibit reading or editing it; it was not read.

*Codex*: Roslyn diagnostics reported many missing implicit-using symbols in existing test files even while `dotnet build` and `dotnet test` succeeded cleanly; MSBuild results were used as authoritative.

*Codex*: The user-approved amended scope allowed DI and tool routing files, so the prior ownership blocker was resolved without touching Stage 14 smoke-test artifacts or orchestrator-owned status files.

## Verification queries (for work-verifier)
- *Codex*: Run `dotnet test ClaudeCodexMcp.sln --no-build --filter "FullyQualifiedName~CodexCliBackendTests|FullyQualifiedName~ProfileSelectedCliFallbackRoutesToolDispatchOnlyWhenProfileRequestsIt"` to verify CLI fallback degraded capability reporting, `?` statusline behavior, direct execution mapping, final output capture, changed-file/test summaries, backend selection policy, unsupported feature handling, and profile-selected tool routing.
- *Codex*: Run `dotnet test ClaudeCodexMcp.sln --no-build` to verify the full suite remains green.
- *Codex*: Inspect `CodexCliBackend.Capabilities.DegradedCapabilities`, `CodexProfileBackendSelector`, `CodexToolService.SelectBackend`, and `Docs/WorkItems/ImplementClaudeCodexMcpMvp/cli_fallback.md` to verify CLI is selected only by explicit profile backend policy and app-server remains the default path.
