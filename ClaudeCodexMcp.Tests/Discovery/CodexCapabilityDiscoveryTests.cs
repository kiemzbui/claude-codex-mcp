using ClaudeCodexMcp.Discovery;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Storage;

namespace ClaudeCodexMcp.Tests.Discovery;

public sealed class CodexCapabilityDiscoveryTests
{
    [Fact]
    public async Task MissingDiscoveryDirectoriesReturnEmptyBuckets()
    {
        using var workspace = TemporaryDiscoveryWorkspace.Create();
        var discovery = workspace.CreateDiscovery(codexHome: Path.Combine(workspace.Root, "missing-codex-home"));

        var result = await discovery.ListSkillsAsync();

        Assert.Empty(result.Global);
        Assert.Empty(result.RepoLocal);
        Assert.Empty(result.Configured);
        Assert.Empty(result.Merged);
    }

    [Fact]
    public async Task DiscoveryPreservesSourceBucketsAndConflicts()
    {
        using var workspace = TemporaryDiscoveryWorkspace.Create();
        workspace.WriteSkill(Path.Combine(workspace.CodexHome, "skills", "shared", "SKILL.md"), "shared", "global shared");
        workspace.WriteSkill(Path.Combine(workspace.RepoRoot, ".codex", "skills", "shared", "SKILL.md"), "shared", "repo shared");
        var discovery = workspace.CreateDiscovery();

        var result = await discovery.ListSkillsAsync(repoRoot: workspace.RepoRoot);

        Assert.Single(result.Global);
        Assert.Single(result.RepoLocal);
        Assert.Equal(2, result.Merged.Count);
        Assert.All(result.Merged, item => Assert.NotEmpty(item.ConflictsWith));
        Assert.Contains(result.Merged, item => item.SourceScope == "global" && item.SourcePath.Contains("shared"));
        Assert.Contains(result.Merged, item => item.SourceScope == "repoLocal" && item.SourcePath.Contains("shared"));
    }

