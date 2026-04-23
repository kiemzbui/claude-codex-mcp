# Execution Recommendations: Implement Claude Codex MCP MVP

## Execution Mode

Run the plan sequentially:

```text
$orchestrate execute Docs/WorkItems/ImplementClaudeCodexMcpMvp
```

The first executable step is Stage 1 - Scaffold, Options, And Logging. Keep coder and verifier passes small and stage-bound.

## Parallel Batches

No parallel batches are proposed for the initial run.

Reason:

- Stage 1 through Stage 8 establish shared project structure, domain DTOs, persistence formats, backend contracts, DI registration, and supervisor behavior.
- Later stages still touch shared `Tools/`, `Domain/`, `Storage/`, or `Supervisor/` files.
- A premature batch would create avoidable merge and verification ambiguity.

If a later plan revision adds parallelism, define it canonically in this section and update `progress.md` with `Next executable batch: <batch-name> (<step-id>, <step-id>).`

## Manual Smoke Gates

### Manual Smoke Gate A: App-Server Feasibility

Occurs after Stage 4.

Required evidence:

- `Docs/WorkItems/ImplementClaudeCodexMcpMvp/app_server_feasibility.md`
- `ClaudeCodexMcp/Backend/AppServerProtocol/provenance.md`
- Generated schema bundle under `ClaudeCodexMcp/Backend/AppServerProtocol/Schema/**`
- Generated TypeScript reference under `ClaudeCodexMcp/Backend/AppServerProtocol/TypeScript/**`
- Generated or vendored C# binding surface under `ClaudeCodexMcp/Backend/AppServerProtocol/CSharp/**`
- `codex --version` output and `Get-Command codex` resolved executable path
- Binding generation commands:
  - `codex app-server generate-json-schema --experimental --out ClaudeCodexMcp/Backend/AppServerProtocol/Schema`
  - `codex app-server generate-ts --experimental --out ClaudeCodexMcp/Backend/AppServerProtocol/TypeScript`
- Approved MVP method and notification subset validation, or explicit documented gaps
- Probe command or test command used
- Whether app-server can start a thread/turn
- Whether status can be streamed or polled
- Whether final output can be read
- Whether token usage/context window can be read
- Whether account rate-limit windows can be read
- Whether prior thread state can be resumed or read
- Any documented fallback behavior

Do not continue into production backend-dependent work until this gate is reviewed.

### Manual Smoke Gate B: Channel Feasibility

Occurs after Stage 5.

Required evidence:

- `Docs/WorkItems/ImplementClaudeCodexMcpMvp/channel_feasibility.md`
- Claude Code version and whether it satisfies the required minimum `2.1.80`
- Whether the target environment is `2.1.117` or the exact observed version if different
- Claude `claude.ai` login prerequisite evidence when observable
- Channel-capable launch/configuration command, such as `--channels` or the current development-channel mechanism
- Channel configuration used
- Payload emitted
- Whether an active Claude Code session received the event
- Whether production channel support is enabled by default or disabled by default
- Confirmation that polling remains the fallback

Do not make channel notifications mandatory. If delivery is not verified, implement them as disabled-by-default or best-effort according to the report.

### Manual Smoke Gate C: End-To-End MVP

Occurs after Stage 14.

Required evidence:

- `Docs/WorkItems/ImplementClaudeCodexMcpMvp/smoke_results.md`
- `dotnet build ClaudeCodexMcp.sln`
- `dotnet test ClaudeCodexMcp.sln`
- Local MCP registration or launch command used
- Pass/fail results for the smoke flows listed below

## Smoke Flow Checklist

Use these as the final smoke suite unless a verifier narrows a flow for repeatability:

