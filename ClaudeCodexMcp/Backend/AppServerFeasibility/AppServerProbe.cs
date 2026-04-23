using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using ClaudeCodexMcp.Backend.AppServerProtocol.CSharp;

namespace ClaudeCodexMcp.Backend.AppServerFeasibility;

public sealed class AppServerProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AppServerProbeResult> RunStartTaskProbeAsync(
        AppServerProbeOptions options,
        CancellationToken cancellationToken = default)
    {
        var stdout = Channel.CreateUnbounded<string>();
        var stderr = Channel.CreateUnbounded<string>();
        using var process = StartCodexAppServer(options);
        _ = PumpLinesAsync(process.StandardOutput, stdout.Writer, cancellationToken);
        _ = PumpLinesAsync(process.StandardError, stderr.Writer, cancellationToken);

        var messages = new List<string>();
        var errors = new List<string>();

        try
        {
            await SendRequestAsync(process, 1, AppServerProtocolNames.Initialize, CreateInitializeParams(), cancellationToken);
            var initialize = await ReadResponseAsync(stdout.Reader, messages, 1, options.RequestTimeout, cancellationToken);
            var userAgent = TryGetString(initialize, "result", "userAgent");

            await SendRequestAsync(process, 2, AppServerProtocolNames.ThreadStart, CreateThreadStartParams(options), cancellationToken);
            var threadStart = await ReadResponseAsync(stdout.Reader, messages, 2, options.RequestTimeout, cancellationToken);
            var threadId = TryGetString(threadStart, "result", "thread", "id");
            if (string.IsNullOrWhiteSpace(threadId))
            {
                return new AppServerProbeResult(false, userAgent, null, null, false, false, false, false, null, messages, errors);
            }

            await SendRequestAsync(process, 3, AppServerProtocolNames.TurnStart, CreateTurnStartParams(threadId, options.Prompt), cancellationToken);
            var turnStart = await ReadResponseAsync(stdout.Reader, messages, 3, options.RequestTimeout, cancellationToken);
            var turnId = TryGetString(turnStart, "result", "turn", "id");

            var completed = await ObserveUntilCompletionAsync(stdout.Reader, messages, options.TurnTimeout, cancellationToken);

            await SendRequestAsync(process, 4, AppServerProtocolNames.ThreadRead, new AppServerThreadReadParams { ThreadId = threadId, IncludeTurns = true }, cancellationToken);
            var threadRead = await ReadResponseAsync(stdout.Reader, messages, 4, options.RequestTimeout, cancellationToken);
            var finalText = ExtractFirstAgentMessage(threadRead);

            await SendRequestAsync(process, 5, AppServerProtocolNames.AccountRateLimitsRead, null, cancellationToken);
            var rateLimits = await ReadResponseAsync(stdout.Reader, messages, 5, options.RequestTimeout, cancellationToken);
            var rateLimitRead = rateLimits.RootElement.TryGetProperty("result", out _);

            if (options.VerifyResume)
            {
                await SendRequestAsync(process, 6, AppServerProtocolNames.ThreadResume, CreateThreadResumeParams(threadId, options), cancellationToken);
                _ = await ReadResponseAsync(stdout.Reader, messages, 6, options.RequestTimeout, cancellationToken);
            }

            return new AppServerProbeResult(
                Initialized: true,
                UserAgent: userAgent,
                ThreadId: threadId,
                TurnId: turnId,
                TurnCompleted: completed,
                ThreadReadSucceeded: threadRead.RootElement.TryGetProperty("result", out _),
                TokenUsageObserved: messages.Any(message => message.Contains(AppServerProtocolNames.ThreadTokenUsageUpdated, StringComparison.Ordinal)),
                RateLimitsObserved: rateLimitRead || messages.Any(message => message.Contains(AppServerProtocolNames.AccountRateLimitsUpdated, StringComparison.Ordinal)),
                FinalOutput: finalText,
                Messages: messages,
                Errors: errors);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            while (stderr.Reader.TryRead(out var error))
            {
                errors.Add(error);
            }
        }
    }

    public static AppServerInitializeParams CreateInitializeParams() => new()
    {
        ClientInfo = new AppServerClientInfo
        {
            Name = "claude-codex-mcp-feasibility",
            Title = "Claude Codex MCP Feasibility",
            Version = "0.1.0"
        },
        Capabilities = new AppServerInitializeCapabilities
        {
            ExperimentalApi = true
        }
    };

    public static AppServerThreadStartParams CreateThreadStartParams(AppServerProbeOptions options) => new()
    {
        Cwd = options.WorkingDirectory,
        ApprovalPolicy = "never",
        ApprovalsReviewer = "user",
        Sandbox = "read-only",
        ExperimentalRawEvents = false,
        PersistExtendedHistory = true
    };

    public static AppServerTurnStartParams CreateTurnStartParams(string threadId, string prompt) => new()
    {
        ThreadId = threadId,
        Input = [AppServerUserInput.FromText(prompt)]
    };

    public static AppServerThreadResumeParams CreateThreadResumeParams(string threadId, AppServerProbeOptions options) => new()
    {
        ThreadId = threadId,
        Cwd = options.WorkingDirectory,
        ApprovalPolicy = "never",
        ApprovalsReviewer = "user",
        Sandbox = "read-only",
        PersistExtendedHistory = true
    };

    private static Process StartCodexAppServer(AppServerProbeOptions options)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.CodexExecutable,
                WorkingDirectory = options.WorkingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("app-server");
        process.Start();
        return process;
    }

    private static async Task PumpLinesAsync(
        TextReader reader,
        ChannelWriter<string> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                await writer.WriteAsync(line, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task SendRequestAsync(
        Process process,
        int id,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var request = new AppServerJsonRpcRequest(id, method, parameters);
        var json = JsonSerializer.Serialize(request, JsonOptions);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<JsonDocument> ReadResponseAsync(
        ChannelReader<string> reader,
        List<string> messages,
        int id,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        while (await reader.WaitToReadAsync(timeoutSource.Token))
        {
            while (reader.TryRead(out var line))
            {
                messages.Add(line);
                var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out var responseId) &&
                    responseId.ValueKind == JsonValueKind.Number &&
                    responseId.GetInt32() == id)
                {
                    return document;
                }

                document.Dispose();
            }
        }

        throw new TimeoutException($"Timed out waiting for app-server response id {id}.");
    }

    private static async Task<bool> ObserveUntilCompletionAsync(
        ChannelReader<string> reader,
        List<string> messages,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        while (await reader.WaitToReadAsync(timeoutSource.Token))
        {
            while (reader.TryRead(out var line))
            {
                messages.Add(line);
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("method", out var method))
                {
                    continue;
                }

                if (method.GetString() is AppServerProtocolNames.TurnCompleted or AppServerProtocolNames.Error)
                {
                    return method.GetString() == AppServerProtocolNames.TurnCompleted;
                }
            }
        }

        return false;
    }

    private static string? TryGetString(JsonDocument document, params string[] path)
    {
        var current = document.RootElement;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string? ExtractFirstAgentMessage(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("thread", out var thread) ||
            !thread.TryGetProperty("turns", out var turns))
        {
            return null;
        }

        foreach (var turn in turns.EnumerateArray())
        {
            if (!turn.TryGetProperty("items", out var items))
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) &&
                    type.GetString() == "agentMessage" &&
                    item.TryGetProperty("text", out var text))
                {
                    return text.GetString();
                }
            }
        }

        return null;
    }
}
