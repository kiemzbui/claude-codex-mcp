using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Storage;

namespace ClaudeCodexMcp.Discovery;

public sealed class CodexCapabilityDiscovery
{
    public const string GlobalSourceScope = "global";
    public const string RepoLocalSourceScope = "repoLocal";
    public const string ConfiguredSourceScope = "configured";

    private const int DetailMaxBytes = 32 * 1024;
    private readonly DiscoveryCacheStore cacheStore;
    private readonly CodexDiscoveryOptions options;

    public CodexCapabilityDiscovery(
        DiscoveryCacheStore cacheStore,
        CodexDiscoveryOptions? options = null)
    {
        this.cacheStore = cacheStore;
        this.options = options ?? CodexDiscoveryOptions.FromEnvironment();
    }

    public async Task<DiscoveryBucketedResponse> ListSkillsAsync(
        bool forceRefresh = false,
        string? repoRoot = null,
        CancellationToken cancellationToken = default) =>
        await ListAsync(DiscoveryCacheKind.Skills, forceRefresh, repoRoot, cancellationToken);

    public async Task<DiscoveryBucketedResponse> ListAgentsAsync(
        bool forceRefresh = false,
        string? repoRoot = null,
        CancellationToken cancellationToken = default) =>
        await ListAsync(DiscoveryCacheKind.Agents, forceRefresh, repoRoot, cancellationToken);

    public async Task<DiscoveryDetailResponse> GetSkillAsync(
        string? name,
        string? sourceScope = null,
        string? sourcePath = null,
        bool includeBody = false,
        bool forceRefresh = false,
        string? repoRoot = null,
        int maxBytes = DetailMaxBytes,
        CancellationToken cancellationToken = default) =>
        await GetDetailAsync(
            DiscoveryCacheKind.Skills,
            name,
            sourceScope,
            sourcePath,
            includeBody,
            forceRefresh,
            repoRoot,
            maxBytes,
            cancellationToken);

    public async Task<DiscoveryDetailResponse> GetAgentAsync(
        string? name,
        string? sourceScope = null,
        string? sourcePath = null,
        bool includePrompt = false,
        bool forceRefresh = false,
        string? repoRoot = null,
        int maxBytes = DetailMaxBytes,
        CancellationToken cancellationToken = default) =>
        await GetDetailAsync(
            DiscoveryCacheKind.Agents,
            name,
            sourceScope,
            sourcePath,
            includePrompt,
            forceRefresh,
            repoRoot,
            maxBytes,
            cancellationToken);

    private async Task<DiscoveryBucketedResponse> ListAsync(
        DiscoveryCacheKind kind,
        bool forceRefresh,
        string? repoRoot,
        CancellationToken cancellationToken)
    {
        var normalizedRepoRoot = NormalizeOptionalPath(repoRoot) ?? NormalizeOptionalPath(options.RepoRoot);
        var roots = GetRoots(kind, normalizedRepoRoot).ToArray();
        var snapshots = roots.Select(root => CreateSnapshot(root.SourceScope, root.Path)).ToArray();
        var cached = await cacheStore.ReadAsync(kind, cancellationToken);

        if (!forceRefresh && IsCacheFresh(cached, normalizedRepoRoot, snapshots))
        {
            return ToBucketedResponse(cached!, cacheHit: true);
        }

        var items = new List<DiscoveryCacheItem>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (root.SourceScope == ConfiguredSourceScope)
            {
                items.AddRange(DiscoverConfigured(kind, root.Path));
                continue;
            }

            items.AddRange(DiscoverDirectory(kind, root.SourceScope, root.Path));
        }

        var withConflicts = ApplyConflicts(items);
        var record = new DiscoveryCacheRecord
        {
            Kind = kind,
            UpdatedAt = DateTimeOffset.UtcNow,
            RepoRoot = normalizedRepoRoot,
            Roots = snapshots,
            Items = withConflicts
        };
        await cacheStore.SaveAsync(record, cancellationToken);

