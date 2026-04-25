# Stop Hook Wake-up Implementation

## Goal

When a Claude session starts a Codex job and then goes idle, automatically wake up that **same Claude session** when the job reaches a terminal state.

This is not a general Claude notification mechanism. It is a **calling-session return path** so Claude can act as a remote-control portal into Codex without polling.

## Non-Goal

Do **not** wake:

- a new Claude session
- an arbitrary idle Claude session
- every Claude session on the machine

The wake-up target must be the Claude session that originally called `codex_start_task`.

## Contract Summary

- `codex_start_task` captures the caller Claude session id and persists it as `WakeSessionId`.
- `WakeSessionId` is durable job state and survives normal server restarts.
- Terminal completion writes exactly one signal file for that session at `wake-signals/<wakeSessionId>/<jobId>.json`.
- The Stop hook watches only the current session's directory and wakes only that same Claude session.
- Optional channel notifications may still exist, but they are not the authoritative wake path.

## Background

- `notifications/claude/channel` does not solve the requirement. In practice it leads to MCP disconnect behavior and does not provide a reliable same-session wake-up path.
- Long-polling `codex_status wait=true` is not viable for long-running jobs over stdio.
- `asyncRewake: true` + hook `exit 2` is still the correct Claude-side wake primitive.
- The missing piece is a **session-bound wake signal**, not a global notification.

## Corrected Architecture

### Core requirement: session-bound signals

Each Codex job must persist the identity of the Claude session that started it. Terminal completion must write a wake signal that is visible only to that session's Stop hook.

The previous global `wake-signals\` + "consume first file" model is incorrect because it can wake the wrong session if multiple Claude sessions exist.

### Signal directory convention

- Root signals dir: `C:\Users\misterkiem\.codex-manager\wake-signals\`
- Per-session subdir: `C:\Users\misterkiem\.codex-manager\wake-signals\<wakeSessionId>\`
- One file per completed job inside that session dir: `<jobId>.json`

Example:

```text
C:\Users\misterkiem\.codex-manager\wake-signals\
  2293728e-b20e-47cc-8b62-97db73dd3c57\
    job_20260424030153_e4afdc3ad4744fd8867b10f71f683d83.json
```

### Signal payload

```json
{
  "wakeSessionId": "2293728e-b20e-47cc-8b62-97db73dd3c57",
  "jobId": "job_xxx",
  "title": "smoke test",
  "status": "Completed",
  "resultSummary": "hello",
  "ts": "2026-04-25T01:51:00Z"
}
```

## Required Data Flow

### 1. Capture the originating Claude session identity at task start

At `codex_start_task` time, the MCP layer must capture or receive a stable identifier for the calling Claude session, here called `wakeSessionId`.

This identifier must be persisted on the job record so it survives long-running execution and restarts.

Minimum requirement:

- `CodexJob` stores `wakeSessionId`
- Job persistence includes `wakeSessionId`

The exact mechanism used to obtain the caller Claude session id can vary by integration point, but the implementation must preserve the same invariant:

- one job
- one originating Claude session
- one session-specific wake signal target

### 2. Stop hook must watch only its own session path

The Claude Stop hook for a given session must watch:

```text
C:\Users\misterkiem\.codex-manager\wake-signals\<wakeSessionId>\
```

It must not scan a shared global pool and must not consume another session's files.

### 3. Job completion writes the signal only to the originating session

When a job becomes terminal (`Completed`, `Failed`, `Cancelled`), the supervisor writes:

```text
<root>\<wakeSessionId>\<jobId>.json
```

and nowhere else.

### 4. Same-session Stop hook consumes and rewakes

When the originating Claude session goes idle:

1. Stop hook fires with `asyncRewake: true`
2. It checks its own session directory for existing files
3. If a file exists, it reads and deletes it, emits a concise message, and exits `2`
4. If none exist, it watches its own session directory with `FileSystemWatcher`
5. When a new file appears, it reads and deletes it, emits a concise message, and exits `2`

This gives same-session event-driven wake-up without polling.

## Flow

1. Claude session `S` calls `codex_start_task`
2. MCP server captures `wakeSessionId = S`
3. Job persists `wakeSessionId`
4. Claude finishes its response and goes idle
5. Session `S` Stop hook watches `wake-signals\S\`
6. Job reaches terminal state
7. MCP server writes `wake-signals\S\<jobId>.json`
8. Session `S` Stop hook consumes the file and exits `2`
9. Claude rewakes **in session `S`**

## Changes Required

### 1. MCP server: persist caller session identity on the job

Relevant areas:

- tool request handling for `codex_start_task`
- `CodexJob` model
- job persistence layer

Add a field such as:

```csharp
public string? WakeSessionId { get; set; }
```

This field must be populated when the job is created and must round-trip through persisted job state.

### 2. MCP server: write session-specific signal file on terminal transition

Relevant area:

- `ClaudeCodexMcp/Supervisor/CodexJobSupervisor.cs`

Add a helper shaped like:

```csharp
private static readonly string WakeSignalsRoot =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex-manager",
        "wake-signals");

