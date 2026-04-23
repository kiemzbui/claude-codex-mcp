# Work Report - Step 8
**Date:** 2026-04-23
**Status:** SUCCESS

## Step executed
*Codex*: Implemented the Stage 8 background supervisor, startup recovery, active-job observation, polling fallback, per-job locking, transient retry, and unrecoverable backend-thread failure handling.

## Files changed
- ClaudeCodexMcp/Supervisor/CodexJobSupervisor.cs - *Codex*: Added the hosted background supervisor with startup index reconstruction, active-job resume, refresh loop, observation/poll selection, persisted usage/output enrichment, retry handling, and `backend_thread_unrecoverable` terminal failure behavior.
- ClaudeCodexMcp/Supervisor/CodexJobLockRegistry.cs - *Codex*: Added per-job async locking shared by supervisor and tool flows.
- ClaudeCodexMcp/Supervisor/CodexJobRecordUpdater.cs - *Codex*: Added shared job transition helpers for status, output, usage, transient errors, terminal invariants, and backend id projection.
- ClaudeCodexMcp/Supervisor/CodexJobSupervisorOptions.cs - *Codex*: Added supervisor polling and transient failure options.
- ClaudeCodexMcp/Supervisor/CodexJobSupervisorResult.cs - *Codex*: Added a compact result DTO for recovery and refresh passes.
- ClaudeCodexMcp/ClaudeCodexMcpHost.cs - *Codex*: Registered supervisor services, the shared lock registry, and the supervisor hosted service.
- ClaudeCodexMcp/Backend/ICodexBackend.cs - *Codex*: Added `PollStatusAsync` for bounded polling fallback.
- ClaudeCodexMcp/Backend/CodexBackendThreadUnrecoverableException.cs - *Codex*: Added a stable exception for unrecoverable backend thread recovery failures.
- ClaudeCodexMcp/Backend/CodexAppServerBackend.cs - *Codex*: Implemented `PollStatusAsync` using `thread/read` mapping.
- ClaudeCodexMcp/Backend/FakeCodexBackend.cs - *Codex*: Added polling support and call counters for deterministic tests.
- ClaudeCodexMcp/Domain/BackendRecords.cs - *Codex*: Added polling capability metadata plus status/output fields for result summaries, changed files, test summaries, and usage snapshots.
- ClaudeCodexMcp/Domain/JobRecords.cs - *Codex*: Persisted backend usage snapshots in job records.
- ClaudeCodexMcp/Tools/CodexToolService.cs - *Codex*: Shared the per-job lock with status, result, cancellation, and input tool paths so tool-driven writes do not race supervisor refresh.
- ClaudeCodexMcp.Tests/Supervisor/CodexJobSupervisorTests.cs - *Codex*: Added Stage 8 coverage for startup recovery, index reconstruction, event/poll fallback, clarification status, terminal invariants, lock behavior, transient retry, persisted output/usage summaries, and unrecoverable thread failure.
- ClaudeCodexMcp.Tests/Tools/CodexToolServiceTests.cs - *Codex*: Updated tool-service test wiring for the shared job lock and backend polling interface.

## Analysis queries run
- *Codex*: `rg -n "ICodexBackend|Observe|Poll|Resume|JobStore|OutputStore|QueueStore|CodexJobSupervisor|waiting_for_input|BackendThread|Unrecoverable|Clarification|Lock" ClaudeCodexMcp ClaudeCodexMcp.Tests` -> baseline mapping query; no cleanup target, so after count is N/A.
- *Codex*: `rg -n "class .*: ICodexBackend|SupportsObserveStatus|ObserveStatusAsync" ClaudeCodexMcp.Tests ClaudeCodexMcp` -> identified backend interface implementers to update; after build confirmed 0 interface implementation errors.
- *Codex*: No stage-specific Pre queries are defined in `execution_recs.md`.

## Build result
*Codex*: Before: 0 errors. After: 0 errors. `dotnet build ClaudeCodexMcp.sln` passed with 0 warnings and 0 errors. `dotnet test ClaudeCodexMcp.sln --no-build` passed: 68 tests passed, 0 failed.

## Surprises / deviations from plan
*Codex*: The per-job lock requirement cannot be made meaningful for cancellation and user input from `Supervisor/**` alone, so `ClaudeCodexMcp/Tools/CodexToolService.cs` was intentionally touched to share the supervisor lock registry with tool-driven writes.

*Codex*: The user-facing instruction listed `progress.md` for reading, but the work-coder rules mark it orchestration-owned and prohibit reading/editing it. I did not intentionally open it for planning, and did not edit it; however, a later broad `rg` over the work-item directory may have scanned it while checking for forbidden notification/progress references.

*Codex*: Roslyn diagnostics reported unresolved implicit framework/global using symbols in existing test files even after reload, while `dotnet build` and `dotnet test` both completed cleanly. I treated the command-line compiler/test results as authoritative.

## Verification queries (for work-verifier)
*Codex*: NONE - `execution_recs.md` does not define Stage 8 Post queries. Suggested verifier checks are `dotnet build ClaudeCodexMcp.sln`, `dotnet test ClaudeCodexMcp.sln`, and focused review of `ClaudeCodexMcp.Tests/Supervisor/CodexJobSupervisorTests.cs`.
