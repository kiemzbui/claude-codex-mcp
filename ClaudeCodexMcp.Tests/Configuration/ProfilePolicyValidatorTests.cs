using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeCodexMcp.Configuration;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Workflows;
using Microsoft.Extensions.Options;

namespace ClaudeCodexMcp.Tests.Configuration;

public sealed class ProfilePolicyValidatorTests
{
    [Fact]
    public void ValidProfileLoadAppliesDefaultsAndSummaryData()
    {
        var repo = CreateNormalizedPath("repo");
        var validator = CreateValidator(new Dictionary<string, ProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["implementation"] = new()
            {
                Repo = repo,
                AllowedRepos = [repo],
                TaskPrefix = "Use repo instructions.",
                Backend = "appServer",
                ReadOnly = true,
                Permissions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sandbox"] = "read-only",
                    ["approvalPolicy"] = "never"
                },
                DefaultWorkflow = CanonicalWorkflows.Direct,
                AllowedWorkflows = [CanonicalWorkflows.Direct, CanonicalWorkflows.SubagentManager],
                ChannelNotifications = new ChannelNotificationOptions { Enabled = false },
                DefaultModel = "gpt-5.4",
                AllowedModels = ["gpt-5.4"],
                DefaultEffort = CodexEfforts.Medium,
                AllowedEfforts = [CodexEfforts.Low, CodexEfforts.Medium],
                FastMode = true
            }
        });

        var result = validator.ValidateStartDispatch(new StartDispatchRequest(
            "implementation",
            Workflow: null,
            "Fix validation",
            Repo: null,
            new DispatchOptions()));

        Assert.True(result.IsValid, DescribeErrors(result.Errors));
        Assert.NotNull(result.Value);
        Assert.Equal("implementation", result.Value.Profile);
        Assert.Equal(repo, result.Value.Repo);
        Assert.Equal(CanonicalWorkflows.Direct, result.Value.Workflow);
        Assert.Equal("Fix validation", result.Value.Title);
        Assert.Equal("Use repo instructions.", result.Value.TaskPrefix);
        Assert.Equal("appServer", result.Value.Backend);
        Assert.True(result.Value.ReadOnly);
        Assert.Equal("read-only", result.Value.Permissions["sandbox"]);
        Assert.Equal(1, result.Value.MaxConcurrentJobs);
        Assert.False(result.Value.ChannelNotifications.Enabled);
        Assert.Equal("gpt-5.4", result.Value.Options.Model);
        Assert.Equal(CodexEfforts.Medium, result.Value.Options.Effort);
        Assert.True(result.Value.Options.FastMode);
        Assert.Equal(CodexServiceTiers.Fast, result.Value.Options.ServiceTier);

        var summary = validator.GetProfileSummary("implementation");
        Assert.True(summary.IsValid, DescribeErrors(summary.Errors));
        Assert.NotNull(summary.Value);
        Assert.Equal("Use repo instructions.", summary.Value.TaskPrefix);
        Assert.Equal("appServer", summary.Value.Backend);
        Assert.True(summary.Value.ReadOnly);
        Assert.Equal("never", summary.Value.Permissions["approvalPolicy"]);
        Assert.False(summary.Value.ChannelNotifications.Enabled);
        Assert.Equal("gpt-5.4", summary.Value.DefaultModel);
        Assert.Equal(CodexEfforts.Medium, summary.Value.DefaultEffort);
        Assert.False(summary.Value.AllowModelOverride);
        Assert.False(summary.Value.AllowEffortOverride);
        Assert.False(summary.Value.AllowFastModeOverride);
    }

    [Fact]
    public void UnknownProfileIsRejected()
    {
        var validator = CreateValidator();

        var result = validator.ValidateStartDispatch(new StartDispatchRequest(
            "missing",
            CanonicalWorkflows.Direct,
            "Investigate",
            CreateNormalizedPath("repo"),
            new DispatchOptions()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "unknown_profile");
    }

    [Fact]
    public void RepoOutsideAllowlistIsRejected()
    {
        var allowedRepo = CreateNormalizedPath("allowed");
        var validator = CreateValidator(new Dictionary<string, ProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["investigation"] = BasicProfile(allowedRepo)
        });

        var result = validator.ValidateStartDispatch(new StartDispatchRequest(
            "investigation",
            CanonicalWorkflows.Direct,
            "Investigate",
            CreateNormalizedPath("outside"),
            new DispatchOptions()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "repo_not_allowed");
    }

    [Fact]
    public void WorkflowOutsideAllowlistIsRejected()
    {
        var repo = CreateNormalizedPath("repo");
        var profile = BasicProfile(repo);
        profile.AllowedWorkflows = [CanonicalWorkflows.Direct];
        var validator = CreateValidator(new Dictionary<string, ProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["implementation"] = profile
        });

        var result = validator.ValidateStartDispatch(new StartDispatchRequest(
            "implementation",
            CanonicalWorkflows.OrchestrateExecute,
            "Run plan step",
            repo,
            new DispatchOptions()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "workflow_not_allowed");
    }

    [Fact]
    public void InvalidEffortIsRejected()
    {
        var repo = CreateNormalizedPath("repo");
        var profile = BasicProfile(repo);
        profile.AllowEffortOverride = true;
        var validator = CreateValidator(new Dictionary<string, ProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["implementation"] = profile
        });

        var result = validator.ValidateStartDispatch(new StartDispatchRequest(
            "implementation",
            CanonicalWorkflows.Direct,
            "Fix failing test",
            repo,
            new DispatchOptions(Effort: "turbo")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "invalid_effort");
    }

    [Fact]
    public void BlankTitleIsRejected()
    {
        var repo = CreateNormalizedPath("repo");
        var validator = CreateValidator(new Dictionary<string, ProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["implementation"] = BasicProfile(repo)
        });

        var result = validator.ValidateStartDispatch(new StartDispatchRequest(
            "implementation",
            CanonicalWorkflows.Direct,
            " ",
            repo,
            new DispatchOptions()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "blank_title");
    }

    [Fact]
    public void MissingWorkflowIsRejected()
    {
        var repo = CreateNormalizedPath("repo");
        var profile = BasicProfile(repo);
        profile.DefaultWorkflow = null;
        var validator = CreateValidator(new Dictionary<string, ProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["implementation"] = profile
        });

        var result = validator.ValidateStartDispatch(new StartDispatchRequest(
            "implementation",
            Workflow: null,
            "Fix failing test",
            repo,
            new DispatchOptions()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "missing_workflow");
    }

    [Fact]
    public void BlankProfileNameIsRejected()
    {
        var validator = CreateValidator();

        var result = validator.ValidateStartDispatch(new StartDispatchRequest(
            " ",
            CanonicalWorkflows.Direct,
            "Investigate",
            CreateNormalizedPath("repo"),
            new DispatchOptions()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "blank_profile");
    }

    [Fact]
    public void MaxConcurrentJobsDefaultsToOne()
    {
        var repo = CreateNormalizedPath("repo");
        var profile = BasicProfile(repo);
        profile.MaxConcurrentJobs = 0;
        var validator = CreateValidator(new Dictionary<string, ProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["implementation"] = profile
        });

        var result = validator.GetProfileSummary("implementation");

        Assert.True(result.IsValid, DescribeErrors(result.Errors));
        Assert.NotNull(result.Value);
        Assert.Equal(1, result.Value.MaxConcurrentJobs);
    }

    [Fact]
    public void AllowedOverridesAreSelectedAndValidated()
    {
        var repo = CreateNormalizedPath("repo");
        var profile = BasicProfile(repo);
        profile.DefaultModel = "gpt-5.4";
        profile.AllowedModels = ["gpt-5.4", "gpt-5.4-codex"];
        profile.AllowModelOverride = true;
        profile.DefaultEffort = CodexEfforts.Low;
        profile.AllowedEfforts = [CodexEfforts.Low, CodexEfforts.High];
        profile.AllowEffortOverride = true;
        profile.FastMode = false;
        profile.AllowFastModeOverride = true;
        profile.DefaultServiceTier = CodexServiceTiers.Flex;
        var validator = CreateValidator(new Dictionary<string, ProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["implementation"] = profile
        });

        var result = validator.ValidateStartDispatch(new StartDispatchRequest(
            "implementation",
            CanonicalWorkflows.Direct,
            "Implement change",
            repo,
            new DispatchOptions("gpt-5.4-codex", CodexEfforts.High, FastMode: true)));

        Assert.True(result.IsValid, DescribeErrors(result.Errors));
        Assert.NotNull(result.Value);
        Assert.Equal("gpt-5.4-codex", result.Value.Options.Model);
        Assert.Equal(CodexEfforts.High, result.Value.Options.Effort);
        Assert.True(result.Value.Options.FastMode);
        Assert.Equal(CodexServiceTiers.Fast, result.Value.Options.ServiceTier);
    }

    [Fact]
    public void DisallowedOverridesAreRejectedBeforeSelectionCanBeUsed()
    {
        var repo = CreateNormalizedPath("repo");
        var profile = BasicProfile(repo);
        profile.DefaultModel = "gpt-5.4";
        profile.DefaultEffort = CodexEfforts.Medium;
        profile.FastMode = false;
        var validator = CreateValidator(new Dictionary<string, ProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["implementation"] = profile
        });

        var result = validator.ValidateStartDispatch(new StartDispatchRequest(
            "implementation",
            CanonicalWorkflows.Direct,
            "Implement change",
            repo,
            new DispatchOptions("gpt-5.4-codex", CodexEfforts.High, FastMode: true)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "model_override_disallowed");
        Assert.Contains(result.Errors, error => error.Code == "effort_override_disallowed");
        Assert.Contains(result.Errors, error => error.Code == "fast_mode_override_disallowed");
    }

    [Fact]
    public void ChannelNotificationsAndServiceTierHaveDefaults()
    {
        var repo = CreateNormalizedPath("repo");
        var validator = CreateValidator(new Dictionary<string, ProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["investigation"] = BasicProfile(repo)
        });

        var result = validator.GetProfileSummary("investigation");

        Assert.True(result.IsValid, DescribeErrors(result.Errors));
        Assert.NotNull(result.Value);
        Assert.False(result.Value.ChannelNotifications.Enabled);
        Assert.False(result.Value.FastMode);
        Assert.Equal(CodexServiceTiers.Normal, result.Value.DefaultServiceTier);
    }

    private static ProfilePolicyValidator CreateValidator(
        Dictionary<string, ProfileOptions>? profiles = null)
    {
        var options = new ManagerOptions
        {
            Profiles = profiles ?? new Dictionary<string, ProfileOptions>(StringComparer.OrdinalIgnoreCase)
        };

        return new ProfilePolicyValidator(Options.Create(options));
    }

    private static ProfileOptions BasicProfile(string repo) => new()
    {
        Repo = repo,
        AllowedRepos = [repo],
        Backend = "appServer",
        DefaultWorkflow = CanonicalWorkflows.Direct,
        AllowedWorkflows = [CanonicalWorkflows.Direct],
        DefaultEffort = CodexEfforts.Medium
    };

    private static string CreateNormalizedPath(string leaf) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claude-codex-mcp-policy-tests", leaf));

    private static string DescribeErrors(IReadOnlyList<PolicyValidationError> errors) =>
        string.Join(", ", errors.Select(error => $"{error.Code}:{error.Field}"));
}