private void WriteWakeSignal(CodexJob job)
{
    if (string.IsNullOrWhiteSpace(job.WakeSessionId))
    {
        return;
    }

    var sessionDir = Path.Combine(WakeSignalsRoot, job.WakeSessionId);
    Directory.CreateDirectory(sessionDir);

    var finalPath = Path.Combine(sessionDir, $"{job.JobId}.json");
    var tempPath = finalPath + ".tmp";

    var payload = JsonSerializer.Serialize(new
    {
        wakeSessionId = job.WakeSessionId,
        jobId = job.JobId,
        title = job.Title,
        status = job.Status.ToString(),
        resultSummary = job.ResultSummary ?? "",
        ts = DateTimeOffset.UtcNow.ToString("o")
    });

    File.WriteAllText(tempPath, payload);
    File.Move(tempPath, finalPath, overwrite: true);
}
```

Write the signal only after terminal state has been persisted.

The temp-file-plus-rename pattern avoids partial-read races from the watcher side.

### 3. Disable terminal channel notification as the wake mechanism

Completion should no longer rely on `notifications/claude/channel` for wake-up.

If terminal channel sends are still present, either:

- remove them for this workflow, or
- explicitly demote them to non-authoritative optional diagnostics

The authoritative wake path is now the session-specific filesystem signal.

### 4. Claude settings: Stop hook must be session-aware

The Stop hook should be revised so it watches a **session-specific** directory, not a global one.

The old draft hook is directionally right on `asyncRewake` and `FileSystemWatcher`, but wrong on signal scope.

The hook must:

- determine the current Claude session's wake id
- map that to `wake-signals\<wakeSessionId>\`
- consume only files in that directory

Pseudo-flow:

```powershell
$wakeSessionId = <current Claude session id>
$signalDir = "$env:USERPROFILE\.codex-manager\wake-signals\$wakeSessionId"

if (-not (Test-Path $signalDir)) { exit 0 }

$existing = Get-ChildItem $signalDir -Filter '*.json' | Select-Object -First 1
if ($existing) {
  $content = Get-Content $existing.FullName -Raw | ConvertFrom-Json
  Remove-Item $existing.FullName -Force
  Write-Output "Codex job '$($content.title)' finished: $($content.status). $($content.resultSummary)"
  exit 2
}

$watcher = New-Object System.IO.FileSystemWatcher($signalDir, '*.json')
$watcher.EnableRaisingEvents = $true
$result = $watcher.WaitForChanged([System.IO.WatcherChangeTypes]::Created, 1800000)
if ($result.TimedOut) { exit 0 }

$filePath = Join-Path $signalDir $result.Name
Start-Sleep -Milliseconds 100
$content = Get-Content $filePath -Raw | ConvertFrom-Json
Remove-Item $filePath -Force
Write-Output "Codex job '$($content.title)' finished: $($content.status). $($content.resultSummary)"
exit 2
```

Implementation note:

- The Stop hook and `codex_start_task` must agree on the same stable Claude session id value.
- If the integration cannot supply that value, automatic rewake is unavailable for that job and polling remains the fallback.

## Guarantees

- The job wakes the same Claude session that started it when `WakeSessionId` was captured successfully.
- Another Claude session on the same machine does not consume that job's terminal signal.
- Terminal signal files are written per session, not into a shared global consume-first pool.
- Channel delivery success or failure does not change the wake contract.

## Not Guaranteed

- Waking a different Claude session, a new Claude session, or all idle Claude sessions.
- Wake delivery when `WakeSessionId` is missing, the Stop hook is not active, or the server crashes before signal write.
- Channel-based wake-up reliability. Channels are optional and non-authoritative.
- More than one concurrent wake consumer per job.

## Test Plan

### Same-session correctness

1. Start Claude session `A`
2. Start a Codex job from `A`
3. Let `A` go idle
4. Verify terminal completion writes a signal under `wake-signals\A\`
5. Verify `A` rewakes automatically

### Wrong-session isolation

1. Start Claude session `A`
2. Start Claude session `B`
3. Start a Codex job from `A`
4. Let both sessions go idle
5. Verify completion writes only under `wake-signals\A\`
6. Verify `A` wakes
7. Verify `B` does **not** wake

### Race correctness

1. Start a short job from session `A`
2. Ensure the job completes before `A`'s Stop hook enters watch mode
3. Verify the pre-existing file is consumed on hook startup
4. Verify `A` still rewakes correctly

### Persistence correctness

1. Start a job from session `A`
2. Restart the MCP server during job execution if supported by the scenario
3. Verify `WakeSessionId` remains attached to the job
4. Verify terminal completion still writes into `wake-signals\A\`

## Edge Cases

- **Multiple jobs from one session**: acceptable for MVP because current profile already limits concurrency to `maxConcurrentJobs: 1`
- **Multiple Claude sessions on same machine**: session-specific directories prevent cross-session wake-up
- **Job completes before idle**: pre-existing file is consumed immediately by the Stop hook
- **Job takes more than 30 minutes**: watcher times out; manual `codex_status` remains fallback
- **Missing `WakeSessionId`**: no signal is written; this should be logged as a diagnosable defect
- **Server crash before signal write**: no wake occurs; manual polling remains fallback

## Acceptance Criteria

- A Codex job started by Claude session `S` wakes session `S` on terminal completion
- No other Claude session wakes for that job
- Wake-up does not depend on channel notifications
- Wake-up does not depend on long-polling
- Terminal job completion leaves a concise, session-local rewake message for Claude to act on
