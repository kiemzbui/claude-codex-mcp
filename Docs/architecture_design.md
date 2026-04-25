# Architecture Design: Claude Codex MCP Manager

## Source Basis

This design synthesizes:

- `Docs/requirements.md`, especially target architecture, locked decisions, persistence, safety, backend, supervisor, implementation platform, maintainability, and open questions (`Docs/requirements.md:22`, `Docs/requirements.md:60`, `Docs/requirements.md:466`, `Docs/requirements.md:589`, `Docs/requirements.md:605`, `Docs/requirements.md:641`, `Docs/requirements.md:667`, `Docs/requirements.md:697`, `Docs/requirements.md:823`).
- `Docs/proposed_workflow.md`, especially the operating model, profile/workflow split, routing rules, reconnection behavior, queued input, statusline reporting, and design rule (`Docs/proposed_workflow.md:3`, `Docs/proposed_workflow.md:15`, `Docs/proposed_workflow.md:123`, `Docs/proposed_workflow.md:376`, `Docs/proposed_workflow.md:438`, `Docs/proposed_workflow.md:485`, `Docs/proposed_workflow.md:526`).

The core architecture rule is: Claude routes, the MCP server records and monitors, and Codex executes.

## Repository Boundary

This folder is the repository root. The implementation solution lands here, with the main project and test project one level deeper.

Root-level `Docs/` contains planning source material and should be ignored by the implementation repo when desired. Implementation paths in this document are relative to the repository root unless explicitly qualified.

```text
claude-codex-mcp/
  architecture_design.md
  Docs/
    requirements.md
    proposed_workflow.md
  codex-manager.json
  ClaudeCodexMcp.sln
  ClaudeCodexMcp/
    ClaudeCodexMcp.csproj
  ClaudeCodexMcp.Tests/
    ClaudeCodexMcp.Tests.csproj
```

## Target Shape

Implement a local .NET 10 / C# MCP server that runs as a .NET Generic Host console process over stdio. Stdout is reserved for MCP protocol traffic; diagnostics go to stderr or structured log files.

Runtime topology:

```text
Claude Code
  -> stdio MCP tools
    -> ClaudeCodexMcp Generic Host
      -> tool layer validates requests
      -> profile/workflow policy layer authorizes dispatch
      -> job store records durable state under .codex-manager/
      -> Codex app-server backend starts and observes Codex work
      -> hosted supervisor updates jobs, delivers queued input, writes session-bound wake signals, and emits optional notifications
      -> Claude Stop hook rewakes the same session from .codex-manager/wake-signals/<wakeSessionId>/
      -> optional CLI backend provides degraded fallback
```

The MCP server is a control surface, not a coding agent framework. It must not reinterpret `$subagent-manager`, `$prepare-orchestrate-plan`, or `$orchestrate`; it preserves those entrypoints mechanically and lets Codex own the workflow execution.

## Implementation Ownership Boundaries

Use the source structure suggested by the requirements as the ownership map, adapted to the `ClaudeCodexMcp` project layout (`Docs/requirements.md:697`):

- `Program.cs`: compose the Generic Host, bind options, register MCP tools, storage, backends, supervisor, notifications, and logging.
- `Tools/`: expose stable MCP tools only. Tool classes validate input shape, call application services, and return compact DTOs. They do not construct raw Codex commands directly.
- `Configuration/`: load `codex-manager.json`, profile definitions, allowed repos, allowed workflows, dispatch defaults, override policy, permissions summaries, optional channel policy, and concurrency limits.
- `Workflows/`: define canonical workflow names and profile allowlist checks. This layer preserves workflow identity as data instead of embedding workflow selection in prompts.
- `Discovery/`: discover Codex-visible skills and agents read-only, cache results, and invalidate by mtime or short TTL when mtime cannot be trusted.
- `Storage/`: own all `.codex-manager/` file formats and atomic persistence for jobs, queues, output logs, notification logs, wake-signal files, and discovery caches.
- `Backend/`: hide Codex app-server and CLI details behind `ICodexBackend`. This is the only layer allowed to know backend command/protocol specifics.
- `Supervisor/`: implement hosted background observation, queue delivery, restart recovery, stale backend recovery, and per-job concurrency control.
- `Notifications/`: send compact Claude Code Channel notifications when enabled and record attempts or failures. Notification delivery cannot mutate job lifecycle state.
- `Usage/`: normalize account usage windows, token/context data, estimates, and the preformatted statusline.

