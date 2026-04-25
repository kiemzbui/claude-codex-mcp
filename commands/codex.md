---
description: Dispatch a Codex job explicitly. Bypasses NL routing inference.
argument-hint: [--profile=X] [--workflow=Y] [--model=Z] [--effort=...] [--fast] [--title="..."] <prompt>
---

If the user passed `--help` (anywhere in `$ARGUMENTS`), output the help block below and stop. Otherwise dispatch.

## Help block

```
/codex [flags] <prompt>

Dispatch a Codex job. Skips NL routing — flags + prompt go straight to codex_start_task.

Flags (all optional):
  --profile=<name>     implementation | investigation | planning | orchestration | review
  --workflow=<name>    direct | subagent_manager | prepare_orchestrate_plan |
                       managed_plan | orchestrate_execute | orchestrate_revise
  --model=<id>         e.g. gpt-5.4
  --effort=<level>     none | minimal | low | medium | high | xhigh
  --fast               enable fast mode (service tier)
  --title="..."        override auto-generated title
  --help               show this help

If --profile or --workflow is omitted, the claude-codex skill picks based on prompt
keywords (or asks if ambiguous).

Examples:
  /codex fix the failing token cleanup test
  /codex --profile=investigation explain how auth expiry works
  /codex --profile=orchestration --workflow=orchestrate_execute $orchestrate execute Docs/WorkItems/AuthCleanup
```

## Dispatch flow

1. Parse `$ARGUMENTS` for flags and remaining prompt text.
2. If `--profile` or `--workflow` missing, apply the keyword-map from the claude-codex skill. If still ambiguous, ask the user.
3. Generate a short title from the prompt (3-7 words) unless `--title` supplied.
4. Call `mcp__claude-codex-mcp__codex_start_task` with: `profile`, `workflow`, `prompt`, `title`, optional `repo` (current repo if relevant), optional `model`, optional `effort`, optional `fastMode`.
5. Report dispatch on one line: `Dispatched: <title> (<jobId>) · <profile>/<workflow> · <model>/<effort>`.
6. Append the statusline: `[codex status: context X% | weekly X% | 5h X%]`.
7. Idle. Do not poll. Wait for wake-up.
