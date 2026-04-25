---
name: claude-codex
description: Routes natural-language Codex requests to the claude-codex MCP server. TRIGGER when the user mentions "codex", "$orchestrate", "$subagent-manager", "$prepare-orchestrate-plan", or any profile/workflow keyword (investigate, fix, implement, plan, review, orchestrate). Also TRIGGER when handling a wake-up notification from a Codex job. SKIP if the request is unrelated to Codex.
---

# claude-codex routing skill

Claude routes. MCP records and monitors. Codex executes. This skill operationalizes the routing rules in `Docs/proposed_workflow.md` and the wake-up render contract.

## Profile keyword map

| User says | Profile |
|---|---|
| investigate, explain, find out, read-only, why does, how does, trace | `investigation` |
| fix, edit, implement, change, update, refactor, add (code/test) | `implementation` |
| plan, draft a plan, prepare orchestrate plan | `planning` |
| `$orchestrate execute`, run plan, execute batch | `orchestration` (workflow=`orchestrate_execute`) |
| `$orchestrate revise`, revise plan | `orchestration` (workflow=`orchestrate_revise`) |
| review, audit, find risks, check for | `review` |

## Workflow keyword map

| User says | Workflow |
|---|---|
| (default for narrow tasks) | `direct` |
| `$subagent-manager`, "use subagent manager", broad bug trace, preserve context | `subagent_manager` |
| `$prepare-orchestrate-plan` | `prepare_orchestrate_plan` |
| `$subagent-manager` + `$prepare-orchestrate-plan` together | `managed_plan` |
| `$orchestrate execute ...` | `orchestrate_execute` |
| `$orchestrate revise ...` | `orchestrate_revise` |

## Disambiguation rule

- **Keywords present → auto-pick + dispatch.** State the choice in the lead-in: "Starting with profile=X, workflow=Y."
- **Keywords absent / ambiguous → ask user.** Example: "Read-only investigation, or implementation (edits allowed)?"
- Never edit code under `investigation` or `review` profiles regardless of user later asking; switch profiles instead.

## First-job-of-session

If the user's request implies continuation ("keep working on X", "what's codex doing", "continue the auth work"), call `codex_list_jobs` first. For unambiguous new asks ("fix the failing test"), skip — go straight to `codex_start_task`.

## Title generation

Generate a short descriptive title (3-7 words) from the prompt. Server rejects blank titles. Example:
- Prompt: "Fix the failing token cleanup test" → Title: "Fix Token Cleanup Test"

## Concurrent jobs

If `codex_list_jobs` shows an active job in the same profile and the new request relates to it, ask the user:
- **Spin up a new job** (if `maxConcurrentJobs` allows) — call `codex_start_task` again
- **Queue the prompt** for the active job — call `codex_queue_input`
- **Cancel current first** — call `codex_cancel`, then dispatch new

State all three options. Default suggestion: queue if the new request is a follow-up; new job if it's distinct work.

## Waiting-for-input flow

When `codex_status` returns `waiting_for_input`:
1. Read `pendingInputRequest` (summary, message, options)
2. Present the question to the user verbatim with options
3. Take user's reply, call `codex_send_input`
4. Resume idle (event-driven; do not poll)

## Idle behavior

**Do not poll while idle.** After dispatch, idle and wait for the Stop-hook rewake. Only use `codex_status wait=true` (≤20s) when user explicitly says "wait on job X".

## Wake-up handling — strict three-zone render

When woken by a Codex job completion notification, call `codex_result` (default `detail=full`) and `codex_status`, then render **exactly** this format:

```
Codex finished: <title>

─── Codex output ───
<full response from codex_result>
───────────────────

[codex status: context <X>% | weekly <X>% | 5h <X>%]
<jobId> · <profile>/<workflow> · <model>/<effort> · <status> in <duration>
```

Rules:
- Lead-in line: literally `Codex finished: <title>` (or `Codex failed: <title>` / `Codex cancelled: <title>` / `Codex needs input: <title>`)
- Use Unicode box-drawing rules `───` for separators (not code fences — Codex output may contain code).
- Statusline format exact: `[codex status: context X% | weekly X% | 5h X%]`. Use `?` for unavailable values.
- Job line: `<jobId> · <profile>/<workflow> · <model>/<effort> · <status> in <duration>`.
- For `failed`: append `lastError: <message>` on a new line in metadata block.
- For `waiting_for_input`: append the pending question on a new line, then ask the user.

### Multi-job footer

If 2+ jobs are active when wake fires, append after metadata block:

```

Other active: <jobId-1> (<status>, <age>), <jobId-2> (<status>, <age>)
```

## Statusline format

Always render: `[codex status: context X% | weekly X% | 5h X%]`
- Remaining percentages, not used.
- `?` for unavailable.
- Include on every Codex-related report (status, result, send_input).

## Slash commands

The plugin provides `/codex-run`, `/codex-status`, `/codex-result`, `/codex-cancel`, `/codex-profiles`. Each accepts `--help`. Slash commands skip routing inference; this skill applies only to NL requests.