This boundary keeps MCP tools thin and makes backend replacement possible without changing Claude-facing contracts.

## Wake Contract

The authoritative wake path is session-bound and filesystem-backed.

- `codex_start_task` must capture or receive the caller Claude session id and persist it as `WakeSessionId` on the durable job record.
- The supervisor writes `.codex-manager/wake-signals/<wakeSessionId>/<jobId>.json` only when the job first becomes terminal.
- The Stop hook for that same Claude session watches only its own session directory and exits `2` after consuming the signal.
- Optional `notifications/claude/channel` delivery is best-effort only and never replaces the session-bound wake path.

## Tool Surface Ownership

The MCP tool layer exposes stable operations:

- Discovery: `codex_list_skills`, `codex_list_agents`, optional `codex_get_skill`, optional `codex_get_agent`.
- Profiles: `codex_list_profiles`.
- Jobs: `codex_start_task`, `codex_status`, `codex_result`, `codex_send_input`, `codex_queue_input`, `codex_cancel_queued_input`, `codex_cancel`, `codex_list_jobs`.
- Output and usage: `codex_read_output`, `codex_usage`.

Tool responses are compact by default. They return IDs, state, counts, summaries, statusline fields, and local artifact references. Full logs, transcripts, diffs, prompts, and event streams stay in storage unless explicitly requested through paginated output retrieval.

## Profile And Workflow Policy

Profiles are execution policy, not task behavior (`Docs/proposed_workflow.md:15`). They own:

- repo and `allowedRepos`
- `taskPrefix`
- backend selection
- read-only posture
- permission summary
- default and allowed workflows
- `maxConcurrentJobs`, defaulting to `1`
- channel notification policy
- model, effort, and fast-mode defaults and override policy

Workflows are first-class dispatch values (`Docs/requirements.md:75`, `Docs/proposed_workflow.md:123`). `codex_start_task` and continuation tools must preserve the selected workflow in the job record and reject workflows not allowed by the selected profile.

Supported workflow names:

- `direct`
- `subagent_manager`
- `prepare_orchestrate_plan`
- `managed_plan`
- `orchestrate_execute`
- `orchestrate_revise`

The profile/workflow layer must validate the request before any backend call or job activation.

## Dispatch Lifecycle

`codex_start_task` follows this safe ordering:

1. Validate required input: `profile`, `workflow`, `prompt`, and non-empty `title`.
2. Resolve and validate the target repo against the selected profile allowlist.
3. Validate workflow allowlist, model override, effort override, and fast-mode override.
4. Acquire the profile concurrency gate and reject or return a policy conflict if `maxConcurrentJobs` would be exceeded.
5. Create a durable job record and log path under `.codex-manager/` before starting backend work.
6. Start the Codex backend with the server-owned approval/sandbox bypass policy.
7. Persist backend identifiers such as thread or session ID as soon as they are known.
8. Return compact accepted/running status with job ID, title, profile, workflow, repo, dispatch options, notification mode, and statusline.

This order is safe because policy failures happen before side effects, concurrency is reserved before backend dispatch, and the job is recoverable from disk even if the process exits after Codex startup.

Continuation tools use the same profile policy and per-job lock. `codex_send_input` starts an immediate follow-up only when the job can accept one. `codex_queue_input` persists a future turn and lets the supervisor deliver it later.

## Durable State Layout

Store all manager state under `.codex-manager/`, matching the requirements layout (`Docs/requirements.md:466`):

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

State ownership:

- `JobStore` owns `.codex-manager/jobs/*.json` and `jobs/index.json`.
- `QueueStore` owns `.codex-manager/queues/<job-id>.json`.
- `OutputStore` owns `.codex-manager/logs/<job-id>.jsonl`.
- `NotificationStore` owns `.codex-manager/notifications/<job-id>.jsonl`.
- `CodexCapabilityDiscovery` owns `.codex-manager/cache/skills.json` and `agents.json`.

