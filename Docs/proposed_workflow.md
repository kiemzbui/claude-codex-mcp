# Proposed Workflow

## Operating Model

Claude should act as the router and monitor. Codex should remain the worker.

The MCP server should not replace the user's existing Codex workflow. It should expose that workflow through stable, context-efficient tools so Claude can start the right kind of Codex session, monitor it, pass follow-up input, and retrieve compact or full output when needed.

Core split:

- Claude routes the request.
- The MCP server validates profiles, starts Codex, stores state, tracks usage/context, and returns compact summaries.
- Codex executes the selected workflow using the user's existing skills, custom agents, MCP servers, and repo configuration.

## Profiles

Profiles define execution policy, not detailed task behavior. Claude should choose a profile and optional workflow, not construct raw Codex commands.

`workflow` is a first-class dispatch field. It should not be hidden inside the prompt or inferred only from the selected profile.

Profiles also define concurrency policy through `maxConcurrentJobs`, defaulting to `1`.

### `implementation`

Use when Codex is allowed to edit code, tests, docs, and configuration in the target repo.

Expected use:

- Small fixes.
- Normal implementation work.
- Test updates.
- Follow-up changes after a plan has been approved.

Default posture:

- Code edits allowed.
- Tests should be run when relevant.
- Codex may use skills and custom agents when appropriate.

### `investigation`

Use when the user wants answers but does not want code files edited.

Expected use:

- Read-only bug investigation.
- Architecture questions.
- "Find out why this happens" requests.
- Repo reconnaissance before deciding whether to implement.
- Checking whether a change is feasible.

Default posture:

- Read-only.
- No source, test, config, generated, or documentation file edits unless the user explicitly changes profile or gives a separate edit request.
- Shell commands should be limited to inspection and diagnostics.
- The final answer should lead with confirmed facts, evidence, and remaining uncertainty.

Example dispatch:

```json
{
  "profile": "investigation",
  "workflow": "direct",
  "title": "Session Expiration Flow",
  "prompt": "Find where auth session expiration is enforced and explain the flow. Do not edit files.",
  "effort": "medium",
  "fastMode": true
}
```

### `planning`

Use when Codex should create or revise an orchestration-ready plan pack.

Expected use:

- Drafting `plan.md`.
- Updating `progress.md`.
- Writing or revising `architecture_design.md`.
- Writing or revising `execution_recs.md`.

Default posture:

- Edits are limited to planning documents unless the user asks otherwise.
- Plans should be shaped for later `$orchestrate` execution.
- Ambiguity should be recorded as unresolved questions instead of encoded as executable work.

### `orchestration`

Use when Codex should launch or continue `$orchestrate`.

Expected use:

- Executing an existing plan.
- Revising an existing plan.
- Running a specific parallel batch.
- Continuing an orchestrator-owned plan workflow.

Default posture:

- Treat `$orchestrate` as the owner of the work after handoff.
- The parent Codex session should remain lightweight.
- Claude should monitor status and retrieve summaries unless the user asks for full output.

### `review`

Use when Codex should review only.

Expected use:

- Code review.
- Plan review.
- Risk review.
- Test gap review.

Default posture:

- No edits.
- Findings should lead.
- Prioritize correctness, security, behavior regressions, and missing tests.

## Workflows

Workflows define the Codex-side operating style for a profile. They should preserve the user's existing skill semantics instead of reimplementing them in Claude or the MCP server.

Supported workflows:

- `direct`
- `subagent_manager`
- `prepare_orchestrate_plan`
- `managed_plan`
- `orchestrate_execute`
- `orchestrate_revise`

The MCP server should reject a workflow if the selected profile does not allow it.

## Workflow Selection

### Small Tasks: `direct`

Use `direct` when the task is small enough for one normal Codex session.

Examples:

- "Fix this failing test."
- "Find the file that owns this behavior."
- "Update this helper to handle null input."
- "Explain how this subsystem works."

Implementation example:

```json
{
  "profile": "implementation",
  "workflow": "direct",
  "title": "Fix Token Cleanup Test",
  "prompt": "Fix the failing token cleanup test and run the focused test.",
  "model": "gpt-5.4",
  "effort": "medium",
  "fastMode": true
}
```

