using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Storage;
using ClaudeCodexMcp.Workflows;

namespace ClaudeCodexMcp.Backend;

public sealed class CodexCliBackend : ICodexBackend
{
    private readonly OutputStore outputStore;
    private readonly ICodexCliProcessRunner processRunner;
    private readonly CodexCliBackendOptions options;
    private readonly ConcurrentDictionary<string, CodexBackendOutput> completedOutputs = new(StringComparer.Ordinal);

    public CodexCliBackend(
        OutputStore outputStore,
        ICodexCliProcessRunner? processRunner = null,
        CodexCliBackendOptions? options = null)
    {
        this.outputStore = outputStore;
        this.processRunner = processRunner ?? new DefaultCodexCliProcessRunner();
        this.options = options ?? new CodexCliBackendOptions();
    }

    public CodexBackendCapabilities Capabilities { get; } = new()
    {
        BackendId = "codex-cli",
        BackendKind = CodexBackendNames.Cli,
        SupportsStart = true,
        SupportsObserveStatus = false,
        SupportsStatusPolling = false,
        SupportsSendInput = false,
        SupportsCancel = false,
        SupportsReadFinalOutput = true,
        SupportsReadUsage = false,
        SupportsResume = false,
        DegradedCapabilities =
        [
            new(CodexBackendCapabilityNames.ObserveStatus, "CLI fallback has no app-server lifecycle notification stream."),
            new(CodexBackendCapabilityNames.PollStatus, "CLI fallback direct execution blocks until process exit and does not expose live status polling."),
            new(CodexBackendCapabilityNames.SendInput, "CLI fallback cannot send follow-up input to an existing turn."),
            new(CodexBackendCapabilityNames.Cancel, "CLI fallback cancellation is not reliable after direct execution has been dispatched.", Terminal: true),
            new(CodexBackendCapabilityNames.ReadUsage, "CLI fallback does not expose token context, account usage, or rate-limit windows."),
            new(CodexBackendCapabilityNames.Resume, "CLI fallback does not provide verified thread resume support."),
            new("clarification_prompts", "CLI fallback cannot surface structured clarification prompts; callers should treat this capability as unavailable.")
        ]
    };