        return ToBucketedResponse(record, cacheHit: false);
    }

    private async Task<DiscoveryDetailResponse> GetDetailAsync(
        DiscoveryCacheKind kind,
        string? name,
        string? sourceScope,
        string? sourcePath,
        bool includeBody,
        bool forceRefresh,
        string? repoRoot,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new DiscoveryDetailResponse
            {
                Errors = [new ToolError("blank_name", "A skill or agent name is required.", "name")]
            };
        }

        var list = await ListAsync(kind, forceRefresh, repoRoot, cancellationToken);
        var matches = list.Merged
            .Where(item => string.Equals(item.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(sourceScope)
                || string.Equals(item.SourceScope, sourceScope.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(sourcePath)
                || PathsEqual(item.SourcePath, sourcePath.Trim()))
            .ToArray();

        if (matches.Length == 0)
        {
            return new DiscoveryDetailResponse
            {
                Found = false,
                Errors = [new ToolError("not_found", $"No {kind.ToString().ToLowerInvariant()} entry named '{name.Trim()}' was found.", "name")]
            };
        }

        if (matches.Length > 1)
        {
            return new DiscoveryDetailResponse
            {
                Found = true,
                Ambiguous = true,
                Matches = matches,
                Errors = [new ToolError("ambiguous_name", "Multiple discovery entries match; provide sourceScope or sourcePath.", "name")]
            };
        }

        var item = matches.Single();
        if (!includeBody)
        {
            return new DiscoveryDetailResponse
            {
                Found = true,
                Item = item,
                BodyIncluded = false
            };
        }

        var bodyPath = item.BodyPath ?? item.SourcePath;
        if (string.IsNullOrWhiteSpace(bodyPath) || !File.Exists(bodyPath))
        {
            return new DiscoveryDetailResponse
            {
                Found = true,
                Item = item,
                BodyIncluded = false,
                Errors = [new ToolError("body_unavailable", "The discovery entry does not have a readable body path.", "sourcePath")]
            };
        }

        var body = await File.ReadAllTextAsync(bodyPath, cancellationToken);
        var limit = Math.Clamp(maxBytes, 1, DetailMaxBytes);
        var truncated = Encoding.UTF8.GetByteCount(body) > limit;
        return new DiscoveryDetailResponse
        {
            Found = true,
            Item = item,
            Body = ProjectionSanitizer.TruncateUtf8(body, limit),
            BodyIncluded = true,
            Truncated = truncated
        };
    }

    private IEnumerable<(string SourceScope, string Path)> GetRoots(DiscoveryCacheKind kind, string? repoRoot)
    {
        var codexHome = ResolveCodexHome();
        yield return (GlobalSourceScope, Path.Combine(codexHome, KindDirectoryName(kind)));

        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            yield return (RepoLocalSourceScope, Path.Combine(repoRoot, ".codex", KindDirectoryName(kind)));
        }

        yield return (ConfiguredSourceScope, ResolveConfigPath(codexHome));
    }

    private IEnumerable<DiscoveryCacheItem> DiscoverDirectory(
        DiscoveryCacheKind kind,
        string sourceScope,
        string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return [];
        }

        return kind == DiscoveryCacheKind.Skills
            ? DiscoverSkills(sourceScope, rootPath)
            : DiscoverAgents(sourceScope, rootPath);
    }

    private static IEnumerable<DiscoveryCacheItem> DiscoverSkills(string sourceScope, string rootPath)
    {
        foreach (var skillFile in Directory.EnumerateFiles(rootPath, "SKILL.md", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var metadata = ReadMetadata(skillFile);
            var name = NullIfWhiteSpace(metadata.GetValueOrDefault("name"))
                ?? new DirectoryInfo(Path.GetDirectoryName(skillFile)!).Name;
            yield return new DiscoveryCacheItem
            {
                Name = name,
                Description = NullIfWhiteSpace(metadata.GetValueOrDefault("description")),
                SourceScope = sourceScope,
                SourcePath = Path.GetFullPath(Path.GetDirectoryName(skillFile)!),
                BodyPath = Path.GetFullPath(skillFile),
                Enabled = true
            };
        }
    }

    private static IEnumerable<DiscoveryCacheItem> DiscoverAgents(string sourceScope, string rootPath)
    {
        foreach (var agentFile in Directory.EnumerateFiles(rootPath, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(path => string.Equals(Path.GetExtension(path), ".toml", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var metadata = ReadMetadata(agentFile);
            var name = NullIfWhiteSpace(metadata.GetValueOrDefault("name"))
                ?? Path.GetFileNameWithoutExtension(agentFile);
            yield return new DiscoveryCacheItem
            {
                Name = name,
                Description = NullIfWhiteSpace(metadata.GetValueOrDefault("description")),
                SourceScope = sourceScope,
                SourcePath = Path.GetFullPath(agentFile),
                BodyPath = Path.GetFullPath(agentFile),
                Enabled = true
            };
        }
    }

    private IEnumerable<DiscoveryCacheItem> DiscoverConfigured(DiscoveryCacheKind kind, string configPath)
    {
        if (!File.Exists(configPath))
        {
            return [];
        }

        return ReadConfiguredEntries(kind, configPath)
            .Select(entry =>
            {
                var bodyPath = ResolveConfiguredBodyPath(kind, entry.Path);
                var metadata = File.Exists(bodyPath) ? ReadMetadata(bodyPath) : new Dictionary<string, string>();
                var fallbackName = kind == DiscoveryCacheKind.Skills
                    ? new DirectoryInfo(Path.GetDirectoryName(bodyPath) ?? bodyPath).Name
                    : Path.GetFileNameWithoutExtension(bodyPath);
                return new DiscoveryCacheItem
                {
                    Name = NullIfWhiteSpace(metadata.GetValueOrDefault("name")) ?? fallbackName,
                    Description = NullIfWhiteSpace(metadata.GetValueOrDefault("description")),
                    SourceScope = ConfiguredSourceScope,
                    SourcePath = Path.GetFullPath(entry.Path),
                    BodyPath = File.Exists(bodyPath) ? Path.GetFullPath(bodyPath) : null,
                    Enabled = entry.Enabled
                };
            })
            .ToArray();
    }

    private static IReadOnlyList<DiscoveryCacheItem> ApplyConflicts(IReadOnlyList<DiscoveryCacheItem> items)
    {
        var conflictIdsByName = items
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => $"{item.SourceScope}:{item.SourcePath}").ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return items.Select(item =>
        {
            if (!conflictIdsByName.TryGetValue(item.Name, out var ids))
            {
                return item;
            }

            var ownId = $"{item.SourceScope}:{item.SourcePath}";
            return item with
            {
                ConflictsWith = ids
                    .Where(id => !string.Equals(id, ownId, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            };
        }).ToArray();
    }

    private static DiscoveryBucketedResponse ToBucketedResponse(DiscoveryCacheRecord record, bool cacheHit)
    {
        var global = record.Items.Where(item => item.SourceScope == GlobalSourceScope).ToArray();
        var repoLocal = record.Items.Where(item => item.SourceScope == RepoLocalSourceScope).ToArray();
        var configured = record.Items.Where(item => item.SourceScope == ConfiguredSourceScope).ToArray();

        return new DiscoveryBucketedResponse
        {
            Kind = record.Kind.ToString().ToLowerInvariant(),
            CacheHit = cacheHit,
            UpdatedAt = record.UpdatedAt,
            Global = global,
            RepoLocal = repoLocal,
            Configured = configured,
            Merged = record.Items
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SourceScope, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private bool IsCacheFresh(
        DiscoveryCacheRecord? cached,
        string? repoRoot,
        IReadOnlyList<DiscoveryRootSnapshot> snapshots)
    {
        if (cached is null)
        {
            return false;
        }

        if (!string.Equals(cached.RepoRoot, repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - cached.UpdatedAt > options.CacheTtl)
        {
            return false;
        }

        return cached.Roots.Count == snapshots.Count
            && cached.Roots.Zip(snapshots).All(pair => RootSnapshotEquals(pair.First, pair.Second));
    }

    private static bool RootSnapshotEquals(DiscoveryRootSnapshot left, DiscoveryRootSnapshot right) =>
        string.Equals(left.SourceScope, right.SourceScope, StringComparison.OrdinalIgnoreCase)
        && PathsEqual(left.RootPath, right.RootPath)
        && left.Exists == right.Exists
        && left.MaxLastWriteTimeUtcTicks == right.MaxLastWriteTimeUtcTicks
        && left.FileCount == right.FileCount;

    private static DiscoveryRootSnapshot CreateSnapshot(string sourceScope, string rootPath)
    {
        if (File.Exists(rootPath))
        {
            return new DiscoveryRootSnapshot
            {
                SourceScope = sourceScope,
                RootPath = Path.GetFullPath(rootPath),
                Exists = true,
                MaxLastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(rootPath).Ticks,
                FileCount = 1
            };
        }

        if (!Directory.Exists(rootPath))
        {
            return new DiscoveryRootSnapshot
            {
                SourceScope = sourceScope,
                RootPath = Path.GetFullPath(rootPath),
                Exists = false
            };
        }

        var files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).ToArray();
        return new DiscoveryRootSnapshot
        {
            SourceScope = sourceScope,
            RootPath = Path.GetFullPath(rootPath),
            Exists = true,
            MaxLastWriteTimeUtcTicks = files.Length == 0 ? Directory.GetLastWriteTimeUtc(rootPath).Ticks : files.Max(path => File.GetLastWriteTimeUtc(path).Ticks),
            FileCount = files.Length
        };
    }

    private string ResolveCodexHome()
    {
        if (!string.IsNullOrWhiteSpace(options.CodexHome))
        {
            return Path.GetFullPath(options.CodexHome);
        }

        var userProfile = NullIfWhiteSpace(options.UserProfile)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.GetFullPath(Path.Combine(userProfile, ".codex"));
    }

    private string ResolveConfigPath(string codexHome) =>
        Path.GetFullPath(NullIfWhiteSpace(options.ConfigPath) ?? Path.Combine(codexHome, "config.toml"));

    private static string KindDirectoryName(DiscoveryCacheKind kind) =>
        kind == DiscoveryCacheKind.Skills ? "skills" : "agents";

    private static string ResolveConfiguredBodyPath(DiscoveryCacheKind kind, string configuredPath)
    {
        var fullPath = Path.GetFullPath(configuredPath);
        if (kind == DiscoveryCacheKind.Skills && Directory.Exists(fullPath))
        {
            return Path.Combine(fullPath, "SKILL.md");
        }

        return fullPath;
    }

    private static IReadOnlyList<(string Path, bool Enabled)> ReadConfiguredEntries(DiscoveryCacheKind kind, string configPath)
    {
        var sectionName = kind == DiscoveryCacheKind.Skills ? "skills.config" : "agents.config";
        var entries = new List<(string Path, bool Enabled)>();
        string? path = null;
        var enabled = true;
        var inSection = false;

        foreach (var rawLine in File.ReadLines(configPath))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
            {
                Flush();
                inSection = string.Equals(line[2..^2].Trim(), sectionName, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
            {
                path = Unquote(value);
            }
            else if (string.Equals(key, "enabled", StringComparison.OrdinalIgnoreCase)
                && bool.TryParse(value, out var parsed))
            {
                enabled = parsed;
            }
        }

        Flush();
        return entries;

        void Flush()
        {
            if (inSection && !string.IsNullOrWhiteSpace(path))
            {
                entries.Add((path, enabled));
            }

            path = null;
            enabled = true;
            inSection = false;
        }
    }

    private static Dictionary<string, string> ReadMetadata(string path)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return metadata;
        }

        using var reader = File.OpenText(path);
        var firstLine = reader.ReadLine();
        if (string.Equals(firstLine, "---", StringComparison.Ordinal))
        {
            while (reader.ReadLine() is { } line)
            {
                if (string.Equals(line.Trim(), "---", StringComparison.Ordinal))
                {
                    break;
                }

                AddMetadataLine(metadata, line);
            }

            return metadata;
        }

        AddMetadataLine(metadata, firstLine ?? string.Empty);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                break;
            }

            AddMetadataLine(metadata, line);
        }

        return metadata;
    }

    private static void AddMetadataLine(Dictionary<string, string> metadata, string line)
    {
        var trimmed = line.Trim();
        var separator = trimmed.IndexOf('=');
        if (separator < 0)
        {
            separator = trimmed.IndexOf(':');
        }

        if (separator <= 0)
        {
            return;
        }

        var key = trimmed[..separator].Trim();
        var value = Unquote(trimmed[(separator + 1)..].Trim());
        if (!string.IsNullOrWhiteSpace(key) && !metadata.ContainsKey(key))
        {
            metadata[key] = value;
        }
    }

    private static string StripComment(string line)
    {
        var inQuote = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] is '"' or '\'')
            {
                inQuote = !inQuote;
            }

            if (!inQuote && line[index] == '#')
            {
                return line[..index];
            }
        }

        return line;
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim().TrimEnd(',');
        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            return trimmed[1..^1];
        }

        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
            return Regex.Unescape(trimmed);
        }

        return trimmed;
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim());

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool PathsEqual(string left, string right) =>
        PathComparison.Equals(Path.GetFullPath(left), Path.GetFullPath(right));

    private static StringComparer PathComparison =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