Investigation example:

```json
{
  "profile": "investigation",
  "workflow": "direct",
  "title": "Investigate Token Cleanup Skips",
  "prompt": "Investigate why token cleanup sometimes skips expired sessions. Do not edit files.",
  "model": "gpt-5.4",
  "effort": "high",
  "fastMode": false
}
```

### High-Context Investigation: `subagent_manager`

Use `subagent_manager` when the user explicitly asks for `$subagent-manager`, when the task is a broad bug trace, or when preserving the parent session's context is more important than direct work.

Claude should not spawn subagents itself. It should ask Codex to invoke the existing `$subagent-manager` workflow.

Example:

```json
{
  "profile": "investigation",
  "workflow": "subagent_manager",
  "title": "Managed Token Cleanup Investigation",
  "prompt": "$subagent-manager Investigate why token cleanup sometimes skips expired sessions. Do not edit files. Dispatch focused read-only subagents and synthesize evidence.",
  "model": "gpt-5.4",
  "effort": "high",
  "fastMode": false
}
```

Rules:

- Preserve `$subagent-manager` as the workflow entrypoint.
- Keep the prompt focused on the task, evidence required, and edit restrictions.
- Let Codex own subagent dispatch and synthesis.
- Claude should monitor the parent Codex job, not child agents directly unless Codex exposes them cleanly.

### Plan Generation: `prepare_orchestrate_plan`

Use `prepare_orchestrate_plan` when Codex should create or revise a plan pack for later orchestration.

Example:

```json
{
  "profile": "planning",
  "workflow": "prepare_orchestrate_plan",
  "title": "Prepare AuthCleanup Plan",
  "prompt": "$prepare-orchestrate-plan create a plan pack for Docs/WorkItems/AuthCleanup",
  "model": "gpt-5.4",
  "effort": "high",
  "fastMode": false
}
```

Rules:

- Preserve `$prepare-orchestrate-plan` as the workflow entrypoint.
- The output should be a plan pack that `$orchestrate` can execute with minimal reinterpretation.
- Plan docs should carry enough context that later execution does not depend on Claude chat history.

### Managed Plan Generation: `managed_plan`

Use `managed_plan` when the plan requires substantial repo discovery or parallel investigation before authoring.

This mirrors the user's existing habit of using `$subagent-manager` together with `$prepare-orchestrate-plan`.

Example:

```json
{
  "profile": "planning",
  "workflow": "managed_plan",
  "title": "Managed AuthCleanup Plan",
  "prompt": "$subagent-manager Use $prepare-orchestrate-plan to prepare Docs/WorkItems/AuthCleanup. Dispatch investigation subagents where needed, then synthesize an orchestration-ready plan pack.",
  "model": "gpt-5.4",
  "effort": "high",
  "fastMode": false
}
```

Rules:

- Preserve both skill entrypoints in the prompt.
- Codex should manage discovery through subagents.
- Codex should write the final plan pack through the plan-preparation workflow.
- Claude should receive compact progress updates and final changed planning files.

### Plan Execution: `orchestrate_execute`

Use `orchestrate_execute` when a plan pack already exists and should be executed.

Example:

```json
{
  "profile": "orchestration",
  "workflow": "orchestrate_execute",
  "title": "Execute AuthCleanup Plan",
  "prompt": "$orchestrate execute Docs/WorkItems/AuthCleanup",
  "model": "gpt-5.4",
  "effort": "medium",
  "fastMode": false
}
```

Batch example:

```json
{
  "profile": "orchestration",
  "workflow": "orchestrate_execute",
  "title": "Execute AuthCleanup PB1",
  "prompt": "$orchestrate execute Docs/WorkItems/AuthCleanup batch PB1",
  "model": "gpt-5.4",
  "effort": "medium",
  "fastMode": true
}
```

Rules:

- Preserve the `$orchestrate execute ...` command mechanically.
- Do not have Claude reinterpret the plan or manually assign plan steps.
- Treat orchestration as asynchronous by default.
- Claude should report the Codex job ID, current status, usage/context percentages, and final summary.

### Plan Revision: `orchestrate_revise`

