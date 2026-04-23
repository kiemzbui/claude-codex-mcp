using ClaudeCodexMcp.Backend;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Storage;
using ClaudeCodexMcp.Usage;
using ClaudeCodexMcp.Workflows;

namespace ClaudeCodexMcp.Tests.Backend;

public sealed class CodexCliBackendTests
{
    [Fact]
    public void CapabilitiesReportDegradedCliFallbackAndUnknownUsageStatusline()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var backend = CreateBackend(workspace, new RecordingCliRunner());

        Assert.Equal(CodexBackendNames.Cli, backend.Capabilities.BackendKind);
        Assert.True(backend.Capabilities.SupportsStart);
        Assert.True(backend.Capabilities.SupportsReadFinalOutput);
        Assert.False(backend.Capabilities.SupportsObserveStatus);
        Assert.False(backend.Capabilities.SupportsStatusPolling);
        Assert.False(backend.Capabilities.SupportsSendInput);
        Assert.False(backend.Capabilities.SupportsReadUsage);
        Assert.False(backend.Capabilities.SupportsResume);
        Assert.Contains(backend.Capabilities.DegradedCapabilities, gap => gap.Capability == CodexBackendCapabilityNames.ReadUsage);
        Assert.Contains(backend.Capabilities.DegradedCapabilities, gap => gap.Capability == "clarification_prompts");
        Assert.Equal(UsageReporter.UnknownStatusline, new UsageReporter().CreateStatusline(null));
    }

    [Fact]
    public async Task DirectExecutionMapsCliProcessOutputAndChangedFiles()
    {
        using var workspace = TemporaryStateWorkspace.Create(gitRepo: true);
        var runner = new RecordingCliRunner();
        runner.EnqueueGit("");
        runner.EnqueueCodex("CLI stdout\nTests: 2 passed", "", 0, "FINAL_FROM_CLI\nTests: 2 passed");
        runner.EnqueueGit(" M src/File.cs\n?? tests/NewTest.cs");
        var backend = CreateBackend(workspace, runner);

        var result = await backend.StartAsync(new CodexBackendStartRequest
        {
            JobId = "job_cli_direct",
            Title = "CLI direct",
            Repo = workspace.Repo,
            Workflow = CanonicalWorkflows.Direct,
            Prompt = "Do the task.",
            LaunchPolicy = new CodexBackendLaunchPolicy { Sandbox = "workspace-write" },
            Options = new CodexBackendDispatchOptions
            {
                Model = "gpt-5.4-codex",
                Effort = "high",
                ServiceTier = CodexServiceTiers.Fast
            }
        });

        Assert.Equal(JobState.Completed, result.Status.State);
        Assert.Equal("cli:job_cli_direct", result.BackendIds.SessionId);
        Assert.Equal("FINAL_FROM_CLI\nTests: 2 passed", result.Status.ResultSummary);
        Assert.Equal(["src/File.cs", "tests/NewTest.cs"], result.Status.ChangedFiles);
        Assert.Equal("Tests: 2 passed", result.Status.TestSummary);

        var codexRequest = Assert.Single(runner.Requests, request => request.FileName == "codex");
        Assert.Equal(workspace.Repo, codexRequest.WorkingDirectory);
        Assert.Equal("Do the task.", codexRequest.StandardInput);
        Assert.Contains("exec", codexRequest.Arguments);
        Assert.Contains("--output-last-message", codexRequest.Arguments);
        Assert.Contains("gpt-5.4-codex", codexRequest.Arguments);
        Assert.Contains("model_reasoning_effort=\"high\"", codexRequest.Arguments);
        Assert.Contains("model_service_tier=\"fast\"", codexRequest.Arguments);

        var output = await backend.ReadFinalOutputAsync(new CodexBackendOutputRequest
        {
            JobId = "job_cli_direct",
            BackendIds = result.BackendIds
        });
        Assert.Equal("FINAL_FROM_CLI\nTests: 2 passed", output.FinalText);
        Assert.Equal(["src/File.cs", "tests/NewTest.cs"], output.ChangedFiles);

        var page = await new OutputStore(new ManagerStatePaths(workspace.StateDirectory)).ReadAsync("job_cli_direct", limit: 20);
        Assert.Contains(page.Entries, entry => entry.Source == "codex-cli-stdout" && entry.Message.Contains("CLI stdout", StringComparison.Ordinal));
        Assert.Contains(page.Entries, entry => entry.Source == "codex-cli-final" && entry.Message.Contains("FINAL_FROM_CLI", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsupportedWorkflowAndBackendFeaturesAreExplicit()
    {
        using var workspace = TemporaryStateWorkspace.Create();
        var backend = CreateBackend(workspace, new RecordingCliRunner());

        var start = await backend.StartAsync(new CodexBackendStartRequest
        {
            JobId = "job_cli_unsupported",
            Title = "Unsupported",
            Repo = workspace.Repo,
            Workflow = CanonicalWorkflows.OrchestrateExecute,
            Prompt = "Run managed workflow."
        });

        Assert.Equal(JobState.Failed, start.Status.State);
        Assert.Contains("direct workflow", start.Status.LastError);
        await Assert.ThrowsAsync<NotSupportedException>(() => backend.SendInputAsync(new CodexBackendSendInputRequest
        {
            JobId = "job_cli_unsupported",
            BackendIds = start.BackendIds,
            Prompt = "follow up"
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => backend.ReadUsageAsync(new CodexBackendUsageRequest
        {
            JobId = "job_cli_unsupported",
            BackendIds = start.BackendIds
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => backend.ResumeAsync(new CodexBackendResumeRequest
        {
            JobId = "job_cli_unsupported",
            Repo = workspace.Repo,
            BackendIds = start.BackendIds
        }));
    }

    [Fact]
    public void SelectionPolicyAllowsCliOnlyWhenProfileRequestsCli()
    {
        Assert.Equal(CodexBackendNames.AppServer, CodexCliBackendSelection.ResolveBackendKind(null, appServerAvailable: true));
        Assert.Equal(CodexBackendNames.AppServer, CodexCliBackendSelection.ResolveBackendKind("appServer", appServerAvailable: true));
        Assert.Equal(CodexBackendNames.Cli, CodexCliBackendSelection.ResolveBackendKind("cli", appServerAvailable: true));
        Assert.Equal(CodexBackendNames.Cli, CodexCliBackendSelection.ResolveBackendKind("cli-fallback", appServerAvailable: false));
        Assert.False(CodexCliBackendSelection.IsCliAllowedByProfile(null));
        Assert.False(CodexCliBackendSelection.IsCliAllowedByProfile("appServer"));
        Assert.True(CodexCliBackendSelection.IsCliAllowedByProfile("cliFallback"));
        Assert.Throws<InvalidOperationException>(() => CodexCliBackendSelection.ResolveBackendKind(null, appServerAvailable: false));
        Assert.Throws<InvalidOperationException>(() => CodexCliBackendSelection.ResolveBackendKind("appServer", appServerAvailable: false));
    }

    private static CodexCliBackend CreateBackend(TemporaryStateWorkspace workspace, RecordingCliRunner runner) =>
        new(
            new OutputStore(new ManagerStatePaths(workspace.StateDirectory)),
            runner,
            new CodexCliBackendOptions
            {
                CodexExecutablePath = "codex",
                GitExecutablePath = "git",
                ExecutionTimeout = TimeSpan.FromSeconds(5),
                GitStatusTimeout = TimeSpan.FromSeconds(5)
            });

    private sealed class RecordingCliRunner : ICodexCliProcessRunner
    {
        private readonly Queue<Func<CodexCliProcessRequest, CodexCliProcessResult>> gitResults = [];
        private readonly Queue<Func<CodexCliProcessRequest, CodexCliProcessResult>> codexResults = [];

        public List<CodexCliProcessRequest> Requests { get; } = [];

        public void EnqueueGit(string stdout, string stderr = "", int exitCode = 0) =>
            gitResults.Enqueue(_ => new CodexCliProcessResult(exitCode, stdout, stderr));

        public void EnqueueCodex(string stdout, string stderr, int exitCode, string? lastMessage) =>
            codexResults.Enqueue(request =>
            {
                if (lastMessage is not null)
                {
                    var outputPath = GetLastMessagePath(request);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    File.WriteAllText(outputPath, lastMessage);
                }

                return new CodexCliProcessResult(exitCode, stdout, stderr);
            });

        public Task<CodexCliProcessResult> RunAsync(
            CodexCliProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var queue = request.FileName == "git" ? gitResults : codexResults;
            if (!queue.TryDequeue(out var next))
            {
                throw new InvalidOperationException($"No CLI result queued for {request.FileName}.");
            }

            return Task.FromResult(next(request));
        }

        private static string GetLastMessagePath(CodexCliProcessRequest request)
        {
            var index = request.Arguments.ToList().FindIndex(argument => argument == "--output-last-message");
            Assert.True(index >= 0 && index + 1 < request.Arguments.Count);
            return request.Arguments[index + 1];
        }
    }

    private sealed class TemporaryStateWorkspace : IDisposable
    {
        private TemporaryStateWorkspace(string root, bool gitRepo)
        {
            Root = root;
            Repo = Path.Combine(root, "repo");
            StateDirectory = Path.Combine(root, ".codex-manager");
            Directory.CreateDirectory(Repo);
            if (gitRepo)
            {
                Directory.CreateDirectory(Path.Combine(Repo, ".git"));
            }
        }

        public string Root { get; }

        public string Repo { get; }

        public string StateDirectory { get; }

        public static TemporaryStateWorkspace Create(bool gitRepo = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "claude-codex-mcp-cli-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TemporaryStateWorkspace(root, gitRepo);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
