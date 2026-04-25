# Claude Codex MCP

Claude is the portal. Codex is the worker. The MCP server is the durable control surface between them.

## Current Wake-Up Contract

The authoritative wake path is session-bound, not channel-bound.

- The Claude session that calls `codex_start_task` is the wake owner for that job.
- The MCP layer must capture that caller session id at task start and persist it as `WakeSessionId` on the job record and job index.
- The active design relies on the Stop hook updating `.codex-manager/current-session-id.txt`. Earlier per-process session-binding attempts were unreliable and proved unworkable in practice.
- When the job reaches a terminal state, the supervisor writes a terminal signal file at `.codex-manager/wake-signals/<wakeSessionId>/<jobId>.json`.
- The Claude Stop hook for that same session watches only `.codex-manager/wake-signals/<wakeSessionId>/`, consumes the file, emits a short status message, and exits `2` to rewake Claude in the same session.
- Optional `notifications/claude/channel` events may still be emitted, but they are diagnostics only. They are not the authoritative wake path.

## Claude -> Server -> Codex Boundary

1. Claude session `S` calls `codex_start_task`.
2. The MCP server validates policy, creates the durable job record, and persists `WakeSessionId = S`.
3. Codex executes the work asynchronously.
4. On the first terminal transition, the supervisor persists terminal state and writes `.codex-manager/wake-signals/S/<jobId>.json`.
5. The Stop hook for session `S` consumes that file and rewakes Claude session `S`.
6. Claude then calls `codex_status` or `codex_result` for source-of-truth state.

## Guarantees

- Wake-up targets the same Claude session that started the job when `WakeSessionId` was captured successfully.
- Terminal signal files are isolated per Claude session, so one session does not consume another session's completion signal.
- Wake state survives normal server restarts because `WakeSessionId` is part of persisted job state.
- `codex_status`, `codex_result`, and `codex_list_jobs` remain the source-of-truth recovery tools.

## Not Guaranteed

- Waking a different Claude session, a new Claude session, or every idle Claude session.
- Delivery when `WakeSessionId` was missing at task start, the Stop hook is not running, or the process crashes before the signal file is written.
- Channel delivery or channel-based rewake. Channels are optional and non-authoritative.
- Long blocking waits over stdio. Short `codex_status wait=true` loops remain the fallback when needed.

## Primary Docs

- [Docs/requirements.md](Docs/requirements.md)
- [Docs/proposed_workflow.md](Docs/proposed_workflow.md)
- [Docs/architecture_design.md](Docs/architecture_design.md)
- [Docs/WorkItems/StopHookWakeup/plan.md](Docs/WorkItems/StopHookWakeup/plan.md)
