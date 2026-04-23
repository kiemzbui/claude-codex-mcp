# Audit 1: ImplementClaudeCodexMcpMvp Plan Pack

Date: 2026-04-22
Auditor: plan-auditor

## Summary Table

| Area | Result | Notes |
| --- | --- | --- |
| Required plan-pack docs | Confirmed | `plan.md`, work-item `architecture_design.md`, `execution_recs.md`, and `progress.md` are present. Root `current_architecture.md` and `auditing_guidelines.md` are absent, so no extra guidance was available from those optional files. |
| Source-doc consistency | Warning | The work-item plan intentionally resolves root source-doc open questions, but root `Docs/requirements.md` and `Docs/architecture_design.md` still list those questions as open. |
| Live repo consistency | Confirmed | Repo currently contains docs and Git metadata only; no solution/project/code scaffold exists, matching `progress.md` lines 25-27. |
| Runtime prerequisites | Confirmed | `dotnet --list-sdks` includes `10.0.202`; `codex --version` reports `codex-cli 0.122.0`, matching the plan's pinned local CLI version. |
| Structured code analysis | Confirmed as unavailable | Roslyn load was attempted and failed because no `.sln`, `.slnx`, `.csproj`, or `.vbproj` exists yet. This matches the pre-scaffold state. |
| Orchestrator executability | Amendments required | Sequential execution shape and next step are clear, but two plan amendments are needed before execution is low-risk. |
| Parallel batch contract | Confirmed | No parallel batches are proposed; `execution_recs.md` has a canonical `## Parallel Batches` section stating none, and `progress.md` uses singular `Next executable step`. |
| Manual smoke gates | Confirmed | Gates after Stage 4, Stage 5, and Stage 14 are represented in `plan.md`, `execution_recs.md`, and `progress.md`. |
| Required amendments | Yes | See Errors E1-E2. |

## Confirmed Items

1. The work item directory is present at `Docs/WorkItems/ImplementClaudeCodexMcpMvp/`, and the required core files are readable.
2. `progress.md` correctly says no implementation files have been scaffolded yet and names `Stage 1 - Scaffold, Options, And Logging` as the next executable step.
3. The live repository matches that checkpoint: `ClaudeCodexMcp.sln`, `ClaudeCodexMcp/`, `ClaudeCodexMcp.Tests/`, and `.codex-manager/` are absent.
4. The local .NET SDK has a usable .NET 10 SDK (`10.0.202`) for the planned `net10.0` target.
5. The local Codex CLI reports `codex-cli 0.122.0`, matching the plan's resolved app-server protocol source claim.
6. The plan follows the root-layout decision from `Docs/architecture_design.md` by placing `ClaudeCodexMcp.sln`, `ClaudeCodexMcp/`, and `ClaudeCodexMcp.Tests/` at the repository root rather than under `src/`.
7. Stage 1 covers Generic Host setup, `ModelContextProtocol`, `Microsoft.Extensions.Hosting`, stderr/file logging, `.gitignore`, and a smoke test, consistent with `Docs/requirements.md` implementation-platform requirements.
8. Stages 2, 3, 7, 8, 9, 10, 11, 12, and 13 cover the major required profile, workflow, storage, tool, supervisor, queue, usage, output, notification, and CLI fallback surfaces from `Docs/requirements.md`.
9. Stage 4 and Stage 5 are explicit feasibility gates before production app-server and channel-dependent work, matching the source requirements.
10. Response-size decisions in `plan.md` lines 389-413 and 572 cover the byte-budget question from `Docs/requirements.md` line 829.
11. Discovery decisions in `plan.md` lines 288-299 and 569-571 cover Windows global discovery, source buckets, conflict reporting, and metadata-only detail defaults.
12. The parallelization plan is compliant for the current revision: no informal parallel work is being authorized, no named batch is missing, and `progress.md` uses a singular next step.

## Errors

### E1 - Stage 6 backend write scope has an incorrect feasibility-folder exception

`plan.md` line 251 says Stage 6 owns `ClaudeCodexMcp/Backend/**` except `ChannelFeasibility/**`. `ChannelFeasibility` is under `ClaudeCodexMcp/Notifications/ChannelFeasibility/**` in Stage 5 (`plan.md` line 221), not under `Backend/`.

Effect: Stage 6 unintentionally re-owns `ClaudeCodexMcp/Backend/AppServerFeasibility/**`, which Stage 4 owns at `plan.md` line 186. That makes the handoff between the app-server feasibility probe and production backend implementation ambiguous.

Required amendment: Change the Stage 6 exclusion to the actual app-server feasibility probe scope, for example `ClaudeCodexMcp/Backend/** except ClaudeCodexMcp/Backend/AppServerFeasibility/**`, or explicitly state which feasibility probe files Stage 6 may read or adapt.

### E2 - No explicit stage owns generated or vendored app-server protocol bindings

`Docs/requirements.md` line 781 requires generating or vendoring Codex app-server protocol bindings from the installed app-server schema. The work-item plan pins the MVP app-server contract to generated v2 protocol from local `codex-cli 0.122.0` at `plan.md` line 565 and lists methods at line 566, but no stage explicitly owns generating, vendoring, locating, or validating those bindings.