`jobs/index.json` is an optimization, not the only source of truth. It must be reconstructable by scanning job records.

## Job State Invariants

Allowed job states:

- `queued`
- `running`
- `waiting_for_input`
- `completed`
- `failed`
- `cancelled`

Required invariants:

- Every job has a non-empty user-facing `title`; the server rejects blank titles instead of generating them.
- Every job stores `profile`, `workflow`, `repo`, dispatch options, notification mode, status, timestamps, prompt summary, log path, and queue summary.
- Original prompts and queued prompt bodies may be persisted locally, but MCP responses do not echo them unless explicitly requested.
- Terminal states are lifecycle-terminal: `completed`, `failed`, and `cancelled` do not return to `running`.
- `waiting_for_input` is only for genuine Codex clarification prompts, never permission or sandbox approval prompts.
- Permission/sandbox bypass is server policy and is not exposed as a per-dispatch option.
- Backend events are appended to local logs and summarized into job records; raw event streams are not mirrored into Claude responses.
- All state transitions that affect a job, its active turn, or queue delivery happen under a per-job lock or equivalent concurrency primitive.

## Queue And Continuation Persistence

Queued input is server-delivered (`Docs/requirements.md:95`, `Docs/proposed_workflow.md:438`). The queue file stores full queue details for one job, while the job record stores compact counts and a queue path.

Queue item states:

- `pending`
- `delivered`
- `failed`
- `cancelled`

Queue invariants:

- Queue items are ordered by `createdAt` and delivered FIFO.
- `codex_queue_input` persists the item before returning a queue item ID.
- The supervisor delivers the next pending item only after the active Codex turn completes successfully.
- If the active turn fails, is cancelled, or reaches `waiting_for_input`, pending queue items remain pending.
- `codex_cancel_queued_input` may cancel only `pending` items. It must not cancel the active job or active turn.
- Job status and job lists expose compact queue counts, including pending and failed items.
- Queue delivery attempts, failures, and cancellations are persisted for auditability and recovery.

## Background Supervisor Responsibilities

Implement the supervisor as a hosted service (`BackgroundService` or `IHostedService`) because it must run independently of individual MCP tool calls (`Docs/requirements.md:641`, `Docs/requirements.md:667`).

Responsibilities:

- Resume from `.codex-manager/` on startup.
- Reconstruct or refresh the job index.
- Observe active Codex jobs through backend event streams when available.
- Fall back to bounded polling when event streams are unavailable.
- Update persisted job status, summaries, usage/context data, changed-file summaries, test summaries, and errors.
- Persist backend output/events to `OutputStore`.
- Persist `WakeSessionId` with job state and write one per-session terminal wake signal on first terminal transition.
- Detect Codex clarification prompts and move jobs to `waiting_for_input` with structured request data.
- Deliver queued input in FIFO order after successful active-turn completion.
- Emit optional compact channel notifications for required state changes.
- Apply per-job locking so status refresh, cancellation, queue delivery, and user input do not race.
- Retry transient backend reconnect failures with backoff.

Default active-job supervisor polling should be 10-15 seconds, distinct from Claude-facing polling cadence.

## Backend Boundary

`ICodexBackend` normalizes Codex operations:

- start task/thread/turn
- observe or poll status
- send follow-up input
- cancel work
- read final output or thread history
- expose token usage and context window when available
- expose account usage windows when available
- resume or reconnect by backend thread/session ID when available

`CodexAppServerBackend` is the primary implementation because the requirements prefer app-server for thread lifecycle, turns, persisted sessions, status, resume, and event streaming (`Docs/requirements.md:605`).

`CodexCliBackend` is a degraded fallback. It may support direct execution and output capture, but missing capabilities must be explicit in job status/result through `degradedCapabilities`. Unknown context, account usage, and unsupported resume features render as `?` or documented gaps rather than guessed values.

Before full MVP implementation, build the app-server feasibility gate. It must prove start, observe, output retrieval, usage/context, rate-limit windows, and resume/read-prior-thread behavior. If any required feature fails, document the fallback behavior before implementing dependent features.

## Wake, Notifications, And Polling Boundary

