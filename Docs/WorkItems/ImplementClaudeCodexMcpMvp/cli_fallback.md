# CLI Fallback Notes

*Codex*: Stage 13 added `CodexCliBackend` as a degraded `ICodexBackend` implementation for direct, non-interactive `codex exec` work.

*Codex*: Supported fallback behavior is limited to direct workflow execution, final output capture through `--output-last-message` with stdout fallback, local output-log persistence, best-effort changed-file summaries from `git status --short`, and simple test-summary extraction from captured text.

*Codex*: Unsupported fallback capabilities are reported through `degradedCapabilities`: live observe/status polling, follow-up input, cancellation, usage/context/account windows, resume, and structured clarification prompts. Unavailable usage/statusline fields remain `?` through the existing usage reporter.

*Codex*: App-server remains the default when available. `CodexCliBackendSelection` only resolves CLI when the profile backend policy explicitly requests `cli`, `cliFallback`, or `cli-fallback`.

*Codex*: Runtime tool routing is wired through the profile-selected backend policy. `CodexToolService` resolves CLI only for profiles whose backend policy explicitly allows CLI fallback; app-server profiles and omitted backend policies continue to use app-server routing.
