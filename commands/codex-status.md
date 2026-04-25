---
description: Show Codex job status with context, usage, and metadata.
argument-hint: "[jobId] [--all] (--help for usage)"
---

If `$ARGUMENTS` contains `--help`, output the help block and stop. Otherwise resolve and report.

## Help block

```
/codex-status [jobId] [--all]

Show status for one or more Codex jobs.

  /codex-status                   newest active job(s) started by THIS session
  /codex-status <jobId>           that specific job (any session)
  /codex-status --all             every active job in the repo (cross-session)
  /codex-status --help            show this help
```

## Resolution flow

1. Parse args: extract `jobId` (any token starting with `job_`), `--all` flag.
2. **If `<jobId>` given:** call `codex_status` and `codex_usage` for that job. Render single-job report.
3. **If `--all`:** call `codex_list_jobs` for active states. Render compact table of all active jobs.
4. **No args:** call `codex_list_jobs` filtered to current session's jobs (jobs where `wakeSessionId` matches current session). If exactly one, render single-job report. If multiple, render compact table.

## Single-job render

```
[codex status: context X% | weekly X% | 5h X%]

<title> (<jobId>): <status>
profile/workflow: <profile>/<workflow>
model: <model> | effort: <effort> | fastMode: <true|false>
queue: <pending> pending / <delivered> delivered / <failed> failed
updated: <age> ago
```

If `waiting_for_input`, append the pending question.
If `failed`, append `lastError`.

## Multi-job render

```
[codex status: weekly X% | 5h X%]

Active (<n>):
  <jobId>  <title>  <status>  <profile>/<workflow>  updated <age>
  ...
```

(omit `context` from header statusline when reporting multiple jobs; per-job context varies)
