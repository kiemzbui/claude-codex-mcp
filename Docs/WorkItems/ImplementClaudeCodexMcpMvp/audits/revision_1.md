# Revision 1: ImplementClaudeCodexMcpMvp Plan Pack

Date: 2026-04-23
Revisor: plan-revisor

## Summary Table

| Area | Audit count | Confirmed | Auto-fixed | Outstanding |
| --- | ---: | ---: | ---: | ---: |
| Errors | 2 | 2 | 2 | 0 |
| Warnings | 3 | 3 | 1 | 2 |
| Coverage gaps | 4 | 4 | 4 | 0 |
| Total | 9 | 9 | 7 | 2 |

Revision result: amendments made.

Independent verification performed:

- The live repo remains pre-scaffold: `ClaudeCodexMcp.sln`, `ClaudeCodexMcp/`, `ClaudeCodexMcp.Tests/`, and `.codex-manager/` are absent.
- `dotnet --list-sdks` includes `10.0.202`.
- `codex --version` reports `codex-cli 0.122.0`.
- `codex app-server --help` exposes `generate-json-schema` and `generate-ts`.
- `codex app-server generate-json-schema --help` supports `--out <DIR>` and `--experimental`.
- `codex app-server generate-ts --help` supports `--out <DIR>` and `--experimental`.

## Fixes Applied

1. E1 - Corrected Stage 6 backend ownership.
   - Updated Stage 6 owned write scope in `plan.md` to exclude `ClaudeCodexMcp/Backend/AppServerFeasibility/**`.
   - Added read-only/adaptation guidance for `AppServerFeasibility/**` and `AppServerProtocol/**` so production backend work does not silently re-own feasibility probe evidence.

2. E2 and C4 - Added explicit app-server protocol binding and provenance ownership.
   - Added Stage 4 ownership for `ClaudeCodexMcp/Backend/AppServerProtocol/**`.
   - Required generated JSON Schema under `AppServerProtocol/Schema/**`.
   - Required generated TypeScript reference under `AppServerProtocol/TypeScript/**`.
   - Required generated or vendored C# binding surface under `AppServerProtocol/CSharp/**`.
   - Required `AppServerProtocol/provenance.md` and feasibility-report provenance with CLI version, executable path, generation commands, timestamp, output paths, approved method/notification subset, and schema gaps.
   - Added validation/probe evidence requirements for the approved MVP method and notification subset.
   - Mirrored the protocol provenance contract in `architecture_design.md` and `execution_recs.md`.

3. C1 - Expanded profile field coverage.
   - Updated Stage 2 to model and test every required profile field: `repo`, `allowedRepos`, `taskPrefix`, `backend`, `readOnly`, `permissions`, `defaultWorkflow`, `allowedWorkflows`, `maxConcurrentJobs`, `channelNotifications`, model default, effort default, and fast-mode or service-tier default.
   - Updated Stage 7 and smoke guidance so `codex_list_profiles` returns compact policy summaries for those fields.

4. C2 - Added continuation dispatch override validation.
   - Updated Stage 6 backend contract expectations for continuation `model`, `effort`, and `fastMode` pass-through after policy validation.
   - Updated Stage 7 tool work and tests so `codex_send_input` accepts, validates, persists, dispatches, and rejects continuation overrides before backend side effects when disallowed.
   - Added matching smoke and verifier guidance.

5. C3 - Added channel feasibility environment-prerequisite evidence.
   - Updated Stage 5 to record Claude Code version, minimum-version check, target-version evidence, `claude.ai` login status when observable, and channel-capable launch/configuration.
   - Updated Manual Smoke Gate B evidence in `execution_recs.md`.

6. W2 - Clarified Stage 12 probe versus production notification ownership.
   - Updated Stage 12 owned write scope to exclude `ClaudeCodexMcp/Notifications/ChannelFeasibility/**`.
   - Required Stage 12 to treat the Stage 5 probe and `channel_feasibility.md` as read-only evidence.
   - Added architecture and verifier cautions so production notification code is implemented outside the feasibility folder.

7. Execution guidance alignment.
   - Updated `execution_recs.md` smoke flows and verifier cautions for protocol provenance, profile field coverage, continuation overrides, and feasibility-folder ownership.
   - No parallel batch contract change was needed; the plan remains sequential.

## Outstanding Issues

1. W1 - Root source docs still list resolved questions as open.
   - Confirmed: `Docs/requirements.md` and `Docs/architecture_design.md` still contain the original open-question wording.
   - Classification: source-doc housekeeping, not a work-item plan-pack blocker.
   - Reason not auto-fixed: the revision task is scoped to the plan pack, and the work-item plan already records the resolved decisions.

2. W3 - Root source docs still describe `codex_get_skill` and `codex_get_agent` as optional.
   - Confirmed: the work-item plan promotes them to MVP tools, while `Docs/requirements.md` still labels them optional detail tools.
   - Classification: source-doc drift, not a work-item plan-pack blocker.
   - Reason not auto-fixed: changing product requirements text is outside the plan-pack amendment scope for this revision.

No new unresolved question was introduced in `plan.md`. No user-facing halt is required for continued plan execution.

## Unchanged Confirmed Items

- `progress.md` remains valid: the next executable step is still `Stage 1 - Scaffold, Options, And Logging`.
- No parallel batches are proposed; `execution_recs.md` still has a canonical `## Parallel Batches` section stating none.
- Manual Smoke Gates A, B, and C remain explicit in `plan.md`, `progress.md`, and `execution_recs.md`.
- The repo still has no scaffolded implementation files, matching the current checkpoint.
- The plan still uses the root project layout required by the work-item architecture companion.
