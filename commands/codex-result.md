---
description: Fetch a Codex job's final output. Defaults to full output, falls back to summary if too large.
argument-hint: "<jobId> [--summary] [--page=N] (--help for usage)"
---

If `$ARGUMENTS` contains `--help`, output the help block and stop. Otherwise resolve and render.

## Help block

```
/codex-result <jobId> [--summary] [--page=N]

Fetch Codex job final output.

  /codex-result <jobId>             full output (falls back to summary if too large)
  /codex-result <jobId> --summary   compact summary only
  /codex-result <jobId> --page=N    read paginated chunk N (after a truncated full)
  /codex-result --help              show this help

If --summary returned because the full output didn't fit, walk pages with --page=1, --page=2, ...
```

## Flow

1. Parse args: `jobId` (required), optional `--summary`, optional `--page=N`.
2. If no `jobId` and no `--help`: call `codex_list_jobs` for completed jobs in current session, list them, ask which.
3. If `--page=N`: call `codex_read_output` with `jobId`, `offset`/`limit` derived from N.
4. If `--summary`: call `codex_result` with `detail="summary"`.
5. Default: call `codex_result` with `detail="full"`. If response indicates truncation, fall back to summary + offer `/codex-result <jobId> --page=1`.

## Render — three-zone format (matches wake-up)

```
Codex result: <title>

─── Codex output ───
<full output OR summary OR page chunk>
───────────────────

[codex status: context X% | weekly X% | 5h X%]
<jobId> · <profile>/<workflow> · <model>/<effort> · <status> in <duration>
```

If truncated (full didn't fit), append after metadata:
```

[truncated — use /codex-result <jobId> --page=1 to walk pages]
```
