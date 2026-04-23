using ClaudeCodexMcp.Configuration;
using ClaudeCodexMcp.Domain;
using Microsoft.Extensions.Options;

namespace ClaudeCodexMcp.Backend;

public interface ICodexBackendSelector
{
    ICodexBackend SelectForPolicy(ValidatedDispatchPolicy policy);

    ICodexBackend SelectForJob(CodexJobRecord job);
}

public static class CodexCliBackendSelection
{
    public static string ResolveBackendKind(string? profileBackend, bool appServerAvailable)
    {
        var normalized = NormalizeBackendKind(profileBackend);
        if (normalized == CodexBackendNames.Cli)
        {
            return CodexBackendNames.Cli;
        }

        if (appServerAvailable)
        {
            return CodexBackendNames.AppServer;
        }

        throw new InvalidOperationException("CLI fallback requires profile backend policy to be 'cli'.");
    }

    public static bool IsCliAllowedByProfile(string? profileBackend) =>
        NormalizeBackendKind(profileBackend) == CodexBackendNames.Cli;

    public static string NormalizeBackendKind(string? profileBackend)
    {
        if (string.IsNullOrWhiteSpace(profileBackend))
        {
            return CodexBackendNames.AppServer;
        }

        var value = profileBackend.Trim();
        if (string.Equals(value, CodexBackendNames.Cli, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "cliFallback", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "cli-fallback", StringComparison.OrdinalIgnoreCase))
        {
            return CodexBackendNames.Cli;
        }

        if (string.Equals(value, CodexBackendNames.AppServer, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "app-server", StringComparison.OrdinalIgnoreCase))
        {
            return CodexBackendNames.AppServer;
        }

        return value;
    }
}

public sealed class CodexProfileBackendSelector : ICodexBackendSelector
{
    private readonly ICodexBackend appServerBackend;
    private readonly CodexCliBackend cliBackend;
    private readonly ManagerOptions options;

    public CodexProfileBackendSelector(
        CodexAppServerBackend appServerBackend,
        CodexCliBackend cliBackend,
        IOptions<ManagerOptions> options)
    {
        this.appServerBackend = appServerBackend;
        this.cliBackend = cliBackend;
        this.options = options.Value;
    }

    public ICodexBackend SelectForPolicy(ValidatedDispatchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Select(policy.Backend);
    }

    public ICodexBackend SelectForJob(CodexJobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return options.Profiles.TryGetValue(job.Profile, out var profile)
            ? Select(profile.Backend)
            : appServerBackend;
    }

    private ICodexBackend Select(string? profileBackend)
    {
        var backendKind = CodexCliBackendSelection.ResolveBackendKind(
            profileBackend,
            appServerAvailable: true);
        return string.Equals(backendKind, CodexBackendNames.Cli, StringComparison.Ordinal)
            ? cliBackend
            : appServerBackend;
    }
}