Use `orchestrate_revise` when an existing plan pack needs revision before execution continues.

Example:

```json
{
  "profile": "orchestration",
  "workflow": "orchestrate_revise",
  "title": "Revise AuthCleanup Plan",
  "prompt": "$orchestrate revise Docs/WorkItems/AuthCleanup",
  "model": "gpt-5.4",
  "effort": "medium",
  "fastMode": false
}
```

Rules:

- Preserve the `$orchestrate revise ...` command mechanically.
- The orchestrator owns the revision workflow.
- Claude should avoid injecting extra interpretation unless the user explicitly supplies it as revision guidance.

## Claude Routing Rules

Claude should choose the smallest workflow that matches the user request.

Use `direct` when:

- The task is narrow.
- The user asks for normal implementation or investigation.
- No explicit manager or orchestrator skill is requested.

Use `investigation` profile with `direct` when:

- The user asks for answers only.
- The user says "read only", "do not edit", "investigate", "explain", or "find out".
- The expected result is evidence and explanation rather than changed files.

Use `subagent_manager` when:

- The user explicitly invokes `$subagent-manager`.
- The task is a broad bug trace and the user wants Codex to manage subagents.
- The user says context is low and wants to keep going through subagents.

Use `prepare_orchestrate_plan` when:

- The user asks to create or revise an orchestration-ready plan pack.
- The expected output is planning docs.

Use `managed_plan` when:

- The user asks for `$subagent-manager` and `$prepare-orchestrate-plan` together.
- Plan authoring requires substantial discovery before the plan can be written.

Use `orchestrate_execute` when:

- The user asks to execute an existing plan.
- The user invokes `$orchestrate execute`.
- The user names a plan and a batch to run.

Use `orchestrate_revise` when:

- The user asks to revise an existing plan.
- The user invokes `$orchestrate revise`.

## Context Efficiency Rules

Default MCP responses should stay compact.

Claude should ask for:

- `codex_status` for progress.
- `codex_usage` for percentage-based 5h/weekly usage and context estimates.
- `codex_result` for compact final output.
- `codex_read_output` only when the user asks for exact full output or when debugging a failed Codex run requires transcript detail.

The server should store raw logs and full output locally, then return references and chunks on demand.

## Reconnection, Channels, And Polling

Claude should assume Codex jobs are asynchronous unless a profile explicitly says otherwise.

When Claude starts a job:

1. Call `codex_start_task`.
2. Report the returned title, `jobId`, profile, workflow, and statusline.
3. If Claude Code Channels are enabled, tell the user that Claude will receive a channel event when the job changes state.
4. If the user is actively waiting, call `codex_status` with short `wait=true` loops or poll normally.
5. If the job is backgrounded and channels are enabled, do not keep polling just to detect completion.
6. Stop active polling when the job reaches `completed`, `failed`, or `cancelled`.
7. Pause and ask the user when the job reaches `waiting_for_input`.

Recommended polling cadence:

- User is waiting in the conversation and channels are not enough: poll about every 30 seconds, or use short `wait=true` calls.
- Background monitoring with channels enabled: rely on channel events.
- Background monitoring when channels are unavailable: poll every 60-120 seconds.
- Do not retrieve full output while polling unless the user asks for exact output.

When Claude receives a channel event:

1. Read the event's `jobId`, `title`, state, and short summary.
2. Call `codex_status` for running, cancelled, failed, or `waiting_for_input` states.
3. Call `codex_result` for completed jobs unless the user only needs the status.
4. Ask the user directly if the job is `waiting_for_input`.
5. Keep the visible report compact and include the Codex statusline.

Channel events are wake-up signals, not the source of truth. Claude should always fetch the current job state before reporting detailed results.

If the user says "wait on job X":

1. Call `codex_status` with `wait = true` and `timeoutSeconds = 20`.
2. Repeat with short wait calls until the job completes, fails, is cancelled, reaches `waiting_for_input`, or the user interrupts.
3. Keep each returned report compact and include the statusline.

Do not request long blocking waits. The server caps `wait=true` calls at 25 seconds so MCP transports have room to return cleanly.

