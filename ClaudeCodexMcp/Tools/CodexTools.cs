using ClaudeCodexMcp.Domain;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Text.Json;

namespace ClaudeCodexMcp.Tools;

[McpServerToolType]
public sealed class CodexTools
{
    private readonly CodexToolService service;

    public CodexTools(CodexToolService service)
    {
        this.service = service;
    }

    [McpServerTool]
    public CodexListProfilesResponse codex_list_profiles() =>
        service.ListProfiles();

    [McpServerTool]
    public Task<DiscoveryBucketedResponse> codex_list_skills(
        bool forceRefresh = false,
        string? repo = null,
        CancellationToken cancellationToken = default) =>
        service.ListSkillsAsync(forceRefresh, repo, cancellationToken);

    [McpServerTool]
    public Task<DiscoveryBucketedResponse> codex_list_agents(
        bool forceRefresh = false,
        string? repo = null,
        CancellationToken cancellationToken = default) =>
        service.ListAgentsAsync(forceRefresh, repo, cancellationToken);

    [McpServerTool]
    public Task<DiscoveryDetailResponse> codex_get_skill(
        string? name,
        string? sourceScope = null,
        string? sourcePath = null,
        bool includeBody = false,
        bool forceRefresh = false,
        string? repo = null,
        int maxBytes = 32768,
        CancellationToken cancellationToken = default) =>
        service.GetSkillAsync(name, sourceScope, sourcePath, includeBody, forceRefresh, repo, maxBytes, cancellationToken);

    [McpServerTool]
    public Task<DiscoveryDetailResponse> codex_get_agent(
        string? name,
        string? sourceScope = null,
        string? sourcePath = null,
        bool includePrompt = false,
        bool forceRefresh = false,
        string? repo = null,
        int maxBytes = 32768,
        CancellationToken cancellationToken = default) =>
        service.GetAgentAsync(name, sourceScope, sourcePath, includePrompt, forceRefresh, repo, maxBytes, cancellationToken);

    [McpServerTool]
    public Task<CodexStartTaskResponse> codex_start_task(
        string? profile,
        string? workflow,
        string? title,
        string? repo,
        string? prompt,
        string? model = null,
        string? effort = null,
        bool? fastMode = null,
        RequestContext<CallToolRequestParams>? requestContext = null,
        CancellationToken cancellationToken = default) =>
        service.StartTaskAsync(
            profile,
            workflow,
            title,
            repo,
            prompt,
            model,
            effort,
            fastMode,
            ResolveWakeSessionId(requestContext),
            cancellationToken);

    [McpServerTool]
    public Task<CodexStatusResponse> codex_status(
        string? jobId,
        bool wait = false,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default) =>
        service.StatusAsync(jobId, wait, timeoutSeconds, cancellationToken);

    [McpServerTool]
    public Task<CodexResultResponse> codex_result(
        string? jobId,
        string? detail = null,
        CancellationToken cancellationToken = default) =>
        service.ResultAsync(jobId, detail, cancellationToken);

    [McpServerTool]
    public Task<CodexReadOutputResponse> codex_read_output(
        string? jobId,
        string? threadId = null,
        string? turnId = null,
        string? agentId = null,
        int? offset = null,
        int? limit = null,
        string? format = null,
        CancellationToken cancellationToken = default) =>
        service.ReadOutputAsync(jobId, threadId, turnId, agentId, offset, limit, format, cancellationToken);

    [McpServerTool]
    public Task<CodexSendInputResponse> codex_send_input(
        string? jobId,
        string? prompt,
        string? model = null,
        string? effort = null,
        bool? fastMode = null,
        CancellationToken cancellationToken = default) =>
        service.SendInputAsync(jobId, prompt, model, effort, fastMode, cancellationToken);

    [McpServerTool]
    public Task<CodexQueueInputResponse> codex_queue_input(
        string? jobId,
        string? prompt,
        string? title = null,
        CancellationToken cancellationToken = default) =>
        service.QueueInputAsync(jobId, prompt, title, cancellationToken);

    [McpServerTool]
    public Task<CodexCancelQueuedInputResponse> codex_cancel_queued_input(
        string? jobId,
        string? queueItemId,
        CancellationToken cancellationToken = default) =>
        service.CancelQueuedInputAsync(jobId, queueItemId, cancellationToken);

    [McpServerTool]
    public Task<CodexCancelResponse> codex_cancel(
        string? jobId,
        CancellationToken cancellationToken = default) =>
        service.CancelAsync(jobId, cancellationToken);

    [McpServerTool]
    public Task<CodexUsageResponse> codex_usage(
        string? jobId,
        bool refresh = true,
        CancellationToken cancellationToken = default) =>
        service.UsageAsync(jobId, refresh, cancellationToken);

    [McpServerTool]
    public Task<CodexListJobsResponse> codex_list_jobs(
        int limit = 50,
        bool includeTerminal = true,
        CancellationToken cancellationToken = default) =>
        service.ListJobsAsync(limit, includeTerminal, cancellationToken);

    private static string? ResolveWakeSessionId(RequestContext<CallToolRequestParams>? requestContext)
    {
        var transportSessionId = NormalizeSessionId(
            requestContext?.JsonRpcRequest.Context?.RelatedTransport?.SessionId);
        if (!string.IsNullOrWhiteSpace(transportSessionId))
        {
            return transportSessionId;
        }

        var boundSessionId = ReadBoundClaudeSessionId();
        if (!string.IsNullOrWhiteSpace(boundSessionId))
        {
            return boundSessionId;
        }

        // Stdio transports are implicitly single-session and do not provide an MCP
        // transport session id. Claude Code still exposes its own session UUID to
        // subprocesses, which is the identity we need for same-session wake files.
        return NormalizeSessionId(Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID"))
            ?? NormalizeSessionId(Environment.GetEnvironmentVariable("CLAUDE_SESSION_ID"));
    }

    private static string? ReadBoundClaudeSessionId()
    {
        try
        {
            var userRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userRoot))
            {
                return null;
            }

            // Primary: the active Claude Stop hook updates this on every idle event.
            // For stdio MCP servers this is the most reliable same-session identity source.
            var currentSessionPath = Path.Combine(userRoot, ".codex-manager", "current-session-id.txt");
            if (File.Exists(currentSessionPath))
            {
                return NormalizeSessionId(File.ReadAllText(currentSessionPath));
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? NormalizeSessionId(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId)
            ? null
            : sessionId.Trim();
}
