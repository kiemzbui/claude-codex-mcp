# Requirements: Claude Codex MCP Manager

## Purpose

Build a local MCP server that lets Claude act as a context-efficient manager for Codex work while Codex remains the system that performs coding tasks inside the user's normal Codex environment.

The tool is intentionally an intermediary. It should expose enough stable MCP tools for Claude to discover Codex capabilities, start work, monitor work, relay user input, and return concise results. It should not become a second agent framework, a workflow engine, or an alternative implementation of Codex.

This replaces an opinionated external workflow with a thin, user-owned control surface over the user's existing Codex setup.

## Related Documents

- [proposed_workflow.md](./proposed_workflow.md) defines the intended user workflow, routing rules, profile semantics, Codex skill handoff patterns, and statusline reporting behavior.
- This requirements document is the implementation contract. If the two documents conflict, update both so the product requirements and workflow guidance stay aligned.

## Primary Users

- The human user, who wants to drive Codex from Claude without losing access to existing Codex setup, skills, agents, MCP servers, and repo-specific configuration.
- Claude, acting as a remote-friendly coordinator with limited context budget.
- Codex, acting as the coding worker and source of truth for implementation work.

## Target Architecture

```text
Claude Code
  -> MCP tool call: codex_start_task(...)
    -> local claude-codex-mcp server
      -> Codex app-server or Codex CLI backend
        -> existing Codex config, agents, skills, MCPs, repo checkout
```

Claude should not construct raw Codex commands or choose arbitrary command flags. Claude should call stable MCP tools with a named profile, workflow, and task prompt. The MCP server owns Codex invocation details, profile validation, job persistence, usage/context reporting, and output retrieval.

## Core Principles

1. Preserve Codex as the worker.
   - Claude should ask this MCP server to start, continue, monitor, or cancel Codex work.
   - Claude should not construct raw Codex shell commands when an MCP tool covers the workflow.
   - The MCP server should avoid implementing its own coding-agent behavior.

2. Keep Claude context usage extremely low.
   - Tool responses should be concise by default.
   - Large outputs, logs, transcripts, diffs, and event streams should be persisted locally and summarized.
   - Responses should include stable IDs and short summaries so Claude can request detail only when needed.

3. Discover existing Codex capabilities.
   - The tool should expose available Codex skills.
   - The tool should expose available custom agents.
   - Discovery should be read-only, cacheable, and summarized.

4. Prefer controlled profiles over arbitrary flags.
   - Claude should select a named execution profile.
   - Profiles should encode repo allowlists, permissions, model defaults, task prefixes, and any backend-specific invocation details.
   - The MCP server should own Codex invocation mechanics.

5. Persist durable job state.
   - Claude should be able to reconnect and recover job status without relying on chat history.
   - The server should store job records and logs under a local project-owned state directory.

## Locked Decisions

- Usage remaining is percentage-based.
  - The server should report usage windows as used percentage, remaining percentage, reset time, and window duration.
  - The server should not try to infer exact message counts, token quotas, or plan-specific absolute limits unless Codex directly exposes them.

- Full output access is required.
  - Compact summaries remain the default.
  - Users and Claude must be able to request exact full output when needed.
  - Full output must support pagination or chunking so Claude can inspect details without loading an entire transcript into context.

- Dispatch overrides are required.
  - `model`, `effort`, and `fastMode` are first-class dispatch options for starting and continuing Codex work.
  - Profiles can provide defaults and policy limits, but the MCP tool surface must expose these options.

- Workflow selection is first-class.
  - `workflow` is a required dispatch concept, not just documentation language.
  - Profiles may restrict allowed workflows, but `codex_start_task` must expose the selected workflow explicitly.
  - The server should preserve workflow entrypoints such as `$subagent-manager`, `$prepare-orchestrate-plan`, and `$orchestrate` mechanically instead of reinterpreting them.

- Codex permission prompts are bypassed by policy.
  - The MCP server should always launch Codex with the equivalent of `--dangerously-bypass-approvals-and-sandbox`.
  - This is a server-owned invocation policy, not a dispatch option Claude can choose per request.
  - `waiting_for_input` remains only for genuine Codex clarification prompts or user questions.
  - Permission, sandbox, shell, filesystem, and network approval prompts are out of scope for MVP handling.

- Concurrent jobs are profile-governed.
  - Profiles should include `maxConcurrentJobs`, defaulting to `1`.
  - If a new request arrives while the profile is at its active-job limit, Claude should ask whether to start a new parallel job if policy allows it or queue the prompt for the active job.

