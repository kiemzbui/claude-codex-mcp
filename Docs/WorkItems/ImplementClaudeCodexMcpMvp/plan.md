# Implement Claude Codex MCP MVP

## Summary

Implement the Claude Codex MCP Manager as a local .NET 10 / C# stdio MCP server that lets Claude start, monitor, continue, cancel, and inspect Codex work through stable tools while Codex remains the coding worker. The implementation must use the official C# MCP SDK package `ModelContextProtocol`, `Microsoft.Extensions.Hosting`, and a Generic Host console process.

This plan is shaped for a future `$orchestrate execute Docs/WorkItems/ImplementClaudeCodexMcpMvp` run. It is independently executable from this work item and the source docs under `Docs/`.

## Source Evidence

- `Docs/requirements.md:22-32` defines the target routing: Claude calls MCP tools, the server owns invocation details, and Codex remains the worker.
- `Docs/requirements.md:60-109` locks percentage-based usage, full output access, dispatch overrides, workflow selection, approval bypass policy, queued input, channel preference, and estimate-based context reporting.
- `Docs/requirements.md:177-228` defines profile fields and dispatch option validation.
- `Docs/requirements.md:229-312` defines job lifecycle tools, queue tools, job states, and `waiting_for_input` behavior.
- `Docs/requirements.md:313-451` defines reconnection, channel notification, compact response, statusline, and paginated full-output behavior.
- `Docs/requirements.md:466-548` defines `.codex-manager/` persistence, job record fields, queue record fields, and title requirements.
- `Docs/requirements.md:550-587` defines `codex_usage` and statusline fields.
- `Docs/requirements.md:589-663` defines safety, backend feasibility, channel feasibility, background supervisor, stale recovery, and CLI degraded fallback.
- `Docs/requirements.md:667-676` requires .NET 10, Generic Host, official C# MCP SDK, stdio transport, stderr/file logging, and hosted services.
- `Docs/requirements.md:781` requires generating or vendoring Codex app-server protocol bindings from the installed app-server schema.
- `Docs/requirements.md:747-775` lists MVP acceptance criteria.
- `Docs/proposed_workflow.md:15-22` defines profiles as execution policy and `workflow` as a first-class dispatch field.
- `Docs/proposed_workflow.md:123-136` defines supported workflow names.
- `Docs/proposed_workflow.md:376-415` defines reconnection, channel, and polling behavior.
- `Docs/proposed_workflow.md:438-469` defines concurrent-job and queued-input behavior.
- `Docs/proposed_workflow.md:485-502` defines the visible statusline format.
- `Docs/architecture_design.md:12-30` fixes the repository root and required project layout.
- `Docs/architecture_design.md:52-67` defines implementation ownership boundaries.

## Problem Statement

Claude needs a context-efficient, durable control surface for Codex work. Today the workflow depends on direct chat coordination and manual command construction. The MCP manager must provide stable, compact tools that validate policy, persist state under `.codex-manager/`, hide backend details, and allow Claude to recover work without retaining chat history.

## Goals

- Create the implementation at the repository root using this exact layout:
  - `ClaudeCodexMcp.sln`
  - `ClaudeCodexMcp/ClaudeCodexMcp.csproj`
  - `ClaudeCodexMcp.Tests/ClaudeCodexMcp.Tests.csproj`
- Implement a .NET 10 Generic Host console app with stdio MCP transport using `ModelContextProtocol` and `Microsoft.Extensions.Hosting`.
- Reserve stdout for MCP protocol traffic; send diagnostics to stderr or structured files.
- Implement profile/workflow validation, durable state, backend abstraction, MCP tools, supervisor, queue delivery, output pagination, usage/statusline reporting, channel notifications, CLI fallback, and smoke tests.
- Keep tool responses compact by default and provide paginated access to exact output.
- Preserve Codex as the worker; do not implement a second coding-agent framework.

## Non-Goals

- Do not place the implementation in a nested `src/` repository.
- Do not implement arbitrary shell execution through MCP tools.
- Do not expose approval/sandbox bypass as a caller-controlled option.
- Do not mirror full logs, transcripts, prompts, diffs, or secrets into default MCP responses.
- Do not replace Codex skills, Codex agents, or `$orchestrate` mechanics.
- Do not invent decisions not captured in this work item. The previous source-doc open questions are now resolved under `## Resolved Design Decisions`; gate only app-server and channel runtime capability behind feasibility reports.

## Current Constraints

