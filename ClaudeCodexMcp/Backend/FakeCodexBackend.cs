using System.Collections.Concurrent;
using ClaudeCodexMcp.Domain;

namespace ClaudeCodexMcp.Backend;

public sealed class FakeCodexBackend : ICodexBackend
{
    private readonly ConcurrentQueue<CodexBackendStatus> queuedStatuses = new();

    public FakeCodexBackend(CodexBackendCapabilities? capabilities = null)
    {
        Capabilities = capabilities ?? new CodexBackendCapabilities
        {
            BackendId = "fake-codex-backend",
            BackendKind = CodexBackendNames.Fake,
            SupportsStart = true,
            SupportsObserveStatus = true,
            SupportsSendInput = true,
            SupportsCancel = true,
            SupportsReadFinalOutput = true,
            SupportsReadUsage = true,
            SupportsResume = true
        };
    }

    public CodexBackendCapabilities Capabilities { get; }

    public IReadOnlyList<CodexBackendStartRequest> StartRequests => startRequests;

    public IReadOnlyList<CodexBackendSendInputRequest> SendInputRequests => sendInputRequests;

    public IReadOnlyList<CodexBackendCancelRequest> CancelRequests => cancelRequests;

    public IReadOnlyList<CodexBackendResumeRequest> ResumeRequests => resumeRequests;

    public CodexBackendOutput Output { get; set; } = new()
    {
        FinalText = "fake final output",
        Summary = "fake final output"
    };

    public CodexBackendUsageSnapshot Usage { get; set; } = new();

    private readonly List<CodexBackendStartRequest> startRequests = [];
    private readonly List<CodexBackendSendInputRequest> sendInputRequests = [];
    private readonly List<CodexBackendCancelRequest> cancelRequests = [];
    private readonly List<CodexBackendResumeRequest> resumeRequests = [];

    public void EnqueueStatus(CodexBackendStatus status) => queuedStatuses.Enqueue(status);

    public Task<CodexBackendStartResult> StartAsync(
        CodexBackendStartRequest request,
        CancellationToken cancellationToken = default)
    {
        startRequests.Add(request);
        var ids = new CodexBackendIds
        {
            ThreadId = $"fake-thread-{startRequests.Count}",
            TurnId = $"fake-turn-{startRequests.Count}",
            SessionId = $"fake-session-{startRequests.Count}"
        };
        return Task.FromResult(new CodexBackendStartResult
        {
            Status = new CodexBackendStatus
            {
                State = JobState.Running,
                BackendIds = ids
            }
        });
    }

    public Task<CodexBackendStatus> ObserveStatusAsync(
        CodexBackendObserveRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(queuedStatuses.TryDequeue(out var status)
            ? status
            : new CodexBackendStatus { State = JobState.Running, BackendIds = request.BackendIds });
    }

    public Task<CodexBackendStatus> SendInputAsync(
        CodexBackendSendInputRequest request,
        CancellationToken cancellationToken = default)
    {
        sendInputRequests.Add(request);
        return Task.FromResult(new CodexBackendStatus
        {
            State = JobState.Running,
            BackendIds = request.BackendIds
        });
    }

    public Task<CodexBackendStatus> CancelAsync(
        CodexBackendCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        cancelRequests.Add(request);
        return Task.FromResult(new CodexBackendStatus
        {
            State = JobState.Cancelled,
            BackendIds = request.BackendIds
        });
    }

    public Task<CodexBackendOutput> ReadFinalOutputAsync(
        CodexBackendOutputRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Output with { BackendIds = request.BackendIds });

    public Task<CodexBackendUsageSnapshot> ReadUsageAsync(
        CodexBackendUsageRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Usage);

    public Task<CodexBackendStatus> ResumeAsync(
        CodexBackendResumeRequest request,
        CancellationToken cancellationToken = default)
    {
        resumeRequests.Add(request);
        return Task.FromResult(new CodexBackendStatus
        {
            State = JobState.Running,
            BackendIds = request.BackendIds
        });
    }
}