- Job titles are required for usability.
  - `codex_start_task` requires a non-empty `title`.
  - Claude should generate and supply a short descriptive title before dispatch.
  - The server should reject missing or blank titles instead of generating them.

- Queued input is server-delivered.
  - `codex_queue_input` persists queued follow-up prompts for an existing job.
  - When the active Codex turn finishes successfully, the server should automatically deliver queued prompts in FIFO order as continuation turns.
  - Claude should not have to remain connected to manually deliver queued prompts.

- Claude Code Channels are the preferred push mechanism.
  - The target environment supports Claude Code Channels and accepts their research-preview status.
  - The MCP server should expose a channel-capable mode and emit `notifications/claude/channel` events for important job state changes.
  - Channel events should wake an active Claude Code session to check job state, not carry full output or transcripts.
  - Short `codex_status wait=true` polling remains required as the fallback when channels are unavailable, disabled, or the Claude session is not active.

- Context reporting is estimate-based.
  - The server should calculate context remaining from backend token usage and model context window when both are available.
  - If compaction or backend behavior makes exact context unavailable, the server should label the result as an estimate.

## In Scope

- Local MCP server for Claude Code.
- Profile listing and profile-based task startup.
- Codex job lifecycle management: start, status, result, send input, cancel.
- Discovery of Codex-visible skills.
- Discovery of Codex-visible custom agents.
- Compact summaries of work status and final results.
- Opt-in full output retrieval for jobs, turns, and agent threads.
- Dispatch-time overrides for Codex model, reasoning effort, and fast mode.
- Account usage and per-thread context reporting when the backend exposes it.
- Local persistence of job metadata and logs.
- Repo path allowlisting.
- Claude Code channel notifications for job state changes when the server is launched with channel support.
- App-server backend first, with room for CLI fallback later.

## Out of Scope

- Replacing Codex's agent system.
- Replacing Codex skills.
- Claude directly editing repo files through this tool.
- A general-purpose shell execution bridge.
- Long-form transcript mirroring into Claude.
- Automatic commit, push, branch deletion, or destructive cleanup unless explicitly added through a controlled profile or confirmation flow.
- Cloud-hosted coordination services.

## Functional Requirements

### Capability Discovery

The server must provide a way to discover the Codex capabilities available in the current environment.

Required tools:

- `codex_list_skills`
  - Returns available Codex skills.
  - Inputs: optional `forceRefresh`.
  - Should include skill name, short description when available, source path or source category when safe, and whether details are cached.
  - Should not return full skill bodies by default.

- `codex_list_agents`
  - Returns available custom Codex agents.
  - Inputs: optional `forceRefresh`.
  - Should include agent name, short description or role when available, and source path or source category when safe.
  - Should not return full agent prompts by default.

Optional detail tools:

- `codex_get_skill`
  - Returns one skill's metadata and optionally its full body.
  - Default response should be metadata only.

- `codex_get_agent`
  - Returns one agent's metadata and optionally its full body.
  - Default response should be metadata only.

Discovery behavior:

- Discovery should search the user's configured Codex locations and repo-local configuration.
- Discovery should tolerate missing directories or partially configured environments.
- Discovery results should be cacheable.
- Discovery responses should include counts and compact lists.
- Cache records should include `createdAt`, `sourcePaths`, and source directory/file mtimes when available.
- Discovery tools should invalidate cached results when known source files or directories change.
- Discovery tools should support `forceRefresh = true` to bypass cache.
- If mtime-based invalidation is not reliable for a source, cached discovery results should have a short TTL, initially 5 minutes.

### Profile Management

Required tool:

- `codex_list_profiles`
  - Returns available controlled execution profiles.
  - Should include profile name, default repo, purpose, permissions summary, and whether the profile is read-only.
  - Should not expose raw command templates unless explicitly requested by a debug mode.

Profile configuration must support:

- `repo`
- `allowedRepos`
- `taskPrefix`
- `backend`
- `readOnly`
- `permissions`
- `defaultWorkflow`
- `allowedWorkflows`
- `maxConcurrentJobs`
- Optional `channelNotifications` policy, defaulting to enabled when channel support is available.
- Optional model default.
- Optional reasoning effort default.
- Optional fast mode or service tier default.

### Dispatch Options

Claude should be able to request controlled runtime options when starting or continuing Codex work. These options must be validated against profile policy before being passed to Codex.

Supported dispatch options:

- `model`
  - The Codex model to use for the task or turn.
  - Should map to Codex's model setting or backend thread/turn model override.