Short waits remain the fallback even when channels are enabled, because channels only notify active Claude Code sessions and may be unavailable in non-channel launches.

When Claude starts in a new chat or after losing context:

1. Call `codex_list_jobs` if the user asks about ongoing or recent Codex work.
2. Prefer newest updated active jobs first.
3. Ask the user which job to rejoin only if multiple active jobs match the request.

## Clarification Prompts

Codex is launched with approval/sandbox bypass enabled by server policy, so permission prompts should not block jobs.

If Codex needs genuine user clarification, the MCP server should surface that as `waiting_for_input`.

Claude should handle it as follows:

1. Read the structured pending input request from `codex_status`.
2. Ask the user a direct question using the request summary and options.
3. Use `codex_send_input` for the clarification answer.
4. Resume monitoring after the MCP server accepts the response, using channels when enabled or polling as the fallback.

Claude should not expose approval/sandbox bypass as a per-dispatch choice. It is part of the MCP server's Codex invocation policy.

## Concurrent Jobs

When a user issues a new request and a relevant job is already active, Claude should make the choice explicit.

Ask the user to choose between:

- Spin up a new job, if the profile's `maxConcurrentJobs` allows another active job.
- Queue the prompt for the active job.

If the user chooses a new job:

1. Generate or provide a short title.
2. Call `codex_start_task`.
3. Report the title, job ID, profile, workflow, and statusline.

If the user chooses to queue:

1. Call `codex_queue_input`.
2. Report the target job title, job ID, queue item ID, and queue position.
3. Continue monitoring the active job normally.

Queued input is server-delivered. When the active Codex turn completes successfully, the MCP server automatically sends queued prompts in FIFO order as continuation turns. Claude does not need to remain connected to replay the queued prompt manually. If the active turn fails, is cancelled, or reaches `waiting_for_input`, queued prompts remain pending until the job can continue or the user cancels them.

If the user wants to remove a queued prompt before delivery:

1. Call `codex_cancel_queued_input` with the job ID and queue item ID.
2. Report whether the queue item was cancelled and whether later queue positions changed.
3. Do not cancel the active Codex job unless the user explicitly asks to cancel the job itself.

Claude can monitor multiple active jobs by calling `codex_status` for each job sequentially. Reports should always include the job title so active jobs are easy to distinguish.

With channels enabled, Claude does not need to poll every active job just to discover completion. The server should send compact channel events for completed, failed, cancelled, waiting-for-input, and queue-delivery-failure states, and Claude should fetch details for only the affected job.

## Job Titles

Every job should have a short descriptive title.

Claude must provide a title when dispatching, especially when there may be multiple active jobs. If Claude omits one or sends a blank title, the MCP server should reject the request.

Example status phrasing:

```text
[codex status: context 78% | weekly 29% | 5h 58%]

AuthCleanup (job_abc): running, batch PB2 in progress.
```

## Statusline Reporting

Whenever Claude reports back from Codex, it should include a stable, visually separate statusline.

Required format:

```text
[codex status: context 78% | weekly 29% | 5h 58%]
```

Rules:

- Show remaining percentages, not used percentages.
- `context` is the estimated context remaining for the relevant Codex job or thread.
- `weekly` is weekly usage remaining.
- `5h` is short-window usage remaining.
- If a value is unavailable, show `?`.
- Keep the statusline separate from the actual content so the user can quickly scan or ignore it.

Example:

```text
[codex status: context 78% | weekly 29% | 5h 58%]

Codex finished the investigation. The expiration path is enforced in `SessionExpiryService`, and the skipped cleanup path appears tied to the background worker's stale cursor.
```

## Expected User Experience

The user should be able to keep using natural language:

- "Have Codex fix this."
- "Investigate this read-only."
- "Use `$subagent-manager`."
- "Prepare an orchestrate plan for this."
- "Run `$orchestrate execute Docs/WorkItems/AuthCleanup batch PB1`."
- "How much usage do I have left?"
- "Show me the full Codex output."

Claude translates that into profile and workflow selections. Codex performs the work. The MCP server keeps the interaction durable, inspectable, and context-efficient.

## Design Rule

Claude routes. MCP records and monitors. Codex executes.