Effect: Stage 4 can probe the app-server and Stage 6 can implement `CodexAppServerBackend`, but the executor has no concrete write scope or exit criterion for the protocol binding artifact that the backend depends on.

Required amendment: Add protocol binding generation/vendor work to Stage 4 or Stage 6, with concrete output paths, regeneration command or source schema path, and tests/probe evidence proving the bindings match the local app-server protocol.

## Warnings

### W1 - Root source docs still present resolved questions as unresolved

The plan states the previous source-doc open questions are resolved in `plan.md` line 52 and records decisions at lines 563-572. However, `Docs/requirements.md` lines 823-829 and `Docs/architecture_design.md` lines 310-318 still list the same questions as open.

This does not block Stage 1 because the work-item plan can act as the execution source of truth. It can confuse future auditors or executors who cross-check root docs and think the work item invented answers without updating source material.

### W2 - Stage 12 notification scope may blur probe and production ownership

Stage 5 owns `ClaudeCodexMcp/Notifications/ChannelFeasibility/**` and the channel feasibility report. Stage 12 owns `ClaudeCodexMcp/Notifications/** except feasibility report files` at `plan.md` line 422.

This likely allows Stage 12 to edit the Stage 5 probe code, while only excluding report files. If the probe should remain as evidence, Stage 12 should exclude `ClaudeCodexMcp/Notifications/ChannelFeasibility/**` or state that production code may reuse/move it.

### W3 - Source-doc optional detail tools are promoted to MVP without cross-doc update

`Docs/requirements.md` presents `codex_get_skill` and `codex_get_agent` as optional detail tools, while the plan requires them as MVP tools at `plan.md` line 288 and records that as a resolved decision at line 571.

This is probably acceptable because the work item resolves the open question, but it is another place where root docs and work-item docs differ.

## Coverage Gaps

### C1 - Profile field coverage is not explicit enough in tests

`Docs/requirements.md` requires profile support for `repo`, `allowedRepos`, `taskPrefix`, `backend`, `readOnly`, `permissions`, `defaultWorkflow`, `allowedWorkflows`, `maxConcurrentJobs`, channel notification policy, model default, effort default, and fast-mode/service-tier default. Stage 2 names `ManagerOptions`, `ProfileOptions`, and several validation behaviors, but its exit criteria focus on profile load, unknown profile, repo allowlist, workflow, effort, title, and `maxConcurrentJobs`.

Recommended amendment: Add explicit Stage 2 or Stage 7 tests for `taskPrefix`, `backend`, `readOnly`, `permissions` summary, channel notification defaults, model default/override policy, effort default/allowed values, and fast-mode/service-tier default.

### C2 - Continuation dispatch overrides need explicit acceptance and tests

The requirements make `model`, `effort`, and `fastMode` dispatch options apply to starting and continuing Codex work. The plan clearly covers start-time validation and selected job options, but it does not explicitly require `codex_send_input` to accept, validate, persist, and pass through continuation overrides.

Recommended amendment: Add Stage 7 tool tests and Stage 6 backend/fake-backend tests for `codex_send_input` continuation overrides, including rejection of disallowed values before backend side effects.

### C3 - Channel feasibility should explicitly verify environment prerequisites

`Docs/requirements.md` line 340 requires Claude Code version at least `2.1.80`, target `2.1.117`, Claude login, and channel-capable launch/configuration. Stage 5 requires a channel probe and report, but it does not explicitly require recording the Claude Code version and login/channel prerequisite checks.

Recommended amendment: Add those fields to the Stage 5 feasibility report requirements and to `execution_recs.md` Manual Smoke Gate B evidence.

### C4 - Binding provenance and app-server schema evidence are not captured

This overlaps E2 but is distinct as an audit trail gap: the plan names a local generated v2 protocol and method subset, but no file is planned to record schema version/provenance, generation timestamp, or command output. Future backend failures could be hard to diagnose after the installed Codex CLI changes.

Recommended amendment: Require the feasibility report or binding artifact to record CLI version, schema source, generation command, and approved method subset.

## Unresolved Questions Or Blocked Assumptions

No open-question section is present in `plan.md`, and no blocking unresolved question in `plan.md` prevents Stage 1 from starting.

Blocked assumptions intentionally deferred by the plan:

- App-server runtime capability remains gated by Stage 4 and Manual Smoke Gate A.
- Claude Code Channel runtime delivery remains gated by Stage 5 and Manual Smoke Gate B.

Blocked assumption requiring amendment:

- The app-server protocol binding source and ownership are not defined. This should be fixed before Stage 4 or Stage 6 execution reaches production backend work.

## Batch-Contract Audit

The batch contract passes for this revision.

- `plan.md` line 516 says no parallel batches are proposed.
- `execution_recs.md` line 13 contains the canonical `## Parallel Batches` section and line 15 states none are proposed.
- `progress.md` line 27 uses `Next executable step: Stage 1 - Scaffold, Options, And Logging`.
- `progress.md` line 32 says no parallel batch is currently next.
- No smoke gate sits between members of a proposed batch because there is no proposed batch.

## Audit Result

Amendments are required before execution is low-risk.

Counts:

- Confirmed items: 12
- Errors: 2
- Warnings: 3
- Coverage gaps: 4