- `effort`
  - The reasoning effort to use for the task or turn.
  - Allowed values should match Codex-supported values: `none`, `minimal`, `low`, `medium`, `high`, `xhigh`.
  - Profiles may restrict the allowed values.

- `fastMode`
  - Boolean convenience option for Codex Fast mode.
  - Should map to Codex service tier selection where supported.
  - When true, use the fast service tier. When false, use the normal/flex service tier unless the profile requires fast mode.

Rules:

- Profile defaults apply when an option is omitted.
- Explicit dispatch options override profile defaults only if the profile allows overrides.
- Invalid models, invalid effort values, or disallowed fast-mode changes must be rejected before dispatch.
- The selected values should be stored in the job record and returned in compact status/result responses.

### Job Lifecycle

Required tools:

- `codex_start_task`
  - Starts a Codex task using a named profile.
  - Inputs: `profile`, `workflow`, `prompt`, `title`, optional `repo`, optional `model`, optional `effort`, optional `fastMode`.
  - Returns: `jobId`, status, selected profile, selected workflow, selected repo, selected model/effort/fast mode, channel notification mode, and Codex thread/session ID when available.
  - Must reject missing or blank titles.
  - Must reject workflows not allowed by the selected profile.

- `codex_status`
  - Returns compact state for a job.
  - Inputs: `jobId`, optional `wait`, optional `timeoutSeconds`.
  - Returns: status, updated time, short progress summary, whether Codex is waiting for input, pending input request if any, and last error if any.
  - If `wait = true`, the tool should return when status changes, new output/input is available, or `timeoutSeconds` elapses.
  - `timeoutSeconds` for `wait = true` should default to 20 and must be capped at 25 by server configuration.
  - Callers should loop with repeated short waits instead of setting long blocking timeouts.

- `codex_result`
  - Returns final result for a job.
  - Inputs: `jobId`, optional detail level.
  - Returns: final summary, changed files if known, tests if known, session ID, and paths to local logs or artifacts.

- `codex_send_input`
  - Sends follow-up user or Claude instructions to an existing Codex job.
  - Inputs: `jobId`, `prompt`, optional `model`, optional `effort`, optional `fastMode`.
  - Returns: accepted/rejected status and updated job state.

- `codex_queue_input`
  - Queues follow-up input for a job after the current active turn finishes.
  - Inputs: `jobId`, `prompt`, optional `title`.
  - Returns: `queueItemId`, queue status, queue position, delivery policy, and updated job state.
  - Queue items must be persisted with status `pending`, `delivered`, `failed`, or `cancelled`.
  - Pending queue items are delivered by the server in FIFO order after the active Codex turn completes successfully.
  - If the active turn fails, is cancelled, or reaches `waiting_for_input`, queued items remain pending until the job can continue or the user cancels them.
  - This supports the user choice "queue the prompt" when a job is already active without requiring Claude to stay connected.

- `codex_cancel_queued_input`
  - Cancels a pending queued input item before the server delivers it.
  - Inputs: `jobId`, `queueItemId`.
  - Returns: cancellation status, updated queue item status, queue position changes when relevant, and updated job state.
  - Must reject cancellation for queue items already `delivered`, `failed`, or `cancelled`.
  - Must not cancel the active Codex job or active Codex turn; use `codex_cancel` for that.

- `codex_cancel`
  - Cancels an active job.
  - Inputs: `jobId`.
  - Returns: cancellation status.

- `codex_list_jobs`
  - Lists known jobs from persisted state.
  - Inputs: optional `status`, optional `profile`, optional `workflow`, optional `repo`, optional `limit`.
  - Returns: compact job summaries including `jobId`, title, profile, workflow, repo, status, created/updated timestamps, Codex thread/session ID when available, and latest statusline fields.
  - Default sort should be newest updated first.

Job states:

- `queued`
- `running`
- `waiting_for_input`
- `completed`
- `failed`
- `cancelled`

Pending input request shape:

```json
{
  "type": "clarification",
  "requestId": "input_...",
  "summary": "Codex needs the target branch name.",
  "message": "Which branch should Codex compare against?",
  "options": []
}
```

Rules:

- `waiting_for_input` must include enough structured data for Claude to ask the human a precise question.
- `waiting_for_input` is for clarification prompts, not permission approvals.
- Because Codex is launched with approval/sandbox bypass enabled, permission prompts should not block jobs.
- The server should persist every pending input request and response in the job log.

### Channel Push, Polling, and Reconnection

Claude must be able to recover and monitor jobs without chat memory.

Requirements:

- `codex_list_jobs` is required for reconnecting to existing jobs after a new Claude session starts.
- The server should maintain a compact job index at `.codex-manager/jobs/index.json`.
- The index should be reconstructable by scanning `.codex-manager/jobs/*.json`.
- `codex_start_task` should return immediately after a job is accepted unless a profile explicitly requests blocking behavior.
- When Claude Code Channels are enabled, the server should push compact job-change events into the active Claude session.
- Channel push is preferred for background monitoring so Claude does not need to actively wait or poll to learn that a job changed state.
- For active async jobs without channel support, Claude-facing clients should poll `codex_status`.
- For "wait on job X", Claude should call `codex_status` with `wait = true` and `timeoutSeconds` no higher than 20-25 seconds, repeating until completion or user interruption.
- Recommended polling interval while the user is waiting and channels are not being used: every 30 seconds.
- Recommended polling interval for background jobs when channels are unavailable: every 60-120 seconds.
- Polling should stop when the job reaches `completed`, `failed`, or `cancelled`.
- Polling should pause and ask the user when the job reaches `waiting_for_input`.
- Polling should avoid retrieving full output unless the user asks for it or a failure requires detail.
- After receiving a channel event, Claude should call `codex_status` or `codex_result` for the referenced job rather than relying on the event payload as the source of truth.

### Claude Code Channel Notifications

The server should support Claude Code Channels as the first-class push path for active Claude Code sessions.

Environment requirements:

- Claude Code version must be at least `2.1.80`; the target environment is `2.1.117`.
- Claude Code must be logged in with `claude.ai`.
- The channel-capable server must be enabled through Claude Code channel configuration, such as `--channels` or the current development-channel mechanism.
- Because channels are research-preview, the implementation must retain polling as a fully supported fallback.

Events to push:

- `job_started`, optional; useful when a job is accepted from another control surface.
- `job_progress`, optional and rate-limited; only for meaningful phase changes.
- `job_waiting_for_input`, required when Codex needs user clarification.
- `job_completed`, required.
- `job_failed`, required.
- `job_cancelled`, required.
- `queue_item_delivered`, optional.
- `queue_item_failed`, required.

Event payload requirements:

```json
{
  "source": "codex-manager",
  "event": "job_completed",
  "jobId": "job_...",
  "title": "AuthCleanup PB1",
  "profile": "orchestration",
  "workflow": "orchestrate_execute",
  "status": "completed",
  "summary": "Codex finished; call codex_result for the compact result.",
  "statusline": "[codex status: context ? | weekly ? | 5h ?]"
}
```

Rules:

- Channel payloads must stay compact.
- Channel payloads must not include raw logs, full output, full transcripts, secrets, or long diffs.
- Channel payloads should include enough identifiers for Claude to call `codex_status`, `codex_result`, or `codex_read_output`.
- The server should persist whether a channel event was attempted, delivered when observable, or failed when observable.
- Channel failures must not change job state.
- If a channel event cannot be delivered, the job remains recoverable through `codex_list_jobs`.
- Channel support may be implemented in the same MCP server process as the tools or in a companion channel server, but both must share the same `.codex-manager/` state.

### Concurrent Job Handling

The server and Claude should make active-job conflicts explicit.

Requirements:

- Each profile should have `maxConcurrentJobs`; default is `1`.
- `codex_start_task` should reject or return a policy conflict when starting a new job would exceed the selected profile's limit.
- When a user issues a new request and a relevant job is already active, Claude should ask the user to choose between:
  - Spin up a new job, if the profile allows another concurrent job.
  - Queue the prompt for the active job.
- Queued prompts should be persisted and server-delivered so they survive Claude context loss.
- `codex_status` and `codex_list_jobs` should expose compact queue counts, including pending and failed queued items.
- `codex_list_jobs` should make multiple active jobs easy to distinguish by title, job ID, profile, workflow, status, and updated time.
- Claude can monitor multiple active jobs by calling `codex_status` for each job sequentially.

### Context-Efficient Responses

Every MCP response should be designed for low Claude context usage.

Default response requirements:

- Return IDs, state, counts, and concise summaries.
- Avoid returning raw logs unless explicitly requested.
- Avoid returning full transcripts unless explicitly requested.
- Truncate long text fields and indicate truncation.
- Include local artifact paths when detail is available outside the response.
- Include a compact statusline whenever Claude reports Codex progress, results, or follow-up output to the user.

Claude-facing statusline requirements:

