---
description: Cancel a running Codex job.
argument-hint: "[jobId] (--help for usage)"
---

If `$ARGUMENTS` contains `--help`, output the help block and stop. Otherwise resolve and cancel.

## Help block

```
/codex-cancel [jobId]

Cancel a running Codex job.

  /codex-cancel <jobId>   cancel that specific job
  /codex-cancel           list active jobs in current session, ask which to cancel
  /codex-cancel --help    show this help

Notes:
  - Cancels the active job. Pending queue items remain in queue (will not deliver
    because the job is now terminal).
  - Fire-and-report; no confirmation prompt (user typed the command intentionally).
```

## Flow

1. Parse args for `jobId`.
2. If no `jobId`: call `codex_list_jobs` filtered to current session's running/queued jobs. List them numbered, ask user which. Resolve user's reply to a `jobId`.
3. Call `codex_cancel` with that `jobId`.
4. Report on one line: `Cancelled: <title> (<jobId>)` or the cancellation result string from the server.
5. Append statusline.
