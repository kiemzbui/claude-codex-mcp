namespace ClaudeCodexMcp.Backend;

public sealed class CodexBackendThreadUnrecoverableException : Exception
{
    public CodexBackendThreadUnrecoverableException(string message)
        : base(message)
    {
    }

    public CodexBackendThreadUnrecoverableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