- The statusline should appear before or after the main content consistently, separated from the actual response body.
- The statusline must include context remaining, weekly usage remaining, and 5h usage remaining as percentages whenever those values are available.
- If a value is unavailable, show `?` for that value instead of omitting the field.
- Use a stable one-line format so the user can visually scan and ignore it.

Recommended statusline format:

```text
[codex status: context 78% | weekly 29% | 5h 58%]
```

Rules:

- `context` means estimated context remaining for the relevant Codex job/thread.
- `weekly` means long-window usage remaining percentage.
- `5h` means short-window usage remaining percentage.
- Percentages should be remaining percentages, not used percentages.
- The main report content should stay separate from the statusline.
- The statusline should be included on `codex_status`, `codex_result`, `codex_send_input`, and `codex_read_output` driven reports when Claude presents them to the user.

Detail controls:

- Tools that can return large output should accept a detail parameter such as `summary`, `normal`, or `full`.
- `summary` should be the default.
- `full` should be opt-in and should still enforce max-size safeguards.

Full output requirements:

- `codex_result` with `detail = "full"` should return the full final agent output when it fits within configured MCP response limits.
- If the full output exceeds the response limit, the tool should return a clear truncation marker plus a local artifact reference.
- The server should persist raw Codex event/output logs locally so full output can be retrieved without requiring Claude to keep it in chat context.
- A separate `codex_read_output` tool is required for paginated full-output retrieval.

Required full-output tool:

- `codex_read_output`
  - Inputs: `jobId`, optional `threadId`, optional `turnId`, optional `agentId`, optional `offset`, optional `limit`, optional `format`.
  - Returns: raw or normalized output chunks from local logs or backend thread history.
  - Must support pagination so Claude can inspect exact output without pulling the entire transcript into context.

Recommended summary shape:

```json
{
  "jobId": "job_...",
  "status": "running",
  "summary": "Codex is inspecting the repo and has not edited files yet.",
  "waitingForInput": false,
  "changedFileCount": 0,
  "logRef": ".codex-manager/logs/job_....jsonl"
}
```

### Persistence

The server must persist job state locally.

Suggested layout:

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

Each job record should include:

- `jobId`
- `createdAt`
- `updatedAt`
- `profile`
- `workflow`
- `repo`
- `title`
- `status`
- `promptSummary`
- `codexThreadId`
- `model`
- `effort`
- `fastMode`
- `serviceTier`
- `waitingForInput`
- `resultSummary`
- `changedFiles`
- `testSummary`
- `lastError`
- `logPath`
- `inputQueue` summary and persisted queue item references
- `notificationMode`, such as `channel`, `polling`, or `disabled`
- `notificationLogPath`

The full original prompt may be stored locally, but MCP responses should avoid echoing it back unless requested.

Queue persistence requirements:

- Queue items should be stored in `.codex-manager/queues/<job-id>.json`.
- The job record's `inputQueue` should be a compact summary and reference, not the full queued prompt bodies.
- Queue items should be ordered by `createdAt` and delivered FIFO by the background supervisor.
- Each queue item should include:

```json
{
  "queueItemId": "queue_...",
  "jobId": "job_...",
  "createdAt": "2026-04-22T23:00:00.000Z",
  "updatedAt": "2026-04-22T23:00:00.000Z",
  "status": "pending",
  "title": "Follow-up cleanup",
  "promptSummary": "Run the focused cleanup test after the active turn finishes.",
  "promptRef": ".codex-manager/queues/job_....json#queue_...",
  "deliveryAttemptCount": 0,
  "deliveredAt": null,
  "cancelledAt": null,
  "lastError": null
}
```

- Full queued prompts may be stored in the queue file, but MCP responses should avoid echoing them unless explicitly requested.
- `inputQueue` in the job record should include `pendingCount`, `deliveredCount`, `failedCount`, `cancelledCount`, `nextQueueItemId`, and `queuePath`.

Title requirements:

- Every job record must have a non-empty `title`.
- Claude must provide a short descriptive title at dispatch time.
- The server must reject `codex_start_task` calls that omit `title` or provide a blank title.
- Titles should be included in `codex_status`, `codex_result`, and `codex_list_jobs`.

### Usage and Context Reporting

The server should expose account usage and per-thread context when the selected Codex backend makes that information available.

Required tool:

- `codex_usage`
  - Returns compact usage and context information.
  - Inputs: optional `jobId`, optional `threadId`.
  - Returns: account usage windows, reset times, context window, token usage, and estimated context remaining when available.
  - Should return enough normalized fields for Claude to render the standard statusline without parsing raw usage windows.