    [Fact]
    public async Task ConfiguredSkillEntriesAreIncludedWithEnabledFlag()
    {
        using var workspace = TemporaryDiscoveryWorkspace.Create();
        var configuredSkill = Path.Combine(workspace.Root, "explicit", "SKILL.md");
        workspace.WriteSkill(configuredSkill, "configured-skill", "configured description");
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.ConfigPath)!);
        await File.WriteAllTextAsync(workspace.ConfigPath, $"""
            [[skills.config]]
            path = '{configuredSkill}'
            enabled = false
            """);
        var discovery = workspace.CreateDiscovery();

        var result = await discovery.ListSkillsAsync();

        var item = Assert.Single(result.Configured);
        Assert.Equal("configured-skill", item.Name);
        Assert.False(item.Enabled);
        Assert.Equal("configured", item.SourceScope);
        Assert.Equal(Path.GetFullPath(configuredSkill), item.SourcePath);
    }

    [Fact]
    public async Task CacheIsUsedUntilRootFingerprintChanges()
    {
        using var workspace = TemporaryDiscoveryWorkspace.Create();
        var firstSkill = Path.Combine(workspace.CodexHome, "skills", "first", "SKILL.md");
        workspace.WriteSkill(firstSkill, "first", "first description");
        var discovery = workspace.CreateDiscovery(cacheTtl: TimeSpan.FromMinutes(5));

        var first = await discovery.ListSkillsAsync();
        var cached = await discovery.ListSkillsAsync();

        Assert.False(first.CacheHit);
        Assert.True(cached.CacheHit);

        var secondSkill = Path.Combine(workspace.CodexHome, "skills", "second", "SKILL.md");
        workspace.WriteSkill(secondSkill, "second", "second description");
        var refreshed = await discovery.ListSkillsAsync();

        Assert.False(refreshed.CacheHit);
        Assert.Contains(refreshed.Global, item => item.Name == "second");
    }

    [Fact]
    public async Task DetailToolsRejectAmbiguousNamesAndDefaultToMetadataOnly()
    {
        using var workspace = TemporaryDiscoveryWorkspace.Create();
        workspace.WriteAgent(Path.Combine(workspace.CodexHome, "agents", "worker.toml"), "worker", "global worker", "GLOBAL_PROMPT_SECRET");
        workspace.WriteAgent(Path.Combine(workspace.RepoRoot, ".codex", "agents", "worker.toml"), "worker", "repo worker", "REPO_PROMPT_SECRET");
        var discovery = workspace.CreateDiscovery();

        var ambiguous = await discovery.GetAgentAsync("worker", repoRoot: workspace.RepoRoot);

        Assert.True(ambiguous.Ambiguous);
        Assert.Equal(2, ambiguous.Matches.Count);
        Assert.False(ambiguous.BodyIncluded);
        Assert.Null(ambiguous.Body);

        var metadataOnly = await discovery.GetAgentAsync("worker", sourceScope: "global", repoRoot: workspace.RepoRoot);

        Assert.True(metadataOnly.Found);
        Assert.False(metadataOnly.BodyIncluded);
        Assert.Null(metadataOnly.Body);
        Assert.DoesNotContain("GLOBAL_PROMPT_SECRET", metadataOnly.Item?.Description ?? string.Empty);
    }

    [Fact]
    public async Task FullSkillBodyRequiresExplicitOptInAndIsTruncated()
    {
        using var workspace = TemporaryDiscoveryWorkspace.Create();
        var body = "BODY_START " + new string('x', 4000);
        workspace.WriteSkill(Path.Combine(workspace.CodexHome, "skills", "large", "SKILL.md"), "large", "large skill", body);
        var discovery = workspace.CreateDiscovery();

        var metadataOnly = await discovery.GetSkillAsync("large");
        var withBody = await discovery.GetSkillAsync("large", includeBody: true, maxBytes: 256);

        Assert.Null(metadataOnly.Body);
        Assert.True(withBody.BodyIncluded);
        Assert.True(withBody.Truncated);
        Assert.NotNull(withBody.Body);
        Assert.Contains("[truncated]", withBody.Body);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(withBody.Body) <= 256);
    }

    private sealed class TemporaryDiscoveryWorkspace : IDisposable
    {
        private TemporaryDiscoveryWorkspace(string root)
        {
            Root = root;
            StateDirectory = Path.Combine(root, ".codex-manager");
            CodexHome = Path.Combine(root, "codex-home");
            RepoRoot = Path.Combine(root, "repo");
            ConfigPath = Path.Combine(CodexHome, "config.toml");
            Directory.CreateDirectory(RepoRoot);
        }

        public string Root { get; }

        public string StateDirectory { get; }

        public string CodexHome { get; }

        public string RepoRoot { get; }

        public string ConfigPath { get; }

        public static TemporaryDiscoveryWorkspace Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "claude-codex-mcp-discovery-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TemporaryDiscoveryWorkspace(root);
        }

        public CodexCapabilityDiscovery CreateDiscovery(string? codexHome = null, TimeSpan? cacheTtl = null) =>
            new(
                new DiscoveryCacheStore(new ManagerStatePaths(StateDirectory)),
                new CodexDiscoveryOptions
                {
                    CodexHome = codexHome ?? CodexHome,
                    RepoRoot = RepoRoot,
                    ConfigPath = ConfigPath,
                    CacheTtl = cacheTtl ?? TimeSpan.FromMinutes(5)
                });

        public void WriteSkill(string path, string name, string description, string? body = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"""
                ---
                name: {name}
                description: {description}
                ---

                {body ?? "skill body"}
                """);
        }

        public void WriteAgent(string path, string name, string description, string prompt)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path,
            [
                $"name = \"{name}\"",
                $"description = \"{description}\"",
                "developer_instructions = \"\"\"",
                prompt,
                "\"\"\""
            ]);
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