    public async Task<CodexBackendStartResult> StartAsync(
        CodexBackendStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.JobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        if (!string.Equals(request.Workflow, CanonicalWorkflows.Direct, StringComparison.OrdinalIgnoreCase))
        {
            var unsupported = new CodexBackendStatus
            {
                State = JobState.Failed,
                BackendIds = new CodexBackendIds { SessionId = $"cli:{request.JobId}" },
                LastError = "CLI fallback supports only direct workflow execution."
            };
            await AppendAsync(request.JobId, unsupported.BackendIds, "error", unsupported.LastError, cancellationToken);
            return new CodexBackendStartResult { Status = unsupported };
        }

        var ids = new CodexBackendIds { SessionId = $"cli:{request.JobId}" };
        var beforeStatus = options.CaptureGitStatus
            ? await ReadGitStatusAsync(request.Repo, cancellationToken)
            : [];

        var lastMessagePath = Path.Combine(Path.GetTempPath(), "claude-codex-mcp-cli", $"{request.JobId}.last-message.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(lastMessagePath)!);
        var cliRequest = new CodexCliProcessRequest(
            options.CodexExecutablePath,
            CreateExecArguments(request, lastMessagePath),
            request.Repo,
            request.Prompt,
            options.ExecutionTimeout);

        await AppendAsync(request.JobId, ids, "info", "Starting Codex CLI fallback direct execution.", cancellationToken);
        var result = await processRunner.RunAsync(cliRequest, cancellationToken);
        await AppendProcessOutputAsync(request.JobId, ids, result, cancellationToken);

        var finalText = await ReadFinalTextAsync(lastMessagePath, result, cancellationToken);
        var afterStatus = options.CaptureGitStatus
            ? await ReadGitStatusAsync(request.Repo, cancellationToken)
            : [];
        var changedFiles = GetChangedFiles(beforeStatus, afterStatus);
        var testSummary = ExtractTestSummary(finalText, result.StandardOutput, result.StandardError);
        var output = new CodexBackendOutput
        {
            BackendIds = ids,
            FinalText = finalText,
            Summary = ToSummary(finalText),
            ChangedFiles = changedFiles,
            TestSummary = testSummary
        };
        completedOutputs[request.JobId] = output;

        if (!string.IsNullOrWhiteSpace(finalText))
        {
            await AppendAsync(request.JobId, ids, "info", finalText, cancellationToken, source: "codex-cli-final");
        }

        var state = result.ExitCode == 0 && !result.TimedOut ? JobState.Completed : JobState.Failed;
        return new CodexBackendStartResult
        {
            Status = new CodexBackendStatus
            {
                State = state,
                BackendIds = ids,
                Message = state == JobState.Completed ? "Codex CLI fallback completed." : "Codex CLI fallback failed.",
                ResultSummary = output.Summary,
                ChangedFiles = changedFiles,
                TestSummary = testSummary,
                LastError = state == JobState.Failed
                    ? CreateFailureMessage(result)
                    : null
            }
        };
    }

    public Task<CodexBackendStatus> ObserveStatusAsync(
        CodexBackendObserveRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CodexBackendStatus>(Unsupported(CodexBackendCapabilityNames.ObserveStatus));

    public Task<CodexBackendStatus> PollStatusAsync(
        CodexBackendObserveRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CodexBackendStatus>(Unsupported(CodexBackendCapabilityNames.PollStatus));

    public Task<CodexBackendStatus> SendInputAsync(
        CodexBackendSendInputRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CodexBackendStatus>(Unsupported(CodexBackendCapabilityNames.SendInput));

    public Task<CodexBackendStatus> CancelAsync(
        CodexBackendCancelRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CodexBackendStatus>(Unsupported(CodexBackendCapabilityNames.Cancel));

    public Task<CodexBackendOutput> ReadFinalOutputAsync(
        CodexBackendOutputRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(completedOutputs.TryGetValue(request.JobId, out var output)
            ? output
            : new CodexBackendOutput { BackendIds = request.BackendIds });
    }

    public Task<CodexBackendUsageSnapshot> ReadUsageAsync(
        CodexBackendUsageRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CodexBackendUsageSnapshot>(Unsupported(CodexBackendCapabilityNames.ReadUsage));

    public Task<CodexBackendStatus> ResumeAsync(
        CodexBackendResumeRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CodexBackendStatus>(Unsupported(CodexBackendCapabilityNames.Resume));

    private async Task<IReadOnlyList<string>> ReadGitStatusAsync(
        string repo,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(repo, ".git")))
        {
            return [];
        }

        try
        {
            var result = await processRunner.RunAsync(
                new CodexCliProcessRequest(
                    options.GitExecutablePath,
                    ["status", "--short", "--untracked-files=all"],
                    repo,
                    StandardInput: null,
                    Timeout: options.GitStatusTimeout),
                cancellationToken);
            return result.ExitCode == 0
                ? SplitGitStatusLines(result.StandardOutput)
                : [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> CreateExecArguments(
        CodexBackendStartRequest request,
        string lastMessagePath)
    {
        var arguments = new List<string>
        {
            "exec",
            "--cd",
            request.Repo,
            "--sandbox",
            request.LaunchPolicy.Sandbox,
            "--skip-git-repo-check",
            "--color",
            "never",
            "--output-last-message",
            lastMessagePath
        };

        if (!string.IsNullOrWhiteSpace(request.Options.Model))
        {
            arguments.Add("--model");
            arguments.Add(request.Options.Model);
        }

        if (!string.IsNullOrWhiteSpace(request.Options.Effort))
        {
            arguments.Add("--config");
            arguments.Add($"model_reasoning_effort=\"{request.Options.Effort}\"");
        }

        if (string.Equals(request.Options.ServiceTier, CodexServiceTiers.Fast, StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--config");
            arguments.Add("model_service_tier=\"fast\"");
        }

        arguments.Add("-");
        return arguments;
    }

    private static IReadOnlyList<string> GetChangedFiles(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after)
    {
        var beforeSet = before.ToHashSet(StringComparer.Ordinal);
        return after
            .Where(line => !beforeSet.Contains(line))
            .Select(ParseGitStatusPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ParseGitStatusPath(string line)
    {
        if (line.Length <= 3)
        {
            return line.Trim();
        }

        var path = line[3..].Trim();
        var renameMarker = path.IndexOf(" -> ", StringComparison.Ordinal);
        return renameMarker >= 0 ? path[(renameMarker + 4)..].Trim() : path;
    }

    private static async Task<string?> ReadFinalTextAsync(
        string lastMessagePath,
        CodexCliProcessResult result,
        CancellationToken cancellationToken)
    {
        if (File.Exists(lastMessagePath))
        {
            var fileText = await File.ReadAllTextAsync(lastMessagePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(fileText))
            {
                return fileText.Trim();
            }
        }

        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? null
            : result.StandardOutput.Trim();
    }

    private async Task AppendProcessOutputAsync(
        string jobId,
        CodexBackendIds ids,
        CodexCliProcessResult result,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            await AppendAsync(jobId, ids, "info", result.StandardOutput, cancellationToken, source: "codex-cli-stdout");
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            await AppendAsync(jobId, ids, "error", result.StandardError, cancellationToken, source: "codex-cli-stderr");
        }
    }

    private async Task AppendAsync(
        string jobId,
        CodexBackendIds ids,
        string level,
        string message,
        CancellationToken cancellationToken,
        string? source = null)
    {
        await outputStore.AppendAsync(new OutputLogEntry
        {
            JobId = jobId,
            ThreadId = ids.ThreadId,
            TurnId = ids.TurnId,
            Source = source ?? Capabilities.BackendId,
            Level = level,
            Message = message
        }, cancellationToken);
    }

    private static string? ExtractTestSummary(params string?[] texts)
    {
        foreach (var line in texts
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .SelectMany(text => SplitLines(text!))
            .Reverse())
        {
            if (line.Contains("test", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("passed", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("succeeded", StringComparison.OrdinalIgnoreCase)))
            {
                return ToSummary(line, 512);
            }
        }

        return null;
    }

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<string> SplitGitStatusLines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

    private static string? ToSummary(string? text, int maxCharacters = 2048)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= maxCharacters
            ? trimmed
            : trimmed[..maxCharacters] + " [truncated]";
    }

    private static NotSupportedException Unsupported(string capability) =>
        new($"Codex CLI fallback does not support {capability}.");

    private static string CreateFailureMessage(CodexCliProcessResult result)
    {
        if (result.TimedOut)
        {
            return "Codex CLI fallback timed out.";
        }

        var stderr = ToSummary(result.StandardError, 512);
        return string.IsNullOrWhiteSpace(stderr)
            ? $"Codex CLI fallback exited with code {result.ExitCode}."
            : $"Codex CLI fallback exited with code {result.ExitCode}: {stderr}";
    }
}

public sealed record CodexCliBackendOptions
{
    public string CodexExecutablePath { get; init; } = "codex";

    public string GitExecutablePath { get; init; } = "git";

    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan GitStatusTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public bool CaptureGitStatus { get; init; } = true;
}

public sealed record CodexCliProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string? StandardInput,
    TimeSpan Timeout);

public sealed record CodexCliProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false);

public interface ICodexCliProcessRunner
{
    Task<CodexCliProcessResult> RunAsync(
        CodexCliProcessRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class DefaultCodexCliProcessRunner : ICodexCliProcessRunner
{
    public async Task<CodexCliProcessResult> RunAsync(
        CodexCliProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);

        using var process = new Process();
        process.StartInfo.FileName = request.FileName;
        process.StartInfo.WorkingDirectory = request.WorkingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardInput = request.StandardInput is not null;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        foreach (var argument in request.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stdout.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stderr.AppendLine(args.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (request.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.Timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return new CodexCliProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new CodexCliProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
