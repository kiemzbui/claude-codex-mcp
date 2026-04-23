# Architecture Design: Implement Claude Codex MCP MVP

## Source Basis

This work item implements the architecture described in the root docs:

- `Docs/requirements.md:22-32` defines Claude -> MCP server -> Codex as the target architecture.
- `Docs/requirements.md:466-548` defines the durable `.codex-manager/` state model.
- `Docs/requirements.md:605-663` defines app-server primary backend, CLI fallback, backend feasibility, channel feasibility, supervisor, and stale recovery.
- `Docs/requirements.md:667-676` defines the .NET 10 Generic Host, official C# MCP SDK, stdio, logging, and hosted-service constraints.
- `Docs/proposed_workflow.md:123-136` defines first-class workflows.
- `Docs/proposed_workflow.md:376-415` defines channel and polling behavior.
- `Docs/architecture_design.md:12-30` fixes this repo as the root and gives the required non-`src` project layout.
- `Docs/architecture_design.md:52-67` gives the ownership boundaries used by this plan.

## Repository Boundary

The repository root is:

```text
C:\Users\misterkiem\source\repos\claude-codex-mcp
```

Implementation artifacts must land directly under this root:

```text
ClaudeCodexMcp.sln
ClaudeCodexMcp/
  ClaudeCodexMcp.csproj
ClaudeCodexMcp.Tests/
  ClaudeCodexMcp.Tests.csproj
```

Do not create a nested `src/` repository. `Docs/` is planning source material and should not be used for production source files. `.codex-manager/` is runtime state and must be ignored by Git.

## Process Architecture

`ClaudeCodexMcp` is a .NET 10 Generic Host console process using stdio MCP transport.

Rules:

- stdout is reserved for MCP protocol traffic.
- logs go to stderr or structured files.
- hosted background work uses `BackgroundService` or `IHostedService`.
- MCP tools expose stable requests and compact DTOs.
- backend-specific protocol details stay behind `ICodexBackend`.

## Module Ownership

- `Program.cs`: Generic Host composition, MCP registration, options, logging, hosted services, and dependency injection.
- `Configuration/`: `codex-manager.json` loading, profile definitions, allowed repos, channel policy, model/effort/fast-mode defaults, override policy, and concurrency limits.
- `Workflows/`: canonical workflow names and allowlist validation.
- `Domain/`: shared immutable records/enums/DTOs for jobs, queues, tools, usage, backend capabilities, notifications, and output pages.
- `Storage/`: all `.codex-manager/` JSON and JSONL persistence.
- `Discovery/`: read-only Codex skill and agent discovery with cache invalidation.
- `Backend/`: `ICodexBackend`, `CodexAppServerBackend`, `CodexCliBackend`, capability reports, app-server feasibility probe, and app-server protocol artifacts under `Backend/AppServerProtocol/**`.
- `Tools/`: MCP tool classes and response shaping only; no raw Codex command construction.
- `Supervisor/`: background observation, restart recovery, stale backend recovery, queue delivery, and per-job locking.
- `Notifications/`: channel feasibility probe, notification dispatch, Claude Code channel notifier, and notification persistence.
- `Usage/`: context and account usage normalization plus statusline rendering.

## Durable State Layout

All runtime state lives under root `.codex-manager/`:

```text
.codex-manager/
  jobs/
    index.json
    <job-id>.json
  queues/
    <job-id>.json
  logs/
    <job-id>.jsonl
  notifications/
    <job-id>.jsonl
  cache/
    skills.json
    agents.json
```

Ownership:

- `JobStore` owns `.codex-manager/jobs/*.json` and `.codex-manager/jobs/index.json`.
- `QueueStore` owns `.codex-manager/queues/<job-id>.json`.
- `OutputStore` owns `.codex-manager/logs/<job-id>.jsonl`.
- `NotificationStore` owns `.codex-manager/notifications/<job-id>.jsonl`.
- `CodexCapabilityDiscovery` owns `.codex-manager/cache/skills.json` and `.codex-manager/cache/agents.json`.

`jobs/index.json` is an optimization. The implementation must be able to rebuild it by scanning job records.

## Dispatch Invariants

`codex_start_task` must use this order:

1. Validate required inputs and reject blank `title`.
2. Load profile and validate repo allowlist.
3. Validate workflow allowlist and dispatch overrides.
4. Enforce `maxConcurrentJobs`.
5. Create durable job state and log paths.
6. Start the selected backend with server-owned approval/sandbox bypass policy.
7. Persist backend thread/session IDs when available.
8. Return compact state with job ID, title, profile, workflow, repo, selected options, notification mode, and statusline.

This order prevents backend side effects before policy validation and makes accepted work recoverable from disk.

Continuation tools use the same profile policy and per-job lock. `codex_send_input` validates any continuation `model`, `effort`, or `fastMode` override before backend side effects, then persists the selected continuation options with the job update.

## Job And Queue Invariants

