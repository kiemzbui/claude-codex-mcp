namespace ClaudeCodexMcp.Backend.AppServerFeasibility;

public sealed record AppServerProbeOptions
{
    public string CodexExecutable { get; init; } = "codex";

    public string WorkingDirectory { get; init; } = Environment.CurrentDirectory;

    public string Prompt { get; init; } = "Reply exactly APP_SERVER_FEASIBILITY_OK. Do not edit files.";

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public bool VerifyResume { get; init; }
}