Account usage requirements:

- Report the short-window and long-window usage buckets exposed by Codex.
- Label windows by duration when possible, such as `5h` and `weekly`.
- Include used percentage, remaining percentage, and reset time when available.
- Treat percentage/reset-window reporting as the canonical usage view.
- Do not infer exact quotas or message counts from percentages.
- If exact quotas or message counts are exposed later, they may be included as optional supplemental fields but should not replace the percentage view.
- Include plan type and limit name/id when available.

Context requirements:

- Track token usage updates for active Codex threads.
- Store last-turn token usage, total token usage, and model context window when available.
- Compute estimated context remaining as `modelContextWindow - totalTokens` when both values are present.
- If Codex compaction or backend accounting makes exact remaining context unavailable, report the value as an estimate.
- For spawned agent threads, report per-agent context only when the backend exposes distinct thread or agent token usage.

Normalized statusline fields:

- `contextRemainingPercentEstimate`
- `weeklyUsageRemainingPercent`
- `fiveHourUsageRemainingPercent`
- `statusline`

The server may return a preformatted `statusline` string. If it does, Claude should use it directly unless the user has configured a different display format.

### Safety

The server must enforce a narrow control surface.

Requirements:

- Restrict repo paths to configured allowlists.
- Reject arbitrary command execution requests.
- Reject unknown profiles.
- Avoid exposing secrets from environment, config, logs, or prompts.
- Treat destructive operations as unsupported unless a profile explicitly allows them.
- Prefer read-only discovery for skills, agents, and profiles.
- Record enough job metadata to audit what Claude requested.
- Always launch Codex with the configured approval/sandbox bypass policy.
- Do not expose approval/sandbox bypass as a Claude-controlled per-dispatch flag.

### Backend

Primary backend:

- Codex app-server, if practical.

Reasons:

- Codex app-server is designed for rich Codex clients.
- It supports thread lifecycle, turns, persisted sessions, status, resume, and event streaming.
- It is less brittle than one-off `codex exec` subprocess calls for long-running, resumable work.

Fallback backend:

- Codex CLI, only where app-server is unavailable or lacks required coverage.

Backend requirements:

- Hide backend-specific command or protocol details behind MCP tools.
- Normalize job lifecycle states across backends.
- Preserve Codex thread or session IDs when available.
- Store backend events in local logs rather than sending all events to Claude.
- Report backend capability gaps explicitly in job status/results.

Backend feasibility gate:

- Before implementing the full MVP, prototype one real `codex_start_task`-equivalent call against the installed Codex app-server.
- The prototype must verify whether the backend can start a thread/turn, stream or poll status, read final output, expose token usage/context window, expose account rate-limit windows, and resume or read prior thread state.
- If app-server cannot support a required feature, document the fallback behavior before implementing that feature.

Channel feasibility gate:

- Before implementing full channel notification support, prototype one minimal Claude Code channel notification in the target environment.
- The prototype must verify the server can register or declare the channel, emit a `notifications/claude/channel` event, and cause an active Claude Code session to receive a compact event payload.
- If channel delivery cannot be verified, keep channel support disabled by default and rely on short polling until the channel configuration is fixed.

Background supervisor:

- The server must include a background supervisor or event watcher that runs independently of individual MCP tool calls.
- The supervisor is responsible for observing active Codex jobs, updating persisted job state, delivering pending queue items when an active turn completes successfully, and emitting channel notifications for important state changes.
- The supervisor must resume from `.codex-manager/` state on server startup so queued inputs and active jobs are not stranded after a restart.
- The supervisor should prefer backend event streams when available and fall back to bounded polling when necessary.
- The default supervisor polling interval for active jobs should be 10-15 seconds, distinct from Claude-facing polling guidance.
- The supervisor must use per-job locking or equivalent concurrency control so queue delivery, cancellation, and status updates do not race each other.

Stale backend recovery:

- On startup, the supervisor should attempt to reconnect active persisted jobs to their stored `codexThreadId` or backend session ID.
- If the backend confirms the thread/session is still valid, the supervisor should resume normal observation.
- If the backend cannot be reached, the supervisor should leave the job active but mark its status summary as temporarily degraded and retry with backoff.
- If the backend is reachable but the stored thread/session is unrecoverable, the supervisor should mark the job `failed` with a recoverable error code such as `backend_thread_unrecoverable`.
- Unrecoverable-thread failures should preserve the original prompt summary, log paths, queue state, and backend identifiers so the user can decide whether to retry or start a replacement job.

CLI fallback degraded mode:

