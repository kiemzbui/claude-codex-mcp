namespace ClaudeCodexMcp.Discovery;

public sealed record CodexDiscoveryOptions
{
    public string? CodexHome { get; init; }

    public string? UserProfile { get; init; }

    public string? RepoRoot { get; init; }

    public string? ConfigPath { get; init; }

    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromSeconds(30);

    public static CodexDiscoveryOptions FromEnvironment(string? repoRoot = null)
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new CodexDiscoveryOptions
        {
            CodexHome = string.IsNullOrWhiteSpace(codexHome) ? null : codexHome,
            UserProfile = string.IsNullOrWhiteSpace(userProfile) ? null : userProfile,
            RepoRoot = repoRoot
        };
    }
}
