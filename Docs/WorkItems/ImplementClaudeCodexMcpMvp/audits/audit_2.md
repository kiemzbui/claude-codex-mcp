# Audit 2: ImplementClaudeCodexMcpMvp Revised Plan Pack

Date: 2026-04-23
Auditor: plan-auditor
Mode: revision pass 2

## Summary Table

| Area | Result | Notes |
| --- | --- | --- |
| Required plan-pack docs | Confirmed | `plan.md`, work-item `architecture_design.md`, `execution_recs.md`, `progress.md`, prior `audit_1.md`, and `revision_1.md` are present and readable. Optional `Docs/current_architecture.md` and `Docs/auditing_guidelines.md` are absent. |
| Audit 1 error resolution | Confirmed | E1 and E2 are resolved in the revised plan pack. Stage 6 no longer re-owns app-server feasibility files, and Stage 4 now owns app-server protocol artifacts, C# bindings, validation evidence, and provenance. |
| Audit 1 supported coverage-gap resolution | Confirmed | C1-C4 are resolved in the revised plan pack. Profile field coverage, continuation override handling, channel prerequisite evidence, and app-server binding provenance are now explicit. |
| Audit 1 warning resolution | Partially confirmed | W2 is resolved. W1 and W3 remain as non-blocking source-doc drift because root source docs still list resolved work-item decisions as open or optional. |
| Source-doc consistency | Warning | The work item is internally consistent and records resolved decisions, but root `Docs/requirements.md` and root `Docs/architecture_design.md` still contain the old open-question wording. |
| Live repo consistency | Confirmed | The repo remains pre-scaffold: no `ClaudeCodexMcp.sln`, `ClaudeCodexMcp/`, `ClaudeCodexMcp.Tests/`, or `.codex-manager/` exists, matching `progress.md`. |
| Runtime prerequisites | Confirmed | `dotnet --list-sdks` includes .NET 10 SDKs `10.0.202` and `10.0.203`; `codex --version` reports `codex-cli 0.122.0`. |
| App-server generation command feasibility | Confirmed | `codex app-server --help` exposes `generate-json-schema` and `generate-ts`; both subcommand help outputs support `--out <DIR>` and `--experimental`. |
| Structured code analysis | Confirmed as unavailable | Roslyn load failed because no `.sln`, `.slnx`, `.csproj`, or `.vbproj` exists yet; `solution_status` reports no solution loaded. This matches the pre-scaffold checkpoint. |
| Orchestrator executability | Confirmed | The next executable unit is singular and clear: Stage 1. Manual gates and sequencing are explicit. No blocking amendment is required before execution starts. |
| Parallel batch contract | Confirmed | No parallel batches are proposed; `execution_recs.md` has a canonical `## Parallel Batches` section stating none, and `progress.md` uses `Next executable step`, not a batch. |
| Required amendments | No | No remaining errors or coverage gaps were found in the revised plan pack. |

## Confirmed Items

1. The revised plan pack is complete enough for the orchestrator to start with `Stage 1 - Scaffold, Options, And Logging`; `progress.md` names that as the next executable step.
2. The live repository matches the stated checkpoint: implementation files and runtime state have not been created yet.
3. The target root layout in `plan.md` matches root `Docs/architecture_design.md`: `ClaudeCodexMcp.sln`, `ClaudeCodexMcp/ClaudeCodexMcp.csproj`, and `ClaudeCodexMcp.Tests/ClaudeCodexMcp.Tests.csproj` at repository root.
4. The local .NET SDK state supports the planned `net10.0` target.
5. The local Codex CLI version matches the plan's resolved app-server protocol basis: `codex-cli 0.122.0`.
6. The Codex CLI exposes the schema and TypeScript generation commands that Stage 4 now requires.
7. Audit 1 E1 is resolved: Stage 6 excludes `ClaudeCodexMcp/Backend/AppServerFeasibility/**`, may only read that evidence, and only narrowly updates `AppServerProtocol/CSharp/**` if production backend binding adjustments require it.
8. Audit 1 E2 is resolved: Stage 4 owns `ClaudeCodexMcp/Backend/AppServerProtocol/**`, including schema, TypeScript reference, generated or vendored C# bindings, method/notification validation, and provenance.
9. Audit 1 C1 is resolved: Stage 2 now requires modeling and tests for all required profile fields, including policy summaries and defaults.
10. Audit 1 C2 is resolved: Stage 6 and Stage 7 now explicitly cover `codex_send_input` continuation overrides for `model`, `effort`, and `fastMode`, including rejection before backend side effects.
11. Audit 1 C3 is resolved: Stage 5 and Manual Smoke Gate B now require Claude Code version, minimum-version check, target-version evidence, login observability, and channel-capable launch/configuration evidence.
12. Audit 1 C4 is resolved: app-server schema, binding, approved subset, commands, version, executable path, timestamp, and gap provenance are now required.
13. Audit 1 W2 is resolved: Stage 12 excludes `ClaudeCodexMcp/Notifications/ChannelFeasibility/**` and treats feasibility probe/report evidence as read-only.
14. The manual smoke gates after Stage 4, Stage 5, and Stage 14 are represented in `plan.md`, `execution_recs.md`, and `progress.md`.
15. No unresolved-question section exists in `plan.md`; the only question marks found are intentional statusline/unavailable-value literals.

## Errors

None.

## Warnings

### W1 - Root source docs still list work-item resolved questions as open

`plan.md` says the previous source-doc open questions are resolved under `## Resolved Design Decisions`, and the revised work-item plan records concrete decisions for app-server protocol scope, discovery roots, source-bucket behavior, detail tools, and response budgets.

Root `Docs/requirements.md` still lists those items as open questions, and root `Docs/architecture_design.md` still says they remain unresolved. This is non-blocking for orchestrator execution because the work-item plan pack now contains the decisions, but future auditors or implementers may see avoidable source-doc drift.

### W2 - Root requirements still describe skill and agent detail tools as optional

The revised work-item plan makes `codex_get_skill` and `codex_get_agent` MVP tools with metadata-only defaults and explicit full-body/full-prompt opt-in. Root `Docs/requirements.md` still frames that as an open question.

This is acceptable for the current plan pack because the resolved decision is explicit in `plan.md`, `architecture_design.md`, and `execution_recs.md`. It remains source-doc housekeeping, not a plan-pack blocker.

## Coverage Gaps

None.

## Unresolved Questions Or Blocked Assumptions

No blocking unresolved questions were found in `plan.md`.

The plan intentionally defers two runtime capability decisions to manual feasibility gates:

- Stage 4 / Manual Smoke Gate A determines whether app-server-first behavior can proceed or whether documented degraded behavior is required.
- Stage 5 / Manual Smoke Gate B determines whether production channel notification support is enabled by default or remains disabled/fallback-aware.

These are deliberate gates, not unresolved plan defects.

## Batch-Contract Audit

The batch contract passes.

- `plan.md` states that no parallel batches are proposed for the initial execution plan.
- `execution_recs.md` has a canonical `## Parallel Batches` section and states that no parallel batches are proposed.
- `progress.md` uses `Next executable step: Stage 1 - Scaffold, Options, And Logging`.
- `progress.md` also states that no parallel batch is currently next.
- No named batch is implied by informal wording, no batch member ownership needs validation, and no smoke gate is crossed by a batch because there is no proposed batch.

## Audit Result

No plan-pack amendments are required before orchestrator execution.

Counts:

- Confirmed items: 15
- Errors: 0
- Warnings: 2
- Coverage gaps: 0

