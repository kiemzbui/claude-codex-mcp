using System.Threading;
using System.Threading.Tasks;
using ClaudeCodexMcp.Domain;

namespace ClaudeCodexMcp.Backend;

public interface ICodexBackend
{
    CodexBackendCapabilities Capabilities { get; }

    Task<CodexBackendStartResult> StartAsync(
        CodexBackendStartRequest request,
        CancellationToken cancellationToken = default);

    Task<CodexBackendStatus> ObserveStatusAsync(
        CodexBackendObserveRequest request,
        CancellationToken cancellationToken = default);

    Task<CodexBackendStatus> PollStatusAsync(
        CodexBackendObserveRequest request,
        CancellationToken cancellationToken = default);

    Task<CodexBackendStatus> SendInputAsync(
        CodexBackendSendInputRequest request,
        CancellationToken cancellationToken = default);

    Task<CodexBackendStatus> CancelAsync(
        CodexBackendCancelRequest request,
        CancellationToken cancellationToken = default);

    Task<CodexBackendOutput> ReadFinalOutputAsync(
        CodexBackendOutputRequest request,
        CancellationToken cancellationToken = default);

    Task<CodexBackendUsageSnapshot> ReadUsageAsync(
        CodexBackendUsageRequest request,
        CancellationToken cancellationToken = default);

    Task<CodexBackendStatus> ResumeAsync(
        CodexBackendResumeRequest request,
        CancellationToken cancellationToken = default);
}
