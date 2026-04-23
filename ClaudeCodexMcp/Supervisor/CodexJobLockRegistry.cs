using System.Collections.Concurrent;

namespace ClaudeCodexMcp.Supervisor;

public sealed class CodexJobLockRegistry
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);

    public async ValueTask<CodexJobLockLease> AcquireAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var semaphore = locks.GetOrAdd(jobId.Trim(), _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new CodexJobLockLease(semaphore);
    }
}

public sealed class CodexJobLockLease : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim semaphore;
    private bool disposed;

    internal CodexJobLockLease(SemaphoreSlim semaphore)
    {
        this.semaphore = semaphore;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        semaphore.Release();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