- `codex_list_profiles` returns configured profiles with compact policy summaries, including `taskPrefix`, `backend`, `readOnly`, `permissions`, `channelNotifications`, default model, default effort, and fast-mode or service-tier default.
- `codex_list_skills` returns available skills without full skill bodies by default.
- `codex_list_agents` returns available agents without full prompts by default.
- `codex_start_task` starts a read-only direct task with a non-empty title.
- `codex_status` reports compact running/completed state and supports `wait=true` with `timeoutSeconds = 20`.
- `codex_result` returns compact final output with changed files, tests, session/thread ID, and artifact refs when known.
- `codex_send_input` sends a follow-up to a job that can accept input.
- `codex_send_input` accepts valid `model`, `effort`, and `fastMode` continuation overrides, persists selected values, and rejects disallowed overrides before backend side effects.
- `codex_queue_input` persists a queued prompt and reports queue position.
- Supervisor delivers queued input FIFO after a successful active turn.
- `codex_cancel_queued_input` cancels only pending queue items.
- `codex_cancel` cancels an active job without cancelling unrelated queued items by accident.
- `codex_list_jobs` recovers persisted jobs after restarting the MCP server.
- `waiting_for_input` includes structured clarification data.
- Concurrent-job policy rejects or reports a policy conflict when `maxConcurrentJobs` would be exceeded.
- `codex_usage` returns percentage-based short-window and weekly usage when available and `?` when unavailable.
- Statusline renders `[codex status: context ? | weekly ? | 5h ?]` style fields consistently.
- `codex_read_output` returns paginated chunks and local artifact refs without loading full transcripts by default.
- Channel notifications fire for completed, failed, cancelled, waiting-for-input, and queue-delivery-failure states when channel feasibility passed.
- Polling recovery works when channels are disabled or unavailable.
- CLI fallback reports degraded capabilities and renders unsupported statusline fields as `?`.

## Verifier Cautions

- Check stdout discipline for stdio mode. Logging to stdout is a protocol bug.
- Check that `.codex-manager/` is ignored and all runtime state is under the repo root state directory.
- Check that `Docs/` is not used as production source code.
- Check that profile validation happens before backend dispatch and before durable active-job side effects beyond rejected request logging.
- Check that profile tests cover `taskPrefix`, `backend`, `readOnly`, `permissions`, `channelNotifications`, model defaults, effort defaults, and fast-mode or service-tier defaults.
- Check that `codex_send_input` continuation overrides use the same policy validation as start dispatch and are rejected before backend calls when disallowed.
- Check that prompt bodies, transcripts, raw logs, secrets, and long diffs do not appear in default MCP responses.
- Check that channel notifications never become source-of-truth state.
- Check that queue delivery, cancellation, and status updates are protected by per-job locking or equivalent concurrency control.
- Check that app-server and channel gaps are represented as documented degraded behavior instead of silent success.
- Check that app-server protocol artifacts are generated or vendored under `ClaudeCodexMcp/Backend/AppServerProtocol/**` and that `provenance.md` records CLI version, executable path, generation commands, timestamp, approved subset, and gaps.
- Check that Stage 6 does not edit `ClaudeCodexMcp/Backend/AppServerFeasibility/**` and preserves Stage 4 protocol provenance unless it reruns and documents the generation commands.
- Check that Stage 12 does not edit `ClaudeCodexMcp/Notifications/ChannelFeasibility/**` or treat probe evidence as production notification code.
- Check that response size limits and pagination are enforced for full output paths: 8 KB summary, 32 KB normal, 128 KB full, 64 KB paginated chunks, 256 KB absolute hard cap, and 4 KB channel events.
- Check that discovery responses preserve `global`, `repoLocal`, and `configured` source scopes and surface name conflicts instead of silently deduping.
- Check that `codex_get_skill` and `codex_get_agent` are implemented as MVP tools but do not return full bodies/prompts unless explicitly requested.

## Commit Guidance

- Prefer one commit per verified stage.
- Do not commit feasibility-gated production behavior until the relevant report is written and reviewed.
- If a stage touches shared domain or storage contracts, include tests in the same stage commit.
- Do not include `.codex-manager/` runtime files, local logs, or secrets in commits.

## Resolved Design Decisions

- App-server MVP support is limited to generated v2 thread/turn lifecycle, read/resume, skills, models, account usage, and required notification methods from local `codex-cli 0.122.0`.
- App-server protocol provenance is owned by Stage 4 under `ClaudeCodexMcp/Backend/AppServerProtocol/**` using `codex app-server generate-json-schema --experimental --out ClaudeCodexMcp/Backend/AppServerProtocol/Schema` and `codex app-server generate-ts --experimental --out ClaudeCodexMcp/Backend/AppServerProtocol/TypeScript`.
- Global Codex skills and agents are discovered from `CODEX_HOME` when set, otherwise `%USERPROFILE%\.codex\skills` and `%USERPROFILE%\.codex\agents`; missing roots are empty.
- Repo-local, global, and configured discovery results are listed separately by source, with optional merged view preserving source metadata and conflicts.
- `codex_get_skill` and `codex_get_agent` are required MVP tools.
- Response budgets are byte-based: 8 KB summary, 32 KB normal, 128 KB full, 64 KB paginated chunk, 256 KB hard cap, 4 KB channel event.
- Channel delivery remains fallback-aware even if feasibility passes because the source docs treat channels as research-preview.
