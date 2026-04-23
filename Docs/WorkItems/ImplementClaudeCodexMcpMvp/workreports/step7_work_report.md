# Work Report - Step 7
**Date:** 2026-04-23
**Status:** SUCCESS

## Step executed
Implemented Stage 7 - Core MCP Tool Surface for profile listing, read-only skill/agent discovery, detail retrieval, and core job lifecycle tools.

## Files changed
- ClaudeCodexMcp/Discovery/CodexDiscoveryOptions.cs - added Codex discovery environment/config options.
- ClaudeCodexMcp/Discovery/CodexCapabilityDiscovery.cs - added read-only skill and agent discovery with buckets, configured entries, cache TTL/mtime invalidation, conflicts, and metadata-only detail defaults.
- ClaudeCodexMcp/Domain/DiscoveryCacheRecords.cs - extended discovery cache records with root fingerprints, repo root, and body path metadata.
- ClaudeCodexMcp/Domain/ToolDtos.cs - added compact tool response DTOs for profiles, discovery details, jobs, status, results, input, cancellation, and listing.
- ClaudeCodexMcp/Domain/JobRecords.cs - added persisted Codex turn/session IDs needed by lifecycle tools.
- ClaudeCodexMcp/Storage/JobStore.cs - preserved Codex turn/session IDs in the reconnectable job index.
- ClaudeCodexMcp/Tools/CodexToolService.cs - implemented profile, discovery, start, status, result, send-input, cancel, and list-jobs behavior.
- ClaudeCodexMcp/Tools/CodexTools.cs - added MCP-attributed `codex_*` tool methods.
- ClaudeCodexMcp/ClaudeCodexMcpHost.cs - registered Stage 7 services and MCP tools with the host.
- ClaudeCodexMcp.Tests/Discovery/CodexCapabilityDiscoveryTests.cs - added discovery bucket, conflict, cache, configured entry, ambiguity, and full-body truncation coverage.
- ClaudeCodexMcp.Tests/Tools/CodexToolServiceTests.cs - added profile summary, policy rejection, title enforcement, durable-before-dispatch, status wait cap, reconnectable list, continuation override, and default result leakage coverage.

## Analysis queries run
- NONE - execution_recs.md defines no Stage 7 Pre queries.

## Build result
Before: 0 errors. After: 0 errors.

## Surprises / deviations from plan
*Codex*: `ClaudeCodexMcpHost.cs` was edited outside the listed Stage 7 product-code scope because the new MCP-attributed tools would otherwise compile but not be exposed by the stdio MCP host.
*Codex*: `ClaudeCodexMcp/Domain/JobRecords.cs` and `ClaudeCodexMcp/Storage/JobStore.cs` were extended outside the listed tool-DTO scope to persist turn/session IDs needed by `codex_cancel` and reconnectable lifecycle calls.
*Codex*: A parallel focused test run briefly hit a build output file lock; rerunning the affected discovery suite alone passed, and the final full validation passed.

## Verification queries (for work-verifier)
NONE - execution_recs.md does not define Stage 7 Post queries.
