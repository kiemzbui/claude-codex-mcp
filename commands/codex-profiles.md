---
description: List available Codex execution profiles and modes.
argument-hint: "[--detail] (--help for usage)"
---

If `$ARGUMENTS` contains `--help`, output the help block and stop. Otherwise list.

## Help block

```
/codex-profiles [--detail]

List available Codex profiles (execution policy).

  /codex-profiles            compact list: name + purpose
  /codex-profiles --detail   include maxConcurrentJobs, defaultWorkflow, allowedWorkflows,
                             readOnly, default model/effort/fastMode
  /codex-profiles --help     show this help
```

## Flow

1. Parse args for `--detail`.
2. Call `codex_list_profiles`.
3. Render compact list (default) or full table (`--detail`).

## Compact render

```
Available profiles:

  implementation   edits code, tests, docs, configs in target repo
  investigation    read-only; answers, traces, recon (no edits)
  planning         creates/revises orchestration-ready plan packs
  orchestration    launches/continues $orchestrate execution
  review           review-only; findings, no edits

Workflows: direct, subagent_manager, prepare_orchestrate_plan, managed_plan,
           orchestrate_execute, orchestrate_revise (per-profile allowlist applies)

Use /codex-run --profile=<name> --workflow=<name> <prompt> to dispatch explicitly.
```

## Detail render

For each profile, include: name, purpose, `readOnly`, `defaultWorkflow`, `allowedWorkflows`, `maxConcurrentJobs`, default `model`, default `effort`, default `fastMode`.