- CLI fallback may support direct task execution, final output capture, and changed-file/test summaries.
- CLI fallback may not support live context percentage, percentage-based account usage, clarification prompts, or reliable thread resume.
- In degraded mode, statusline fields that cannot be computed must render as `?`.
- The server should include a `degradedCapabilities` list in job status/result responses when using CLI fallback.

## Non-Functional Requirements

### Implementation Platform

- The server should be implemented in C# on .NET 10.
- Project files should target `net10.0`.
- The primary process should be a .NET Generic Host console application using the official C# MCP SDK.
- Expected baseline packages include the official `ModelContextProtocol` SDK package and `Microsoft.Extensions.Hosting`.
- The primary MCP transport should be stdio for Claude Code local integration.
- Logging must write to stderr or structured log files, not stdout, because stdout is reserved for MCP protocol traffic in stdio mode.
- Background work, including active job observation, queued-input delivery, and notification emission, should be implemented with hosted services such as `BackgroundService` / `IHostedService`.
- Use ASP.NET Core only if a future backend or channel integration requires an HTTP endpoint; it is not required for the MVP tool surface.

### Context Budget

- Default responses should target a few hundred words or less.
- Discovery responses should summarize by name and description, not include full prompt bodies.
- Final results should prioritize outcome, changed files, tests, and next action.
- Large artifacts should be referenced by path.

### Reliability

- Job state should survive MCP server restarts.
- Failed jobs should preserve last error and relevant log references.
- Discovery should degrade gracefully if Codex config paths are missing.

### Portability

- The first target environment is local Windows development.
- Paths should be handled with platform-safe APIs.
- The design should not prevent later macOS or Linux support.

### Maintainability

- Keep the .NET implementation modular.
- Separate MCP tool definitions, profile loading, job storage, discovery, and Codex backend logic.
- Prefer small, testable functions for parsing and persistence.

Suggested project structure:

```text
claude-codex-mcp/
  requirements.md
  proposed_workflow.md
  codex-manager.json
  ClaudeCodexMcp.sln
  src/
    ClaudeCodexMcp/
      ClaudeCodexMcp.csproj
      Program.cs
      Tools/
        CodexTools.cs
        DiscoveryTools.cs
        ProfileTools.cs
      Configuration/
        ManagerOptions.cs
        ProfileOptions.cs
      Workflows/
        WorkflowRegistry.cs
      Discovery/
        CodexCapabilityDiscovery.cs
      Storage/
        JobStore.cs
        OutputStore.cs
        QueueStore.cs
        NotificationStore.cs
      Supervisor/
        CodexJobSupervisor.cs
      Notifications/
        NotificationDispatcher.cs
        ClaudeChannelNotifier.cs
      Backend/
        ICodexBackend.cs
        CodexAppServerBackend.cs
        CodexCliBackend.cs
      Usage/
        UsageReporter.cs
  tests/
    ClaudeCodexMcp.Tests/
      ClaudeCodexMcp.Tests.csproj
```

## MVP Acceptance Criteria

1. Claude can list configured profiles with `codex_list_profiles`.
2. Claude can list available Codex skills with `codex_list_skills` without receiving full skill bodies by default.
3. Claude can list available custom agents with `codex_list_agents` without receiving full agent prompts by default.
4. Claude can start a Codex job through `codex_start_task` using a named profile.
5. Claude can check job state through `codex_status`.
6. Claude can fetch a compact final result through `codex_result`.
7. Claude can send follow-up input through `codex_send_input`.
8. Claude can cancel an active job through `codex_cancel`.
9. Job state and logs persist under `.codex-manager/`.
10. The server rejects unknown profiles and repo paths outside the allowlist.
11. Claude can request a full output view through `codex_result detail="full"` or paginated `codex_read_output`.
12. Claude can dispatch Codex with profile-allowed `model`, `effort`, and `fastMode` overrides.
13. Claude can read usage/context through `codex_usage` when the backend exposes rate-limit and token-usage data.
14. Claude reports Codex progress/results with a stable statusline containing context, weekly usage, and 5h usage remaining percentages.
15. Claude can dispatch Codex with an explicit `workflow` and the server rejects workflows outside the selected profile's allowlist.
16. Claude can recover existing jobs through `codex_list_jobs` after losing chat context.
17. Claude can surface and respond to Codex clarification prompts through structured `waiting_for_input` status and `codex_send_input`.
18. The server launches Codex with approval/sandbox bypass enabled by policy.
19. Profiles enforce `maxConcurrentJobs`, defaulting to `1`.
20. Claude can queue input for an active job with `codex_queue_input`, and the server automatically delivers queued input after the active turn completes successfully.
21. Claude can cancel a pending queued input item through `codex_cancel_queued_input`.
22. The background supervisor delivers queued input, tracks active jobs, and emits notifications without requiring an MCP tool call to be in flight.
23. Claude can wait on a job using repeated short `codex_status wait=true` calls capped at 20-25 seconds each.
24. Every job has a useful title supplied by Claude, and the server rejects missing or blank titles.
25. When Claude Code Channels are enabled, the server pushes compact channel notifications for completed, failed, cancelled, waiting-for-input, and queue-delivery-failure job events.
26. If channel delivery is unavailable or fails, Claude can still recover and monitor every job through `codex_list_jobs`, `codex_status`, and `codex_result`.