- Repo root is `C:\Users\misterkiem\source\repos\claude-codex-mcp`.
- `Docs/` is planning source material. Implementation work must not treat `Docs/` as source code. If a future implementation Git boundary is introduced, ignore `Docs/` there as planning-only material; in this repo, this work item remains intentionally under `Docs/`.
- Runtime state must be under root `.codex-manager/`; `.codex-manager/` must be ignored by Git.
- The target environment is local Windows first, with platform-safe path handling.
- The app-server and Claude Code Channel integrations require feasibility gates before full dependent features are built.
- Global Codex capability discovery should use Codex defaults on Windows: `CODEX_HOME` when set, otherwise `%USERPROFILE%\.codex`, with `skills\` and `agents\` underneath.

## Target Architecture

Runtime topology:

```text
Claude Code
  -> stdio MCP tools
    -> ClaudeCodexMcp Generic Host
      -> Tools validate compact MCP requests
      -> Configuration and Workflows enforce profiles, allowlists, and dispatch options
      -> Storage persists jobs, queues, logs, output, notifications, and discovery cache under .codex-manager/
      -> Backend normalizes Codex app-server and CLI behavior behind ICodexBackend
      -> Supervisor observes active jobs, delivers queued input, and emits notifications
      -> Usage normalizes context and account windows into statusline fields
