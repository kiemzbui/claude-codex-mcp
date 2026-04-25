using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeCodexMcp.Backend.AppServerProtocol.CSharp;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Storage;
using ClaudeCodexMcp.Workflows;

namespace ClaudeCodexMcp.Backend;

public sealed class CodexAppServerBackend : ICodexBackend, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAppServerJsonRpcClientFactory clientFactory;
    private readonly OutputStore outputStore;
    private readonly CodexAppServerBackendOptions options;
    private readonly ConcurrentDictionary<string, IAppServerJsonRpcClient> clientsByThreadId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CodexBackendTokenUsage> tokenUsageByThreadId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CodexBackendRateLimits> rateLimitsByThreadId = new(StringComparer.Ordinal);

    public CodexAppServerBackend(
        IAppServerJsonRpcClientFactory clientFactory,
        OutputStore outputStore,
        CodexAppServerBackendOptions? options = null)
    {
        this.clientFactory = clientFactory;
        this.outputStore = outputStore;
        this.options = options ?? new CodexAppServerBackendOptions();
    }

    public CodexBackendCapabilities Capabilities { get; } = CodexBackendCapabilities.AppServer();

    public async Task<CodexBackendStartResult> StartAsync(
        CodexBackendStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.JobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var client = await clientFactory.CreateAsync(request.Repo, cancellationToken);
        await SendAndLogAsync(client, request.JobId, null, AppServerProtocolNames.Initialize, CreateInitializeParams(), cancellationToken);

        using var threadStart = await SendAndLogAsync(
            client,
            request.JobId,
            null,
            AppServerProtocolNames.ThreadStart,
            CreateThreadStartParams(request),
            cancellationToken);
        var threadId = TryGetString(threadStart, "result", "thread", "id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            await client.DisposeAsync();
            throw new InvalidOperationException("App-server thread/start response did not include a thread id.");
        }

        var sessionId = TryGetString(threadStart, "result", "thread", "path");
        clientsByThreadId[threadId] = client;

        using var turnStart = await SendAndLogAsync(
            client,
            request.JobId,
            new CodexBackendIds { ThreadId = threadId, SessionId = sessionId },
            AppServerProtocolNames.TurnStart,
            CreateTurnStartParams(threadId, request.Prompt, request.Options, request.LaunchPolicy),
            cancellationToken);
        var turnId = TryGetString(turnStart, "result", "turn", "id");
        var ids = new CodexBackendIds { ThreadId = threadId, TurnId = turnId, SessionId = sessionId };

        await AppendEventAsync(request.JobId, ids, "backend_started", "Codex app-server thread and turn started.", turnStart, cancellationToken);
        return new CodexBackendStartResult
        {
            Status = new CodexBackendStatus
            {
                State = JobState.Running,
                BackendIds = ids,
                Message = "Codex app-server turn started."
            }
        };
    }

    public async Task<CodexBackendStatus> ObserveStatusAsync(
        CodexBackendObserveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var status = new CodexBackendStatus { BackendIds = request.BackendIds, State = JobState.Running };
        var sawReadinessSignal = false;

        if (TryGetClient(request.BackendIds, out var client))
        {
            var drain = await DrainNotificationsAsync(client, request.JobId, status.BackendIds, options.NotificationDrainTimeout, cancellationToken);
            status = MergeStatus(status, drain.Status);
            sawReadinessSignal = drain.SawReadinessSignal;
        }

        if (status.State is JobState.Running &&
            !sawReadinessSignal &&
            !string.IsNullOrWhiteSpace(status.BackendIds.ThreadId) &&
            TryGetClient(status.BackendIds, out var waitingClient) &&
            options.ReadinessSignalTimeout > TimeSpan.Zero)
        {
            var readinessDrain = await DrainNotificationsAsync(waitingClient, request.JobId, status.BackendIds, options.ReadinessSignalTimeout, cancellationToken);
            status = MergeStatus(status, readinessDrain.Status);
            sawReadinessSignal |= readinessDrain.SawReadinessSignal;
        }

        if (status.State is JobState.Running && !string.IsNullOrWhiteSpace(status.BackendIds.ThreadId))
        {
            status = MergeStatus(status, await ReadThreadStatusWithRetryAsync(request.JobId, status.BackendIds, cancellationToken));
        }

        return status;
    }

    public async Task<CodexBackendStatus> PollStatusAsync(
        CodexBackendObserveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.BackendIds.ThreadId))
        {
            throw new CodexBackendThreadUnrecoverableException("A Codex thread id is required for status polling.");
        }

        return await ReadThreadStatusWithRetryAsync(request.JobId, request.BackendIds, cancellationToken);
    }

    public async Task<CodexBackendStatus> SendInputAsync(
        CodexBackendSendInputRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var threadId = RequireThreadId(request.BackendIds);
        var client = await GetOrResumeClientAsync(request.JobId, request.BackendIds, null, request.LaunchPolicy, cancellationToken);

        using var turnStart = await SendAndLogAsync(
            client,
            request.JobId,
            request.BackendIds,
            AppServerProtocolNames.TurnStart,
            CreateTurnStartParams(threadId, request.Prompt, request.Options, request.LaunchPolicy),
            cancellationToken);
        var turnId = TryGetString(turnStart, "result", "turn", "id");
        var ids = request.BackendIds with { TurnId = turnId ?? request.BackendIds.TurnId };

        await AppendEventAsync(request.JobId, ids, "backend_input_sent", "Follow-up input sent to Codex app-server.", turnStart, cancellationToken);
        return new CodexBackendStatus
        {
            State = JobState.Running,
            BackendIds = ids,
            Message = "Follow-up input sent."
        };
    }

    public async Task<CodexBackendStatus> CancelAsync(
        CodexBackendCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var threadId = RequireThreadId(request.BackendIds);
        var turnId = string.IsNullOrWhiteSpace(request.BackendIds.TurnId)
            ? throw new InvalidOperationException("A Codex turn id is required for cancellation.")
            : request.BackendIds.TurnId;
        var client = await GetOrResumeClientAsync(request.JobId, request.BackendIds, null, new CodexBackendLaunchPolicy(), cancellationToken);

        using var response = await SendAndLogAsync(
            client,
            request.JobId,
            request.BackendIds,
            AppServerProtocolNames.TurnInterrupt,
            new AppServerTurnInterruptParams { ThreadId = threadId, TurnId = turnId },
            cancellationToken);

        await AppendEventAsync(request.JobId, request.BackendIds, "backend_cancelled", "Interrupt requested for Codex app-server thread.", response, cancellationToken);
        return new CodexBackendStatus
        {
            State = JobState.Cancelled,
            BackendIds = request.BackendIds,
            Message = "Cancellation requested."
        };
    }

    public async Task<CodexBackendOutput> ReadFinalOutputAsync(
        CodexBackendOutputRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var read = await ReadThreadAsync(request.JobId, request.BackendIds, cancellationToken);
        try
        {
            var finalText = ExtractLastAgentMessage(read.Document);
            await AppendEventAsync(request.JobId, read.Ids, "backend_output_read", "Final output read from Codex app-server thread.", read.Document, cancellationToken);
            return new CodexBackendOutput
            {
                BackendIds = read.Ids,
                FinalText = finalText,
                Summary = finalText
            };
        }
        finally
        {
            read.Document.Dispose();
        }
    }

    public async Task<CodexBackendUsageSnapshot> ReadUsageAsync(
        CodexBackendUsageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (TryGetClient(request.BackendIds, out var client))
        {
            if (options.UsageSettleTimeout > TimeSpan.Zero)
            {
                await DrainNotificationsAsync(client, request.JobId, request.BackendIds, options.UsageSettleTimeout, cancellationToken);
            }

            using var response = await SendAndLogAsync(
                client,
                request.JobId,
                request.BackendIds,
                AppServerProtocolNames.AccountRateLimitsRead,
                null,
                cancellationToken);
            var rateLimits = ParseRateLimits(response);
            if (rateLimits is not null && request.BackendIds.ThreadId is not null)
            {
                rateLimitsByThreadId[request.BackendIds.ThreadId] = rateLimits;
            }
        }

        return new CodexBackendUsageSnapshot
        {
            TokenUsage = request.BackendIds.ThreadId is not null && tokenUsageByThreadId.TryGetValue(request.BackendIds.ThreadId, out var tokenUsage)
                ? tokenUsage
                : null,
            RateLimits = request.BackendIds.ThreadId is not null && rateLimitsByThreadId.TryGetValue(request.BackendIds.ThreadId, out var cachedRateLimits)
                ? cachedRateLimits
                : null
        };
    }

    public async Task<CodexBackendStatus> ResumeAsync(
        CodexBackendResumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var client = await GetOrResumeClientAsync(request.JobId, request.BackendIds, request.Repo, request.LaunchPolicy, cancellationToken);
        var read = await ReadThreadAsync(request.JobId, request.BackendIds, cancellationToken);
        try
        {
            var status = MapThreadRead(read.Document, read.Ids);
            await AppendEventAsync(request.JobId, read.Ids, "backend_resumed", "Codex app-server thread resumed and read.", read.Document, cancellationToken);
            _ = client;
            return status;
        }
        finally
        {
            read.Document.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in clientsByThreadId)
        {
            await pair.Value.DisposeAsync();
        }

        clientsByThreadId.Clear();
    }

    private static AppServerInitializeParams CreateInitializeParams() => new()
    {
        ClientInfo = new AppServerClientInfo
        {
            Name = "claude-codex-mcp",
            Title = "Claude Codex MCP",
            Version = "0.1.0"
        },
        Capabilities = new AppServerInitializeCapabilities
        {
            ExperimentalApi = true
        }
    };

    private static string? ToAppServerServiceTier(string? serviceTier) =>
        string.Equals(serviceTier, CodexServiceTiers.Fast, StringComparison.OrdinalIgnoreCase)
            ? CodexServiceTiers.Fast
            : null;

    private static AppServerThreadStartParams CreateThreadStartParams(CodexBackendStartRequest request) => new()
    {
        Model = request.Options.Model,
        ServiceTier = ToAppServerServiceTier(request.Options.ServiceTier),
        Cwd = request.Repo,
        ApprovalPolicy = request.LaunchPolicy.ApprovalPolicy,
        ApprovalsReviewer = request.LaunchPolicy.ApprovalsReviewer,
        Sandbox = request.LaunchPolicy.Sandbox,
        BaseInstructions = request.TaskPrefix,
        PersistExtendedHistory = true
    };

    private static AppServerTurnStartParams CreateTurnStartParams(
        string threadId,
        string prompt,
        CodexBackendDispatchOptions options,
        CodexBackendLaunchPolicy launchPolicy) => new()
    {
        ThreadId = threadId,
        Input = [AppServerUserInput.FromText(prompt)],
        ApprovalPolicy = launchPolicy.ApprovalPolicy,
        ApprovalsReviewer = launchPolicy.ApprovalsReviewer,
        Model = options.Model,
        Effort = options.Effort,
        ServiceTier = ToAppServerServiceTier(options.ServiceTier)
    };

    private static AppServerThreadResumeParams CreateThreadResumeParams(
        string threadId,
        string? repo,
        CodexBackendLaunchPolicy launchPolicy) => new()
    {
        ThreadId = threadId,
        Cwd = repo,
        ApprovalPolicy = launchPolicy.ApprovalPolicy,
        ApprovalsReviewer = launchPolicy.ApprovalsReviewer,
        Sandbox = launchPolicy.Sandbox,
        PersistExtendedHistory = true
    };

    private async Task<JsonDocument> SendAndLogAsync(
        IAppServerJsonRpcClient client,
        string jobId,
        CodexBackendIds? ids,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        await outputStore.AppendAsync(new OutputLogEntry
        {
            JobId = jobId,
            ThreadId = ids?.ThreadId,
            TurnId = ids?.TurnId,
            Source = Capabilities.BackendId,
            Level = "debug",
            Message = $"app-server request {method}",
            PayloadJson = JsonSerializer.Serialize(new { method, parameters }, JsonOptions)
        }, cancellationToken);

        var response = await client.SendRequestAsync(method, parameters, cancellationToken);
        await AppendEventAsync(jobId, ids, "backend_response", $"app-server response {method}", response, cancellationToken);
        return response;
    }

    private async Task AppendEventAsync(
        string jobId,
        CodexBackendIds? ids,
        string eventName,
        string message,
        JsonDocument? document,
        CancellationToken cancellationToken)
    {
        await outputStore.AppendAsync(new OutputLogEntry
        {
            JobId = jobId,
            ThreadId = ids?.ThreadId,
            TurnId = ids?.TurnId,
            Source = Capabilities.BackendId,
            Level = eventName.Contains("error", StringComparison.OrdinalIgnoreCase) ? "error" : "info",
            Message = message,
            PayloadJson = document?.RootElement.GetRawText()
        }, cancellationToken);
    }

    private bool TryGetClient(CodexBackendIds ids, out IAppServerJsonRpcClient client)
    {
        client = null!;
        return ids.ThreadId is not null && clientsByThreadId.TryGetValue(ids.ThreadId, out client!);
    }

    private async Task<IAppServerJsonRpcClient> GetOrResumeClientAsync(
        string jobId,
        CodexBackendIds ids,
        string? repo,
        CodexBackendLaunchPolicy launchPolicy,
        CancellationToken cancellationToken)
    {
        if (TryGetClient(ids, out var existing))
        {
            return existing;
        }

        var threadId = RequireThreadId(ids);
        var client = await clientFactory.CreateAsync(repo ?? Environment.CurrentDirectory, cancellationToken);
        await SendAndLogAsync(client, jobId, ids, AppServerProtocolNames.Initialize, CreateInitializeParams(), cancellationToken);
        using var resume = await SendAndLogAsync(
            client,
            jobId,
            ids,
            AppServerProtocolNames.ThreadResume,
            CreateThreadResumeParams(threadId, repo, launchPolicy),
            cancellationToken);
        clientsByThreadId[threadId] = client;
        return client;
    }

    private async Task<(JsonDocument Document, CodexBackendIds Ids)> ReadThreadAsync(
        string jobId,
        CodexBackendIds ids,
        CancellationToken cancellationToken)
    {
        var threadId = RequireThreadId(ids);
        var client = await GetOrResumeClientAsync(jobId, ids, null, new CodexBackendLaunchPolicy(), cancellationToken);
        var response = await SendAndLogAsync(
            client,
            jobId,
            ids,
            AppServerProtocolNames.ThreadRead,
            new AppServerThreadReadParams { ThreadId = threadId, IncludeTurns = true },
            cancellationToken);
        var sessionId = TryGetString(response, "result", "thread", "path") ?? ids.SessionId;
        return (response, ids with { SessionId = sessionId });
    }

    private async Task<(CodexBackendStatus Status, bool SawReadinessSignal)> DrainNotificationsAsync(
        IAppServerJsonRpcClient client,
        string jobId,
        CodexBackendIds ids,
        TimeSpan quietPeriod,
        CancellationToken cancellationToken)
    {
        var status = new CodexBackendStatus { BackendIds = ids, State = JobState.Running };
        var sawReadinessSignal = false;

        await foreach (var notification in client.ReadAvailableNotificationsAsync(quietPeriod, cancellationToken))
        {
            using (notification)
            {
                await AppendEventAsync(jobId, status.BackendIds, "backend_notification", "App-server notification received.", notification, cancellationToken);
                sawReadinessSignal |= IsReadinessSignal(notification);
                status = MergeStatus(status, MapNotification(notification, status.BackendIds));
                CaptureUsage(notification, status.BackendIds.ThreadId);
            }
        }

        return (status, sawReadinessSignal);
    }

    private async Task<CodexBackendStatus> ReadThreadStatusWithRetryAsync(
        string jobId,
        CodexBackendIds ids,
        CancellationToken cancellationToken)
    {
        var status = new CodexBackendStatus { BackendIds = ids, State = JobState.Running };
        var maxAttempts = Math.Max(1, options.ThreadReadMaxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var read = await ReadThreadAsync(jobId, status.BackendIds, cancellationToken);
            CodexBackendStatus readStatus;
            try
            {
                readStatus = MapThreadRead(read.Document, read.Ids);
            }
            finally
            {
                read.Document.Dispose();
            }

            status = MergeStatus(status, readStatus);
            if (!IsTransientThreadReadStatus(readStatus) || attempt == maxAttempts || status.State is not JobState.Running)
            {
                return status;
            }

            if (options.ThreadReadRetryDelay <= TimeSpan.Zero)
            {
                continue;
            }

            if (TryGetClient(status.BackendIds, out var client))
            {
                var retryDrain = await DrainNotificationsAsync(client, jobId, status.BackendIds, options.ThreadReadRetryDelay, cancellationToken);
                status = MergeStatus(status, retryDrain.Status);
                if (status.State is not JobState.Running)
                {
                    return status;
                }
            }
            else
            {
                await Task.Delay(options.ThreadReadRetryDelay, cancellationToken);
            }
        }

        return status;
    }

    private static CodexBackendStatus MergeStatus(CodexBackendStatus current, CodexBackendStatus next)
    {
        if (next.State is JobState.Completed or JobState.Failed or JobState.Cancelled or JobState.WaitingForInput)
        {
            return next;
        }

        return current.State is JobState.Running ? next : current;
    }

    private static CodexBackendStatus MapNotification(JsonDocument document, CodexBackendIds fallbackIds)
    {
        var root = document.RootElement;
        if (!root.TryGetProperty("method", out var methodElement))
        {
            return new CodexBackendStatus { State = JobState.Running, BackendIds = fallbackIds };
        }

        var method = methodElement.GetString();
        var ids = fallbackIds with
        {
            ThreadId = TryGetString(document, "params", "threadId") ?? fallbackIds.ThreadId,
            TurnId = TryGetString(document, "params", "turnId") ?? fallbackIds.TurnId
        };

        return method switch
        {
            AppServerProtocolNames.TurnCompleted => MapTurnCompletedNotification(root, ids),
            AppServerProtocolNames.Error => new CodexBackendStatus { State = JobState.Failed, BackendIds = ids, LastError = TryGetString(document, "params", "message") ?? "App-server error." },
            AppServerProtocolNames.ThreadStatusChanged => MapThreadStatus(root.GetProperty("params").GetProperty("status"), ids),
            _ => new CodexBackendStatus { State = JobState.Running, BackendIds = ids }
        };
    }

    private static CodexBackendStatus MapThreadRead(JsonDocument document, CodexBackendIds fallbackIds)
    {
        if (TryGetJsonRpcErrorMessage(document.RootElement) is { } errorMessage)
        {
            return IsTransientThreadReadError(errorMessage)
                ? new CodexBackendStatus
                {
                    State = JobState.Running,
                    BackendIds = fallbackIds,
                    Message = errorMessage
                }
                : new CodexBackendStatus
                {
                    State = JobState.Failed,
                    BackendIds = fallbackIds,
                    LastError = errorMessage
                };
        }

        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("thread", out var thread))
        {
            return new CodexBackendStatus
            {
                State = JobState.Failed,
                BackendIds = fallbackIds,
                LastError = "thread/read response did not include a thread."
            };
        }

        var ids = fallbackIds with
        {
            ThreadId = TryGetString(thread, "id") ?? fallbackIds.ThreadId,
            SessionId = TryGetString(thread, "path") ?? fallbackIds.SessionId
        };

        if (thread.TryGetProperty("turns", out var turns))
        {
            var lastTurn = turns.EnumerateArray().LastOrDefault();
            if (lastTurn.ValueKind is not JsonValueKind.Undefined)
            {
                ids = ids with { TurnId = TryGetString(lastTurn, "id") ?? ids.TurnId };
                if (lastTurn.TryGetProperty("status", out var turnStatus))
                {
                    return new CodexBackendStatus
                    {
                        State = MapTurnStatus(turnStatus.GetString()),
                        BackendIds = ids
                    };
                }
            }
        }

        if (thread.TryGetProperty("status", out var status))
        {
            return MapThreadStatus(status, ids);
        }

        return new CodexBackendStatus { State = JobState.Running, BackendIds = ids };
    }

    private static CodexBackendStatus MapThreadStatus(JsonElement status, CodexBackendIds ids)
    {
        var type = TryGetString(status, "type");
        if (string.Equals(type, "active", StringComparison.OrdinalIgnoreCase))
        {
            if (status.TryGetProperty("activeFlags", out var activeFlags) &&
                activeFlags.ValueKind is JsonValueKind.Array &&
                activeFlags.EnumerateArray().Any(flag => string.Equals(flag.GetString(), "waitingOnUserInput", StringComparison.OrdinalIgnoreCase)))
            {
                return new CodexBackendStatus
                {
                    State = JobState.WaitingForInput,
                    BackendIds = ids,
                    WaitingForInput = new WaitingForInputRecord
                    {
                        Prompt = "Codex is waiting for user input."
                    }
                };
            }

            return new CodexBackendStatus { State = JobState.Running, BackendIds = ids };
        }

        return type switch
        {
            "idle" => new CodexBackendStatus { State = JobState.Running, BackendIds = ids },
            "systemError" => new CodexBackendStatus { State = JobState.Failed, BackendIds = ids, LastError = "Codex app-server reported a system error." },
            "notLoaded" => new CodexBackendStatus { State = JobState.Failed, BackendIds = ids, LastError = "Codex thread is not loaded." },
            _ => new CodexBackendStatus { State = JobState.Running, BackendIds = ids }
        };
    }

    private static JobState MapTurnStatus(string? status) => status switch
    {
        "completed" => JobState.Completed,
        "failed" => JobState.Failed,
        "interrupted" => JobState.Cancelled,
        _ => JobState.Running
    };

    private static CodexBackendStatus MapTurnCompletedNotification(JsonElement root, CodexBackendIds ids)
    {
        if (!root.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("turn", out var turn) ||
            turn.ValueKind is not JsonValueKind.Object)
        {
            return new CodexBackendStatus
            {
                State = JobState.Completed,
                BackendIds = ids,
                Message = "Turn completed."
            };
        }

        var status = MapTurnStatus(TryGetString(turn, "status"));
        return new CodexBackendStatus
        {
            State = status,
            BackendIds = ids with { TurnId = TryGetString(turn, "id") ?? ids.TurnId },
            Message = status is JobState.Completed ? "Turn completed." : null,
            LastError = status is JobState.Failed
                ? TryGetString(turn, "error", "message") ?? "Codex turn failed."
                : null
        };
    }

    private static bool IsReadinessSignal(JsonDocument document) =>
        TryGetString(document, "method") is AppServerProtocolNames.ThreadStatusChanged
            or AppServerProtocolNames.TurnStarted
            or AppServerProtocolNames.ItemStarted
            or AppServerProtocolNames.ItemAgentMessageDelta
            or AppServerProtocolNames.TurnCompleted;

    private static bool IsTransientThreadReadError(string errorMessage) =>
        IsEmptyRolloutThreadReadError(errorMessage) ||
        IsThreadNotMaterializedThreadReadError(errorMessage);

    private static bool IsEmptyRolloutThreadReadError(string errorMessage) =>
        errorMessage.Contains("rollout", StringComparison.OrdinalIgnoreCase) &&
        errorMessage.Contains("empty", StringComparison.OrdinalIgnoreCase);

    private static bool IsThreadNotMaterializedThreadReadError(string errorMessage) =>
        errorMessage.Contains("not materialized yet", StringComparison.OrdinalIgnoreCase) ||
        (errorMessage.Contains("includeTurns", StringComparison.OrdinalIgnoreCase) &&
         errorMessage.Contains("before first user message", StringComparison.OrdinalIgnoreCase));

    private static bool IsTransientThreadReadStatus(CodexBackendStatus status) =>
        status.State is JobState.Running &&
        !string.IsNullOrWhiteSpace(status.Message) &&
        IsTransientThreadReadError(status.Message);

    private static string? TryGetJsonRpcErrorMessage(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object ||
            !root.TryGetProperty("error", out var error) ||
            error.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        return TryGetString(error, "message") ?? "App-server thread/read failed.";
    }

    private void CaptureUsage(JsonDocument document, string? fallbackThreadId)
    {
        var method = TryGetString(document, "method");
        var threadId = TryGetString(document, "params", "threadId") ?? fallbackThreadId;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        if (method == AppServerProtocolNames.ThreadTokenUsageUpdated &&
            document.RootElement.TryGetProperty("params", out var tokenParams) &&
            tokenParams.TryGetProperty("tokenUsage", out var tokenUsage))
        {
            tokenUsageByThreadId[threadId] = ParseTokenUsage(tokenUsage);
        }
        else if (method == AppServerProtocolNames.AccountRateLimitsUpdated)
        {
            var rateLimits = ParseRateLimits(document);
            if (rateLimits is not null)
            {
                rateLimitsByThreadId[threadId] = rateLimits;
            }
        }
    }

    private static CodexBackendTokenUsage ParseTokenUsage(JsonElement tokenUsage)
    {
        var total = tokenUsage.TryGetProperty("total", out var totalElement) ? totalElement : default;
        return new CodexBackendTokenUsage
        {
            TotalTokens = TryGetInt(total, "totalTokens"),
            InputTokens = TryGetInt(total, "inputTokens"),
            OutputTokens = TryGetInt(total, "outputTokens"),
            ReasoningOutputTokens = TryGetInt(total, "reasoningOutputTokens"),
            ContextWindowTokens = TryGetInt(tokenUsage, "modelContextWindow")
        };
    }

    private static CodexBackendRateLimits? ParseRateLimits(JsonDocument document)
    {
        if (document.RootElement.TryGetProperty("result", out var result) &&
            result.TryGetProperty("rateLimits", out var resultRateLimits))
        {
            return ParseRateLimits(resultRateLimits);
        }

        if (document.RootElement.TryGetProperty("params", out var parameters) &&
            parameters.TryGetProperty("rateLimits", out var notificationRateLimits))
        {
            return ParseRateLimits(notificationRateLimits);
        }

        return null;
    }

    private static CodexBackendRateLimits ParseRateLimits(JsonElement rateLimits) => new()
    {
        LimitId = TryGetString(rateLimits, "limitId"),
        Primary = rateLimits.TryGetProperty("primary", out var primary) && primary.ValueKind is JsonValueKind.Object
            ? ParseRateLimitWindow(primary)
            : null,
        Secondary = rateLimits.TryGetProperty("secondary", out var secondary) && secondary.ValueKind is JsonValueKind.Object
            ? ParseRateLimitWindow(secondary)
            : null
    };

    private static CodexBackendRateLimitWindow ParseRateLimitWindow(JsonElement window) => new()
    {
        UsedPercent = TryGetDouble(window, "usedPercent"),
        WindowDurationMinutes = TryGetInt(window, "windowDurationMins"),
        ResetsAt = TryGetLong(window, "resetsAt") is { } unixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : null
    };

    private static string? ExtractLastAgentMessage(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("thread", out var thread) ||
            !thread.TryGetProperty("turns", out var turns))
        {
            return null;
        }

        string? finalText = null;
        foreach (var turn in turns.EnumerateArray())
        {
            if (!turn.TryGetProperty("items", out var items))
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (TryGetString(item, "type") == "agentMessage")
                {
                    finalText = TryGetString(item, "text") ?? finalText;
                }
            }
        }

        return finalText;
    }

    private static string RequireThreadId(CodexBackendIds ids) =>
        string.IsNullOrWhiteSpace(ids.ThreadId)
            ? throw new InvalidOperationException("A Codex thread id is required for this backend operation.")
            : ids.ThreadId;

    private static string? TryGetString(JsonDocument document, params string[] path) =>
        TryGetString(document.RootElement, path);

    private static string? TryGetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind is not JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind is JsonValueKind.String ? current.GetString() : null;
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind is not JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not JsonValueKind.Number)
        {
            return null;
        }

        return property.TryGetInt32(out var value) ? value : null;
    }

    private static long? TryGetLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind is not JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not JsonValueKind.Number)
        {
            return null;
        }

        return property.TryGetInt64(out var value) ? value : null;
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind is not JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not JsonValueKind.Number)
        {
            return null;
        }

        return property.TryGetDouble(out var value) ? value : null;
    }
}