## First Implementation Plan

1. Scaffold a .NET 10 C# MCP server using the official C# MCP SDK and `Microsoft.Extensions.Hosting`.
2. Add `codex-manager.json` profile loading.
3. Add profile validation for repo allowlists, read-only mode, workflow allowlists, model overrides, effort overrides, fast-mode policy, and `maxConcurrentJobs`.
4. Generate or vendor Codex app-server protocol bindings from the installed Codex app-server schema.
5. Prototype the Codex app-server feasibility gate: start a thread, start a turn, observe status, capture output, read token usage, read account rate limits, and recover a prior thread.
6. Prototype the Claude Code channel feasibility gate: declare/register a channel, emit one compact notification, and verify an active Claude Code session receives it.
7. Document any degraded capabilities before continuing implementation.
8. Add a local JSON job store under `.codex-manager/jobs/`.
9. Add `.codex-manager/jobs/index.json` and `codex_list_jobs`.
10. Add raw event/output logging under `.codex-manager/logs/`.
11. Add queue persistence under `.codex-manager/queues/`.
12. Implement `codex_list_profiles`.
13. Implement read-only capability discovery for `codex_list_skills` and `codex_list_agents`, including cache invalidation and `forceRefresh`.
14. Implement a minimal Codex app-server client.
15. Implement `codex_start_task`, `codex_status`, and `codex_result`.
16. Add explicit `workflow` handling.
17. Add dispatch option handling for `model`, `effort`, and `fastMode`.
18. Add `codex_send_input`, `codex_queue_input`, `codex_cancel_queued_input`, and `codex_cancel`.
19. Add the background supervisor for active job observation, queued-input delivery, and notification emission.
20. Add `codex_usage` with percentage-based 5h/weekly usage and context estimates.
21. Add `codex_read_output` with paginated full-output retrieval.
22. Add statusline normalization so Codex-backed reports can render `[codex status: context ? | weekly ? | 5h ?]`.
23. Add Claude Code channel notification support for compact job state events.
24. Register the MCP server with Claude Code as both a tool server and channel-capable server as needed by Claude Code's channel configuration.
25. Smoke test with read-only discovery:

```text
Using the investigation profile, list the Codex custom agents and skills visible in this environment. Do not edit files.
```

26. Smoke test direct execution:

```text
Using the investigation profile, explain this repo's current docs and do not edit files.
```

27. Smoke test workflow routing with a no-op or read-only `$subagent-manager` prompt.
28. Smoke test job recovery by starting a job, restarting Claude/MCP, and finding it through `codex_list_jobs`.
29. Smoke test `waiting_for_input` handling with a controlled clarification prompt or backend fixture.
30. Smoke test channel notification delivery for completed, failed, and waiting-for-input job states.
31. Smoke test queued-input delivery by queuing a prompt, letting the active turn finish, and verifying the supervisor delivers the queued prompt.
32. Smoke test `codex_cancel_queued_input` by cancelling a pending queue item before delivery.
33. Smoke test concurrent-job policy by starting a profile-limited job and attempting a second dispatch.
34. Smoke test repeated short `codex_status wait=true` calls using `timeoutSeconds = 20`.

## Open Questions

- Which Codex app-server API endpoints are stable enough for the MVP?
- Where should this server discover global Codex skills and agents on Windows?
- Should repo-local skills and agents be merged with global ones or listed separately?
- Should `codex_get_skill` and `codex_get_agent` exist in the MVP, or wait until the list tools are working?
- What maximum response size should be enforced for each detail level?

## Deferred

- Telegram notification support is deferred. The user already has a custom Telegram notification path, and Claude Code channel support plus the native Telegram channel/plugin ecosystem are the preferred notification paths for now.
