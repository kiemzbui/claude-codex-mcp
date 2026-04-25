using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using ClaudeCodexMcp.Backend.AppServerProtocol.CSharp;

namespace ClaudeCodexMcp.Backend;

public interface IAppServerJsonRpcClient : IAsyncDisposable
{
    Task<JsonDocument> SendRequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<JsonDocument> ReadAvailableNotificationsAsync(
        TimeSpan quietPeriod,
        CancellationToken cancellationToken = default);
}

public interface IAppServerJsonRpcClientFactory
{
    Task<IAppServerJsonRpcClient> CreateAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default);
}

public sealed record CodexAppServerBackendOptions
{
    public string CodexExecutable { get; init; } = "codex";

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan NotificationDrainTimeout { get; init; } = TimeSpan.FromMilliseconds(50);

    public TimeSpan ReadinessSignalTimeout { get; init; } = TimeSpan.FromMilliseconds(200);

    public int ThreadReadMaxAttempts { get; init; } = 6;

    public TimeSpan ThreadReadRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}

public sealed class CodexAppServerProcessClientFactory : IAppServerJsonRpcClientFactory
{
    private readonly CodexAppServerBackendOptions options;

    public CodexAppServerProcessClientFactory(CodexAppServerBackendOptions? options = null)
    {
        this.options = options ?? new CodexAppServerBackendOptions();
    }

    public Task<IAppServerJsonRpcClient> CreateAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var client = new ProcessAppServerJsonRpcClient(options, workingDirectory);
        return Task.FromResult<IAppServerJsonRpcClient>(client);
    }
}

public sealed class ProcessAppServerJsonRpcClient : IAppServerJsonRpcClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CodexAppServerBackendOptions options;
    private readonly Process process;
    private readonly Channel<string> stdout = Channel.CreateUnbounded<string>();
    private readonly Channel<JsonDocument> notifications = Channel.CreateUnbounded<JsonDocument>();
    private readonly ConcurrentQueue<string> stderr = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private int nextId;

    public ProcessAppServerJsonRpcClient(
        CodexAppServerBackendOptions options,
        string workingDirectory)
    {
        this.options = options;
        process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.CodexExecutable,
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("app-server");
        process.Start();

        _ = PumpLinesAsync(process.StandardOutput, stdout.Writer, CancellationToken.None);
        _ = PumpStderrAsync(process.StandardError, CancellationToken.None);
    }

    public async Task<JsonDocument> SendRequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            var id = Interlocked.Increment(ref nextId);
            var request = new AppServerJsonRpcRequest(id, method, parameters);
            var json = JsonSerializer.Serialize(request, JsonOptions);
            await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(options.RequestTimeout);

            while (await stdout.Reader.WaitToReadAsync(timeoutSource.Token))
            {
                while (stdout.Reader.TryRead(out var line))
                {
                    var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("id", out var responseId) &&
                        responseId.ValueKind == JsonValueKind.Number &&
                        responseId.GetInt32() == id)
                    {
                        return document;
                    }

                    await notifications.Writer.WriteAsync(document, timeoutSource.Token);
                }
            }

            throw new InvalidOperationException($"App-server exited before response for '{method}' was read.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for app-server response to '{method}'.");
        }
        finally
        {
            sendLock.Release();
        }
    }

    public async IAsyncEnumerable<JsonDocument> ReadAvailableNotificationsAsync(
        TimeSpan quietPeriod,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(quietPeriod);

        while (!timeoutSource.IsCancellationRequested)
        {
            while (notifications.Reader.TryRead(out var document))
            {
                yield return document;
            }

            try
            {
                if (!await notifications.Reader.WaitToReadAsync(timeoutSource.Token))
                {
                    yield break;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        sendLock.Dispose();
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync();
        process.Dispose();
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

    private async Task PumpStderrAsync(TextReader reader, CancellationToken cancellationToken)
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

                stderr.Enqueue(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
