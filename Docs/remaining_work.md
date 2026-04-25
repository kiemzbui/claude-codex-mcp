# Remaining Work Checklist

Snapshot 2026-04-25 after plugin shipped + end-to-end smoke verified.

## Profiles

- [x] Define `implementation`, `investigation`, `planning`, `orchestration`, `review` in `codex-manager.example.json` + `codex-manager.local.json`
- [x] Per-profile `allowedWorkflows`, `maxConcurrentJobs`, defaults (`gpt-5.4` / `high` / `fastMode=true`)
- [ ] Verify server accepts `"*"` wildcard for `repo` / `allowedRepos` (otherwise need real allowlist)
- [ ] Verify `sandbox: "read-only"` is the exact value Codex app-server expects for read-only profiles
- [ ] Smoke test: dispatch via each profile (implementation, investigation, planning, orchestration, review) and confirm routing + permission posture
- [ ] Verify routing skill keyword-map dispatches correctly to each new profile

## Polish & cleanup

- [ ] Remove diagnostic `LogInformation` calls in `WriteWakeSignalIfNeededAsync` (added during issue #12 debug)
- [ ] Write README install steps: gist URL, `gh auth`, `git config insteadOf` for HTTPS, `/plugin` install flow
- [ ] Document portable-install prereqs (.NET 10 SDK, PowerShell, gh-authenticated GitHub for private repo)
- [ ] Decide whether to keep `marketplace.json` at repo root (gitignored) or remove entirely

## Outstanding bugs

- [ ] **#1** `MapThreadRead` fatal on "rollout is empty" responses (low priority, transient)
- [ ] **#13** First-exchange-after-restart asyncRewake doesn't render visibly (signal consumed, no visible wake message)
- [ ] **#14** Statusline `context` field renders as `?` (`weekly`/`5h` populate, `context` does not)

## Codex-side (in progress, owned by codex)

- [ ] Context status calculation (codex working on this — when done, statusline `context` should populate)

## Future / nice-to-have

- [ ] Plugin auto-update toggle on by default in install instructions
- [ ] Pre-built MCP server binary distribution (avoid .NET 10 SDK requirement on target)
- [ ] Macros/aliases for common dispatch patterns (`/codex-fix <prompt>` → `/codex --profile=implementation --workflow=direct ...`)
- [ ] Cross-session job visibility default toggle (`--all` vs current-session-only)

## Done (recent)

- Plugin shipped to github via private gist marketplace
- Skill + 5 slash commands (`/codex`, `/codex-status`, `/codex-result`, `/codex-cancel`, `/codex-profiles`) registered
- Stop hook bundled in plugin (portable, no per-machine settings.local.json needed)
- Version field omitted — commit SHA used; no manual version bumps
- End-to-end smoke verified: NL dispatch → idle → asyncRewake → three-zone render
- Event-driven model documented; idle polling removed from spec