Session-bound wake signals are the authoritative asynchronous return path. Optional channel notifications and short polling loops are supporting mechanisms.

Notification rules:

- Files under `.codex-manager/wake-signals/<wakeSessionId>/` are the authoritative terminal wake mechanism.
- Optional channel events are compact diagnostics, not source-of-truth state.
- Payloads include compact identifiers: job ID, title, profile, workflow, state, summary, and statusline.
- Payloads never include raw logs, transcripts, long diffs, secrets, or full prompt bodies.
- Notification attempts and observable delivery/failure are persisted.
- Channel failures never change job status.
- If rewake or channels are unavailable, users recover through `codex_list_jobs`, `codex_status`, and `codex_result`.

Claude-facing `wait=true` status calls must be short. The server caps wait time at 25 seconds; recommended waits use about 20 seconds.

## Usage And Statusline

`UsageReporter` owns normalized statusline fields:

- `contextRemainingPercentEstimate`
- `weeklyUsageRemainingPercent`
- `fiveHourUsageRemainingPercent`
- `statusline`

The canonical display uses remaining percentages, not used percentages:

```text
[codex status: context 78% | weekly 29% | 5h 58%]
```

If a value is unavailable, render `?`. Context remaining is estimate-based when backend accounting or compaction prevents exact values.

## Safety Constraints

The server enforces a narrow control surface (`Docs/requirements.md:589`):

- Reject unknown profiles.
- Reject repo paths outside profile allowlists.
- Reject workflows outside profile allowlists.
- Reject invalid or disallowed model, effort, and fast-mode overrides.
- Reject arbitrary command execution; all Codex invocation goes through backend adapters.
- Keep discovery read-only.
- Avoid exposing secrets from environment, config, logs, prompts, and transcripts.
- Treat destructive operations as unsupported unless a controlled profile explicitly allows them.
- Store enough metadata to audit what Claude requested.
- Always apply the server-owned approval/sandbox bypass policy when launching Codex.

## Implementation Ordering Rationale

The safest implementation order is infrastructure before behavior:

1. Scaffold Generic Host, MCP SDK registration, options, and logging so stdio transport rules are correct from the start.
2. Implement profile/workflow validation before backend dispatch so unsafe requests cannot create side effects.
3. Implement durable stores and job index before long-running backend work so every accepted job is recoverable.
4. Prototype app-server and session-bound wake feasibility gates before depending on those capabilities.
5. Implement the minimal app-server lifecycle: start, status, compact result, and logs.
6. Add queue persistence and cancellation before automatic queue delivery.
7. Add the supervisor once storage and backend observation contracts are stable.
8. Add usage/statusline and paginated full-output retrieval after raw logs and backend usage data exist.
9. Add session-bound terminal wake signals after notification storage and fallback polling are working, then add optional channel notifications.
10. Add CLI fallback only behind `ICodexBackend` and mark degraded capabilities explicitly.

This ordering prevents the highest-risk behaviors, such as background delivery and push notifications, from existing before durable state, locks, backend contracts, and fallback recovery are in place.

## Orchestration Companion Guidance

Future `$orchestrate` plan packs should treat this document as the architectural companion, not as an executable plan. Steps should map to the ownership boundaries above and keep write scopes narrow:

- Tool-layer changes should avoid backend protocol edits unless the step explicitly owns `Backend/`.
- Storage schema changes must include migration or reconstruction behavior for `.codex-manager/`.
- Supervisor changes must state lock ownership and queue-delivery effects.
- Backend work must preserve `ICodexBackend` normalization and report capability gaps.
- Notification work must preserve polling recovery and never make channels or any shared notification pool lifecycle-authoritative.

## Unresolved Design Questions

These questions remain open from the source requirements and should be resolved before or during the relevant feasibility gates, not guessed in implementation:

- Which Codex app-server API endpoints are stable enough for the MVP?
- Where should the server discover global Codex skills and agents on Windows?
- Should repo-local skills and agents be merged with global ones or listed separately?
- Should `codex_get_skill` and `codex_get_agent` be in MVP or deferred until list tools are stable?
- What maximum response size should be enforced for each detail level?