```

Primary implementation directories:

- `ClaudeCodexMcp/Configuration/`
- `ClaudeCodexMcp/Workflows/`
- `ClaudeCodexMcp/Domain/`
- `ClaudeCodexMcp/Storage/`
- `ClaudeCodexMcp/Discovery/`
- `ClaudeCodexMcp/Backend/`
- `ClaudeCodexMcp/Tools/`
- `ClaudeCodexMcp/Supervisor/`
- `ClaudeCodexMcp/Notifications/`
- `ClaudeCodexMcp/Usage/`
- `ClaudeCodexMcp.Tests/`

## Implementation Stages

### Stage 1: Scaffold, Options, And Logging

Owned write scope: `ClaudeCodexMcp.sln`, `.gitignore`, `codex-manager.example.json`, `ClaudeCodexMcp/**`, `ClaudeCodexMcp.Tests/**`.

Intent:

- Establish the exact root solution/project layout and safe stdio process behavior.

Work items:

- Create `ClaudeCodexMcp.sln`.
- Create `ClaudeCodexMcp/ClaudeCodexMcp.csproj` targeting `net10.0`.
- Add packages for `ModelContextProtocol`, `Microsoft.Extensions.Hosting`, options/configuration, logging, and test support.
- Create `ClaudeCodexMcp.Tests/ClaudeCodexMcp.Tests.csproj`.
- Implement `Program.cs` with a Generic Host console entrypoint, MCP server registration placeholder, options binding, and stderr/file logging.
- Add `.gitignore` entries for `.codex-manager/`, `bin/`, `obj/`, test artifacts, and local secrets.
- Add `codex-manager.example.json` documenting minimal profile shape without secrets.

Out of scope:

- Do not implement backend startup, job lifecycle, channels, or queue delivery.

Exit criteria:

- `dotnet build ClaudeCodexMcp.sln` succeeds.
- `dotnet test ClaudeCodexMcp.sln` succeeds with at least one smoke test for options binding/logging behavior.
- Verifier confirms no diagnostic logs are intentionally written to stdout in stdio mode.

### Stage 2: Profile And Workflow Validation

Owned write scope: `ClaudeCodexMcp/Configuration/**`, `ClaudeCodexMcp/Workflows/**`, profile/workflow domain types under `ClaudeCodexMcp/Domain/**`, matching tests under `ClaudeCodexMcp.Tests/Configuration/**` and `ClaudeCodexMcp.Tests/Workflows/**`.

Intent:

- Enforce profile policy before any backend side effect.

Work items:

- Implement `ManagerOptions`, `ProfileOptions`, dispatch option models, and validation results.
- Model every required profile field: `repo`, `allowedRepos`, `taskPrefix`, `backend`, `readOnly`, `permissions`, `defaultWorkflow`, `allowedWorkflows`, `maxConcurrentJobs`, `channelNotifications`, default model, default effort, and fast-mode or service-tier default.
- Implement canonical workflows: `direct`, `subagent_manager`, `prepare_orchestrate_plan`, `managed_plan`, `orchestrate_execute`, `orchestrate_revise`.
- Implement repo allowlist validation using platform-safe path normalization.
- Implement workflow allowlist validation.
- Implement dispatch override validation for `model`, `effort`, and `fastMode`.
- Apply profile defaults for omitted dispatch options and reject overrides disallowed by the selected profile.
- Implement `maxConcurrentJobs` defaulting to `1`.
- Reject blank profile names, missing workflows, invalid effort values, and blank titles at the policy layer.

Out of scope:

- Do not start Codex or create job files.
- Do not implement MCP tool methods beyond thin placeholders needed for compilation.

Exit criteria:

- Unit tests cover valid profile load, unknown profile rejection, repo allowlist rejection, workflow rejection, invalid effort rejection, blank title rejection, and default `maxConcurrentJobs = 1`.
- Unit tests explicitly cover `taskPrefix`, `backend`, `readOnly`, `permissions` summary data, `channelNotifications` defaults, model default and override policy, effort default and allowed values, and fast-mode or service-tier default.

### Stage 3: Durable Job, Queue, Output, And Notification Storage

Owned write scope: `ClaudeCodexMcp/Domain/**`, `ClaudeCodexMcp/Storage/**`, storage tests under `ClaudeCodexMcp.Tests/Storage/**`.

Intent:

- Make every accepted job recoverable from `.codex-manager/` before backend work exists.

Work items:

- Implement job states: `queued`, `running`, `waiting_for_input`, `completed`, `failed`, `cancelled`.
- Implement queue item states: `pending`, `delivered`, `failed`, `cancelled`.
- Implement JSON file stores:
  - `.codex-manager/jobs/<job-id>.json`
  - `.codex-manager/jobs/index.json`
  - `.codex-manager/queues/<job-id>.json`
  - `.codex-manager/logs/<job-id>.jsonl`
  - `.codex-manager/notifications/<job-id>.jsonl`
  - `.codex-manager/cache/skills.json`
  - `.codex-manager/cache/agents.json`
- Make `jobs/index.json` reconstructable by scanning job records.
- Store compact queue summaries in job records and full queued prompt bodies only in queue files.
- Implement atomic write or write-then-replace behavior for JSON state.
- Add storage-level redaction/truncation helpers for default response projections.

Out of scope:

- Do not implement automatic queue delivery.
- Do not implement backend event interpretation beyond append-only output logging.

Exit criteria:

- Tests prove job/index reconstruction, queue persistence, FIFO ordering by `createdAt`, output append/read, notification append/read, and prompt-body exclusion from compact projections.

### Stage 4: App-Server Feasibility Gate

Owned write scope: `ClaudeCodexMcp/Backend/AppServerFeasibility/**`, `ClaudeCodexMcp/Backend/AppServerProtocol/**`, `Docs/WorkItems/ImplementClaudeCodexMcpMvp/app_server_feasibility.md`, focused tests under `ClaudeCodexMcp.Tests/Backend/**`.

Intent:

- Verify the installed Codex app-server can support the required MVP before implementation depends on it.

Work items:

- Create `ClaudeCodexMcp/Backend/AppServerProtocol/` with:
  - `Schema/**` for the generated JSON Schema bundle.
  - `TypeScript/**` for the generated TypeScript protocol reference.
  - `CSharp/**` for generated or vendored C# protocol bindings used by the backend.
  - `provenance.md` for binding provenance and regeneration evidence.
- Generate the schema evidence from the installed CLI with:
  - `codex app-server generate-json-schema --experimental --out ClaudeCodexMcp/Backend/AppServerProtocol/Schema`
- Generate the TypeScript reference from the installed CLI with:
  - `codex app-server generate-ts --experimental --out ClaudeCodexMcp/Backend/AppServerProtocol/TypeScript`
- Vendor or generate the minimal C# binding surface under `ClaudeCodexMcp/Backend/AppServerProtocol/CSharp/**` for only the approved MVP app-server methods and notifications.
- Record binding provenance in both `ClaudeCodexMcp/Backend/AppServerProtocol/provenance.md` and `app_server_feasibility.md`: `codex --version`, `Get-Command codex` resolved executable path, generation commands, generation timestamp, output paths, approved method/notification subset, and any schema gaps.
- Add a focused validation test or probe assertion that the approved MVP method and notification names are present in the generated schema/reference artifacts or are explicitly documented as gaps.
- Add a minimal app-server probe or test harness that can run outside normal MCP tool handling.
- Prototype one `codex_start_task`-equivalent call against the installed Codex app-server.
- Verify and document whether the backend can:
  - start a thread or turn
  - stream or poll status
  - read final output
  - expose token usage and context window
  - expose account rate-limit windows
  - resume or read prior thread state
- Write `app_server_feasibility.md` with command used, environment assumptions, observed capabilities, gaps, and fallback implications.

Out of scope:

- Do not implement production `CodexAppServerBackend` beyond probe code and protocol artifacts needed to prove the contract.
- Do not hide failed capabilities; document degraded behavior instead.

Manual smoke gate:

- Stop after this stage. The manager must confirm whether `app_server_feasibility.md` supports continuing with app-server-first behavior or whether dependent backend stages must use documented degraded behavior.

Exit criteria:

- Feasibility report exists and explicitly answers every required capability.
- Tests or probe output demonstrate at least the path used to reach the conclusion.
- Protocol schema/reference artifacts, C# binding artifacts, and provenance exist under `ClaudeCodexMcp/Backend/AppServerProtocol/**`.
- Tests or probe output validate the approved MVP method and notification subset against the generated artifacts, or the feasibility report documents unsupported gaps and fallback implications.

### Stage 5: Channel Feasibility Gate

Owned write scope: `ClaudeCodexMcp/Notifications/ChannelFeasibility/**`, `Docs/WorkItems/ImplementClaudeCodexMcpMvp/channel_feasibility.md`, focused tests under `ClaudeCodexMcp.Tests/Notifications/**`.

Intent:

- Verify Claude Code Channel delivery before building production channel notifications.

Work items:

- Add a minimal channel probe that can declare/register the required channel shape for the target Claude Code environment.
- Record environment prerequisites before sending the event: Claude Code version, whether the version is at least `2.1.80`, whether the target version is `2.1.117`, Claude `claude.ai` login status when observable, and the channel-capable launch/configuration used, such as `--channels` or the current development-channel mechanism.
- Emit one compact `notifications/claude/channel` event.
- Verify whether an active Claude Code session receives the event.
- Write `channel_feasibility.md` with channel configuration, command used, payload shape, observed result, and fallback decision.
- If channel delivery cannot be verified, document that channel support remains disabled by default and polling remains the active path.

Out of scope:

- Do not implement production notification dispatch.
- Do not make channel events lifecycle-authoritative.

Manual smoke gate:

- Stop after this stage. The manager must confirm whether production channel notification work is enabled by default or implemented as disabled-by-default fallback-aware support.

Exit criteria:

- Feasibility report exists and states whether channel delivery was verified.
- Feasibility report records Claude Code version, login/channel prerequisite evidence, launch/configuration command, and any unavailable prerequisite checks.
- A failed or unavailable channel path still leaves polling as the documented fallback.

### Stage 6: Backend Abstraction And Minimal Lifecycle

Owned write scope: `ClaudeCodexMcp/Backend/**` except `ClaudeCodexMcp/Backend/AppServerFeasibility/**`, `ClaudeCodexMcp/Backend/AppServerProtocol/Schema/**`, `ClaudeCodexMcp/Backend/AppServerProtocol/TypeScript/**`, and `ClaudeCodexMcp/Backend/AppServerProtocol/provenance.md`; backend-related domain DTOs under `ClaudeCodexMcp/Domain/**`; tests under `ClaudeCodexMcp.Tests/Backend/**`. Stage 6 may read `ClaudeCodexMcp/Backend/AppServerFeasibility/**` and all `ClaudeCodexMcp/Backend/AppServerProtocol/**` artifacts. Stage 6 may update `ClaudeCodexMcp/Backend/AppServerProtocol/CSharp/**` only when production backend implementation requires binding adjustments.

Intent:

- Hide Codex app-server and CLI mechanics behind a stable backend contract.

Work items:

- Define `ICodexBackend` for start, observe/poll status, send follow-up input, cancel, read final output, read usage/context, and resume/reconnect.
- Implement backend capability records and `degradedCapabilities`.
- Implement production `CodexAppServerBackend` using the Stage 4 feasibility outcome and `ClaudeCodexMcp/Backend/AppServerProtocol/**` binding artifacts.
- Ensure backend follow-up input accepts selected continuation dispatch options for `model`, `effort`, and `fastMode` after policy validation has already selected them.
- Preserve Codex thread/session IDs when available.
- Store backend events in local output logs through `OutputStore`.
- Ensure backend launch policy includes approval/sandbox bypass by server policy and never accepts it as a dispatch option.
- Add fake backend implementations for deterministic unit tests.

Out of scope:

- Do not implement MCP tools or supervisor orchestration in this stage.
- Do not implement CLI fallback beyond interfaces and capability shape.

Exit criteria:

- Tests cover backend lifecycle mapping to normalized job states, capability gap reporting, backend IDs, event logging, approval-bypass policy non-configurability, and continuation dispatch option pass-through to backend requests.

### Stage 7: Core MCP Tool Surface

Owned write scope: `ClaudeCodexMcp/Tools/**`, `ClaudeCodexMcp/Discovery/**`, tool DTOs under `ClaudeCodexMcp/Domain/**`, tests under `ClaudeCodexMcp.Tests/Tools/**` and `ClaudeCodexMcp.Tests/Discovery/**`. Stage 7 may also update `ClaudeCodexMcp/ClaudeCodexMcpHost.cs` for MCP tool registration, and `ClaudeCodexMcp/Domain/JobRecords.cs` plus `ClaudeCodexMcp/Storage/JobStore.cs` only for persisted backend identity needed by the Stage 7 tool surface.

Intent:

- Expose compact, stable tools for profiles, discovery, core job lifecycle, and reconnectable job listing.

Work items:

- Implement `codex_list_profiles`.
- Ensure `codex_list_profiles` returns compact policy summaries for `taskPrefix`, `backend`, `readOnly`, `permissions`, `channelNotifications`, default model, default effort, and fast-mode or service-tier default without exposing raw command templates.
- Implement read-only `codex_list_skills` and `codex_list_agents` with cache records, mtime/TTL invalidation, and `forceRefresh`.
- Implement `codex_get_skill` and `codex_get_agent` as MVP tools.
- Discover global Codex skills and agents from `$CODEX_HOME\skills` and `$CODEX_HOME\agents` when `CODEX_HOME` is set, otherwise `%USERPROFILE%\.codex\skills` and `%USERPROFILE%\.codex\agents`.
- Honor explicitly configured skill entries in Codex config when present.
- Treat missing discovery directories as empty, not errors.
- Return discovery results by source bucket: `global`, `repoLocal`, `configured`, and optional `merged`.
- Preserve `sourceScope`, `sourcePath`, `enabled`, and conflict metadata on every discovery item.
- Surface name collisions through `conflictsWith`; do not silently dedupe.
- Implement `codex_start_task`, `codex_status`, `codex_result`, `codex_send_input`, `codex_cancel`, and `codex_list_jobs`.
- Ensure `codex_start_task` creates durable state before backend dispatch and returns compact accepted/running status.
- Ensure `codex_send_input` accepts optional `model`, `effort`, and `fastMode`, validates them against the job profile policy, persists selected continuation options, and passes them to the backend only after validation succeeds.
- Reject disallowed continuation overrides before backend side effects.
- Ensure `codex_status wait=true` caps `timeoutSeconds` at 25 and defaults to 20.
- Ensure `waiting_for_input` responses include structured request data.
- Make `codex_get_skill` and `codex_get_agent` default to metadata-only responses; full bodies/prompts require explicit opt-in and truncation safeguards.

Out of scope:

- Do not implement automatic queue delivery, `codex_usage`, `codex_read_output`, production channel push, or CLI fallback.

Exit criteria:

- Tests cover compact tool responses, title enforcement, rejected policy violations, status wait cap, reconnectable job list, profile policy summaries, read-only discovery, source bucket preservation, conflict reporting, cache invalidation, detail-tool ambiguity handling, continuation override acceptance/persistence/backend dispatch, continuation override rejection before backend side effects, and no default full prompt/body leakage.

### Stage 8: Background Supervisor

Owned write scope: `ClaudeCodexMcp/Supervisor/**`, supervisor-facing backend/storage interfaces if needed, tests under `ClaudeCodexMcp.Tests/Supervisor/**`. Stage 8 may also update backend/domain/tool/host registration files when required for supervisor polling fallback, persisted supervisor status fields, and shared per-job locking with tool-driven writes: `ClaudeCodexMcp/Backend/**`, `ClaudeCodexMcp/Domain/**`, `ClaudeCodexMcp/Tools/CodexToolService.cs`, `ClaudeCodexMcp/ClaudeCodexMcpHost.cs`, and matching focused tests under `ClaudeCodexMcp.Tests/Tools/**`.

Intent:

- Run job observation independently of individual MCP tool calls.

Work items:

- Implement `CodexJobSupervisor` as `BackgroundService` or `IHostedService`.
- On startup, scan `.codex-manager/`, reconstruct the index, and resume active jobs.
- Observe active jobs through backend event streams when available and bounded polling otherwise.
- Persist status, output summaries, usage snapshots when available, changed files, test summaries, and last errors.
- Move genuine clarification prompts into `waiting_for_input`.
- Implement per-job locking so status refresh, cancellation, queue delivery, and user input do not race.
- Implement stale backend recovery behavior and `backend_thread_unrecoverable` failure handling.

Out of scope:

- Do not deliver queued input yet.
- Do not emit production channel notifications yet.

Exit criteria:

- Tests cover startup recovery, index reconstruction, event/poll fallback, clarification status, terminal-state invariants, lock behavior, transient backend retry, and unrecoverable thread failure.

### Stage 9: Queued Input And Cancellation

Owned write scope: `ClaudeCodexMcp/Tools/**`, `ClaudeCodexMcp/Supervisor/**`, queue-specific storage/domain files under `ClaudeCodexMcp/Storage/**` and `ClaudeCodexMcp/Domain/**`, tests under `ClaudeCodexMcp.Tests/Tools/**`, `ClaudeCodexMcp.Tests/Supervisor/**`, and `ClaudeCodexMcp.Tests/Storage/**`.

Intent:

- Support server-delivered queued follow-up prompts and cancellation of pending queue items.

Work items:

- Implement `codex_queue_input`.
- Implement `codex_cancel_queued_input`.
- Reject cancellation of `delivered`, `failed`, or `cancelled` queue items.
- Add supervisor delivery of pending queued input in FIFO order after the active turn completes successfully.
- Keep pending items pending when the active turn fails, is cancelled, or reaches `waiting_for_input`.
- Persist delivery attempts, delivery failures, cancellations, and updated queue counts.

Out of scope:

- Do not implement production channel notifications for queue failures yet.

Exit criteria:

- Tests cover queue creation, queue position, FIFO delivery, failed/cancelled/waiting behavior, pending-only cancellation, active-job cancellation separation, persisted queue counts, and supervisor restart recovery.

### Stage 10: Usage And Statusline

Owned write scope: `ClaudeCodexMcp/Usage/**`, usage DTOs under `ClaudeCodexMcp/Domain/**`, `ClaudeCodexMcp/Tools/**` only for `codex_usage` registration, tests under `ClaudeCodexMcp.Tests/Usage/**` and focused tool tests. Stage 10 may also update `ClaudeCodexMcp/ClaudeCodexMcpHost.cs` only for usage service registration.

Intent:

- Normalize backend usage/context data into compact fields and the canonical statusline.

Work items:

- Implement `UsageReporter`.
- Implement `codex_usage`.
- Report short-window and long-window usage as percentages and reset times when available.
- Compute context remaining from backend token usage and context window when available.
- Mark unavailable values as `?` and estimate-based context explicitly.
- Return `contextRemainingPercentEstimate`, `weeklyUsageRemainingPercent`, `fiveHourUsageRemainingPercent`, and `statusline`.
- Ensure job status/result/send-input/read-output reports can include the statusline.

Out of scope:

- Do not infer absolute quotas or message counts unless the backend directly exposes them.

Exit criteria:

- Tests cover full data, partial data, unavailable data rendering as `?`, estimate labeling, remaining-percent semantics, and stable statusline format `[codex status: context ? | weekly ? | 5h ?]`.

### Stage 11: Full Output Pagination

Owned write scope: `ClaudeCodexMcp/Storage/OutputStore*`, `ClaudeCodexMcp/Tools/**` only for `codex_read_output` and `codex_result detail`, output DTOs under `ClaudeCodexMcp/Domain/**`, tests under `ClaudeCodexMcp.Tests/Storage/**` and `ClaudeCodexMcp.Tests/Tools/**`.

Intent:

- Provide exact output access without forcing whole transcripts into Claude context.

Work items:

- Implement `codex_read_output` with `jobId`, optional `threadId`, optional `turnId`, optional `agentId`, optional `offset`, optional `limit`, and optional `format`.
- Support pagination over local logs or backend thread history.
- Implement `codex_result detail="full"` with configured response-size safeguards.
- Return truncation markers and local artifact refs when full output exceeds configured limits.
- Keep summary as the default detail level.
- Enforce byte-based response budgets:
  - summary: 8 KB
  - normal: 32 KB
  - full: 128 KB
  - paginated output chunk: 64 KB max
  - absolute hard cap: 256 KB
  - channel event hard cap: 4 KB
- Truncate string fields before serialization so JSON responses remain valid.
- Include `truncated` and artifact/log references when output exceeds a response budget. When truncation is caused by additional paginated output, also include `nextOffset` or `nextCursor` and set `endOfOutput=false`. When truncation is field-level within an otherwise complete page, `endOfOutput=true` and no continuation marker are allowed only if artifact/log references identify where the exact untruncated content can be read.

Out of scope:

- Do not change backend event formats unless required by the output reader contract.

Exit criteria:

- Tests cover offsets, limits, end-of-output markers, missing output, truncation marker, valid JSON after truncation, response budget enforcement, artifact refs, field-level truncation with artifact/log recovery and no dead-end truncation, and default summary behavior.

### Stage 12: Channel Notifications

Owned write scope: `ClaudeCodexMcp/Notifications/**` except `ClaudeCodexMcp/Notifications/ChannelFeasibility/**`, notification DTOs under `ClaudeCodexMcp/Domain/**`, supervisor notification calls under `ClaudeCodexMcp/Supervisor/**`, tests under `ClaudeCodexMcp.Tests/Notifications/**` and focused supervisor tests.

Intent:

- Emit compact channel events for important job changes while retaining polling as fallback.

Work items:

- Implement `NotificationDispatcher`.
- Implement `ClaudeChannelNotifier` according to the Stage 5 channel feasibility outcome.
- Treat `ClaudeCodexMcp/Notifications/ChannelFeasibility/**` and `Docs/WorkItems/ImplementClaudeCodexMcpMvp/channel_feasibility.md` as read-only evidence. If probe logic is useful for production behavior, copy or adapt it into production notification files outside `ChannelFeasibility/**`.
- Emit required events for `job_waiting_for_input`, `job_completed`, `job_failed`, `job_cancelled`, and `queue_item_failed`.
- Optionally emit `job_started`, rate-limited `job_progress`, and `queue_item_delivered`.
- Persist attempted, observable delivered, and observable failed notification records.
- Ensure channel payloads contain compact identifiers and statusline but never raw logs, full output, full transcripts, secrets, or long diffs.
- Ensure channel failures never change job state and jobs remain recoverable through polling/listing.

Out of scope:

- Do not remove polling support.
- Do not make channel support mandatory when feasibility failed.
- Do not edit or delete the Stage 5 probe or feasibility evidence.

Exit criteria:

- Tests cover required event emission, compact payload projection, secret/full-output exclusion, notification persistence, disabled/default fallback mode, and failure-is-not-lifecycle behavior.

### Stage 13: CLI Fallback

Owned write scope: `ClaudeCodexMcp/Backend/CodexCliBackend*`, CLI-specific backend tests under `ClaudeCodexMcp.Tests/Backend/**`, docs note in `Docs/WorkItems/ImplementClaudeCodexMcpMvp/cli_fallback.md` if degraded capability details need recording.

Intent:

- Provide degraded operation when app-server is unavailable or lacks coverage.

Work items:

- Implement `CodexCliBackend` behind `ICodexBackend`.
- Support direct task execution, final output capture, changed-file/test summaries when practical.
- Mark unsupported capabilities explicitly in `degradedCapabilities`.
- Render unavailable context, account usage, clarification prompts, or resume support as `?` or documented gaps.
- Ensure profile backend selection can choose CLI fallback only when allowed by profile policy.

Out of scope:

- Do not make CLI fallback the default when app-server feasibility passed.
- Do not claim reliable resume, live usage, or clarification support unless proven.

Exit criteria:

- Tests cover degraded capability reporting, `?` statusline fields, direct execution mapping, output capture, profile backend selection, and unsupported feature handling.

### Stage 14: End-To-End Smoke Tests And Acceptance

Owned write scope: `ClaudeCodexMcp.Tests/Smoke/**`, `Docs/WorkItems/ImplementClaudeCodexMcpMvp/smoke_results.md`, any small test fixtures under `ClaudeCodexMcp.Tests/Fixtures/**`.

Intent:

- Confirm the MVP works through realistic local flows and satisfies acceptance criteria.

Work items:

- Add smoke test fixtures or scripts for:
  - read-only discovery of skills and agents
  - profile listing
  - direct read-only Codex execution
  - workflow routing with a no-op/read-only `$subagent-manager` prompt
  - job recovery after MCP restart
  - structured `waiting_for_input`
  - queued-input delivery
  - queued-input cancellation
  - concurrent-job policy
  - repeated `codex_status wait=true` calls with `timeoutSeconds = 20`
  - `codex_read_output` pagination
  - `codex_usage` and statusline formatting
  - channel events for completed, failed, and waiting-for-input states when channel feasibility passed
  - CLI fallback degraded behavior when app-server is disabled/unavailable
- Write `smoke_results.md` summarizing commands, environment, pass/fail results, and any accepted degraded capabilities.

Out of scope:

- Do not add cloud-hosted coordination or destructive cleanup flows.

Manual smoke gate:

- Stop for human confirmation after smoke results are written. The manager should review `smoke_results.md` and decide whether remaining failures block MVP acceptance or are documented degraded capabilities.

Exit criteria:

- `dotnet build ClaudeCodexMcp.sln` passes.
- `dotnet test ClaudeCodexMcp.sln` passes.
- Smoke results cover MVP acceptance criteria from `Docs/requirements.md:747-775`.
- Any failed channel or app-server capabilities are documented with fallback behavior.

## Parallelization Plan

No parallel batches are proposed for the initial execution plan. The implementation has shared startup, domain, persistence, backend, tool, supervisor, and notification contracts; sequencing those steps avoids cross-worker edits to shared DTOs, service registration, and tests.

If a later revision identifies stable disjoint write scopes after Stage 3, it may add a named batch to `execution_recs.md` and update `progress.md` with `Next executable batch: <batch-name> (...)`.

## Smoke-Test Plan

### Manual Smoke Gate A: App-Server Feasibility

After Stage 4, confirm the app-server report proves or rejects required backend capabilities before production backend work depends on them.

### Manual Smoke Gate B: Channel Feasibility

After Stage 5, confirm whether channel support is enabled-by-default or disabled-by-default with polling fallback.

### Manual Smoke Gate C: End-To-End MVP

After Stage 14, run the full smoke suite through the local Claude Code MCP configuration and review `smoke_results.md`.

## Risks

- Codex app-server APIs are experimental; the feasibility report must verify the pinned local protocol methods before backend-dependent implementation continues.
- Claude Code Channels are research-preview; channel support must never be the only monitoring path.
- Stdio MCP servers can break if logs go to stdout; scaffold and verifier checks must enforce stderr/file logging.
- Job, queue, supervisor, and cancellation behavior can race without per-job locking.
- Full output retrieval can overwhelm Claude context if pagination and max-size safeguards are weak.
- Discovery conflicts between global, repo-local, and configured capabilities can confuse routing if source scope is hidden; preserve source buckets and conflict metadata.

## Recommended Delivery Order

1. Stage 1: Scaffold, Options, And Logging.
2. Stage 2: Profile And Workflow Validation.
3. Stage 3: Durable Job, Queue, Output, And Notification Storage.
4. Stage 4: App-Server Feasibility Gate.
5. Manual Smoke Gate A.
6. Stage 5: Channel Feasibility Gate.
7. Manual Smoke Gate B.
8. Stage 6: Backend Abstraction And Minimal Lifecycle.
9. Stage 7: Core MCP Tool Surface.
10. Stage 8: Background Supervisor.
11. Stage 9: Queued Input And Cancellation.
12. Stage 10: Usage And Statusline.
13. Stage 11: Full Output Pagination.
14. Stage 12: Channel Notifications.
15. Stage 13: CLI Fallback.
16. Stage 14: End-To-End Smoke Tests And Acceptance.
17. Manual Smoke Gate C.

## Resolved Design Decisions

- App-server MVP contract: use generated v2 protocol from local `codex-cli 0.122.0`, limited to thread/turn lifecycle, read/resume, skills, models, account usage, and required state notifications. Keep CLI fallback if the feasibility gate fails.
- App-server protocol provenance: Stage 4 must generate schema evidence with `codex app-server generate-json-schema --experimental --out ClaudeCodexMcp/Backend/AppServerProtocol/Schema`, generate a TypeScript reference with `codex app-server generate-ts --experimental --out ClaudeCodexMcp/Backend/AppServerProtocol/TypeScript`, vendor or generate the minimal C# binding surface under `ClaudeCodexMcp/Backend/AppServerProtocol/CSharp/**`, and record CLI version, executable path, commands, timestamp, approved subset, and gaps in `ClaudeCodexMcp/Backend/AppServerProtocol/provenance.md`.
- MVP app-server methods: `initialize`, `thread/start`, `turn/start`, `turn/steer`, `turn/interrupt`, `thread/read`, `thread/turns/list`, `thread/list`, `thread/loaded/list`, `thread/resume`, `thread/unsubscribe`, `skills/list`, `plugin/list`, `plugin/read`, `model/list`, `account/read`, and `account/rateLimits/read`.
- MVP app-server notifications: `thread/started`, `thread/status/changed`, `turn/started`, `turn/completed`, `turn/diff/updated`, `turn/plan/updated`, `item/started`, `item/completed`, `item/agentMessage/delta`, `thread/tokenUsage/updated`, `account/rateLimits/updated`, `error`, and `warning`.
- Out of MVP app-server surface: `fs/*`, `command/*`, `config/*`, marketplace mutation, plugin install/uninstall, account login/logout management, feedback, Windows sandbox setup, and realtime APIs.
- Global discovery roots on Windows: use `$CODEX_HOME\skills` and `$CODEX_HOME\agents` when `CODEX_HOME` is set; otherwise use `%USERPROFILE%\.codex\skills` and `%USERPROFILE%\.codex\agents`. Treat missing directories as empty and honor explicit skill config entries.
- Repo-local, global, and configured skills/agents must be listed separately by source. A merged convenience view is allowed only if each item retains `sourceScope`, `sourcePath`, `enabled`, and conflict metadata.
- `codex_get_skill` and `codex_get_agent` are MVP tools. They default to metadata only; full body/prompt retrieval is explicit, truncated when needed, and ambiguity is rejected with a compact conflict list.
- Response size limits are byte-based: summary 8 KB, normal 32 KB, full 128 KB, paginated chunks 64 KB, absolute hard cap 256 KB, and channel events 4 KB.