- Allowed job states are `queued`, `running`, `waiting_for_input`, `completed`, `failed`, and `cancelled`.
- Allowed queue item states are `pending`, `delivered`, `failed`, and `cancelled`.
- Terminal job states do not return to `running`.
- `waiting_for_input` is only for genuine Codex clarification prompts, never permission or sandbox approvals.
- Queue items are delivered FIFO after the active turn completes successfully.
- Queue items remain pending when the active turn fails, is cancelled, or reaches `waiting_for_input`.
- `codex_cancel_queued_input` may cancel only pending queue items and must not cancel the active job.
- State transitions that affect a job, queue item, active turn, or cancellation must happen under a per-job lock or equivalent concurrency control.

## Backend Boundary

`ICodexBackend` normalizes:

- task/thread/turn start
- status observation or polling
- follow-up input
- cancellation
- final output and thread history
- usage/context data when available
- account usage windows when available
- resume/reconnect by backend ID when available

`CodexAppServerBackend` is primary only after Stage 4 proves the installed app-server can support the required features or documents gaps. `CodexCliBackend` is degraded fallback and must report unsupported capabilities explicitly through `degradedCapabilities`.

The app-server MVP contract is pinned to the generated v2 protocol from local `codex-cli 0.122.0`. Production backend work should use only the approved MVP subset: thread/turn lifecycle, read/resume, skills, models, account usage, and required state notifications. File-system, shell, config mutation, marketplace mutation, plugin install/uninstall, login/logout management, feedback, Windows sandbox setup, and realtime APIs remain out of MVP.

Protocol provenance is a first-class backend artifact. Stage 4 owns `ClaudeCodexMcp/Backend/AppServerProtocol/**` and must generate schema evidence with `codex app-server generate-json-schema --experimental --out ClaudeCodexMcp/Backend/AppServerProtocol/Schema`, generate a TypeScript reference with `codex app-server generate-ts --experimental --out ClaudeCodexMcp/Backend/AppServerProtocol/TypeScript`, and vendor or generate the minimal C# binding surface under `ClaudeCodexMcp/Backend/AppServerProtocol/CSharp/**`. `ClaudeCodexMcp/Backend/AppServerProtocol/provenance.md` records the CLI version, resolved executable path, commands, timestamp, approved method/notification subset, and any schema gaps. Stage 6 may consume those artifacts for `CodexAppServerBackend`, but the feasibility probe and generated schema/reference evidence remain owned by Stage 4 unless regeneration is explicitly documented.

## Discovery Boundary

Global Codex skills and agents are discovered from Codex defaults:

- `$CODEX_HOME\skills` and `$CODEX_HOME\agents` when `CODEX_HOME` is set.
- `%USERPROFILE%\.codex\skills` and `%USERPROFILE%\.codex\agents` otherwise.

Missing roots are empty, not errors. Explicit configured skill entries from Codex config are included as `configured` source-scope items.

Discovery responses preserve source buckets:

- `global`
- `repoLocal`
- `configured`
- optional `merged`

Every item keeps `sourceScope`, `sourcePath`, `enabled`, and conflict metadata. Name collisions are surfaced through `conflictsWith`; they are not silently deduped. `codex_get_skill` and `codex_get_agent` are MVP tools and default to metadata-only responses unless full body/prompt retrieval is explicitly requested.

## Notification Boundary

Channel events are wake-up signals, not source-of-truth state. They include compact identifiers and statusline fields, then Claude must call `codex_status`, `codex_result`, or `codex_read_output`.

Channel payloads must not include raw logs, full output, full transcripts, secrets, or long diffs. Channel failures must not change job state. Polling and recovery through `codex_list_jobs`, `codex_status`, and `codex_result` remain mandatory.

The Stage 5 channel feasibility probe under `ClaudeCodexMcp/Notifications/ChannelFeasibility/**` is evidence, not production notification code. Stage 12 must implement production notification dispatch outside that folder and preserve the probe/report unless a later explicit feasibility rerun updates them.

## Response Budget Boundary

Response budgets are enforced as UTF-8 byte limits:

- summary: 8 KB
- normal: 32 KB
- full: 128 KB
- paginated output chunk: 64 KB
- absolute hard cap: 256 KB
- channel event hard cap: 4 KB

Larger content is persisted under `.codex-manager/` and retrieved with pagination. Truncation must happen inside string fields before JSON serialization so responses remain valid.

## Implementation Ordering Rationale

The plan implements infrastructure before behavior:

1. Scaffold stdio, options, and logging first so protocol safety is correct.
2. Add profile/workflow validation before any backend side effects.
3. Add durable storage before long-running work.
4. Run app-server and channel feasibility gates before production dependency.
5. Add backend abstraction and core tools.
6. Add supervisor and queue delivery after storage and backend contracts stabilize.
7. Add usage, full output, notifications, and CLI fallback after core lifecycle behavior works.
8. Finish with end-to-end smoke coverage.

No initial parallel batch is proposed because the shared contracts and service registration are still volatile until the MVP shape is implemented.
