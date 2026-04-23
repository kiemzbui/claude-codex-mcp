# Stage 14 Smoke Results

**Date:** 2026-04-23
**Repo:** `C:\Users\misterkiem\source\repos\claude-codex-mcp`
**Solution:** `ClaudeCodexMcp.sln`

## Environment

- *Codex*: Windows local repo execution from `C:\Users\misterkiem\source\repos\claude-codex-mcp`.
- *Codex*: `dotnet --version` -> `10.0.203`.
- *Codex*: `Get-Command dotnet` -> `C:\Program Files\dotnet\dotnet.exe`.
- *Codex*: `codex --version` -> `codex-cli 0.122.0`.
- *Codex*: `claude --version` -> `2.1.117 (Claude Code)`.
- *Codex*: App-server feasibility report says the local app-server supports the required MVP lifecycle, output, usage, rate-limit, and resume capabilities.
- *Codex*: Channel feasibility report did not verify live delivery through an active `claude --channels server:claude-codex-mcp` session, so channel delivery remains disabled by default and polling recovery is the accepted fallback.

## Commands Run

- *Codex*: `dotnet test ClaudeCodexMcp.Tests\ClaudeCodexMcp.Tests.csproj --filter FullyQualifiedName~Smoke --no-restore` -> passed, 3 smoke tests.
- *Codex*: `dotnet build ClaudeCodexMcp.sln` -> passed, 0 warnings, 0 errors.
- *Codex*: `dotnet test ClaudeCodexMcp.sln --no-restore` -> passed, 112 tests.

## Automated Smoke Coverage

- *Codex*: `ReadOnlyDiscoveryProfilesDirectExecutionAndWorkflowRoutingSmoke` covers profile listing, compact policy summaries, read-only skill and agent discovery, metadata-only detail reads, direct read-only dispatch, policy-owned approval/sandbox launch settings, model/effort/fast-mode overrides, repeated `codex_status wait=true` calls with `timeoutSeconds = 20`, compact result reads, and explicit `subagent_manager` workflow routing with a no-op/read-only `$subagent-manager` prompt.
- *Codex*: `RecoveryWaitingQueuedInputCancellationSupervisorAndOutputPaginationSmoke` covers persisted job state under `.codex-manager/`, job listing after service recreation, structured `waiting_for_input`, valid continuation input, queued-input creation, pending queued-input cancellation, supervisor FIFO delivery after successful completion, active-job cancellation, and `codex_read_output` pagination with offsets, limits, and text formatting.
- *Codex*: `PolicyUsageChannelNotificationAndCliFallbackSmoke` covers unknown profile rejection, repo allowlist rejection, workflow allowlist rejection, `maxConcurrentJobs = 1`, `codex_usage` percentage/statusline normalization, compact channel notification payload emission when channel support is enabled in policy, failed/completed/waiting notification events, and CLI fallback degraded capability reporting with unknown usage/statusline fields.

## Acceptance Criteria Coverage

- *Codex*: Criteria 1-3 are covered by profile, skill, and agent listing smoke checks, including no default full skill bodies or agent prompts.
- *Codex*: Criteria 4-8 are covered by start, status, result, send-input, and cancel smoke flows.
- *Codex*: Criteria 9-12 are covered by `.codex-manager/` job persistence checks, policy rejection checks, full-output pagination checks, and dispatch override checks.
- *Codex*: Criteria 13-14 are covered by usage and statusline smoke checks.
- *Codex*: Criteria 15-17 are covered by explicit workflow routing, workflow rejection, recovered job listing, structured `waiting_for_input`, and response through `codex_send_input`.
- *Codex*: Criteria 18-21 are covered by launch policy assertions, `maxConcurrentJobs = 1`, queued input delivery, and queued input cancellation.
- *Codex*: Criteria 22-24 are covered by supervisor-driven queue delivery, repeated short wait calls capped at 20 seconds in the smoke flow, and non-empty title dispatch.
- *Codex*: Criteria 25-26 are covered as fallback-aware behavior: compact channel events are tested when enabled by policy, but live channel delivery was not verified in Stage 5, so polling through `codex_list_jobs`, `codex_status`, and `codex_result` remains the accepted monitoring path.

## Degraded Or Unverified Capabilities

- *Codex*: Live Claude Code Channel delivery remains unverified because Stage 5 had no active channel-enabled receiver. Channel support is therefore disabled by default; production behavior is best-effort when enabled and polling is authoritative.
- *Codex*: The Stage 14 automated smoke suite uses deterministic in-process MCP service calls and fake backend implementations for repeatability. It does not start an unattended interactive Claude Code MCP session; Manual Smoke Gate C should perform that human-visible registration/session check.
- *Codex*: CLI fallback is intentionally degraded. Unsupported live status observation, follow-up input, usage/context windows, and resume are reported through `degradedCapabilities`, and unavailable usage/statusline fields render as `?`.

## Result

- *Codex*: PASS. Automated Stage 14 smoke tests, solution build, and full solution tests passed.
- *Codex*: Manual Smoke Gate C remains open for manager/human review.
