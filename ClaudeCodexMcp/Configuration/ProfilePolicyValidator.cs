using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeCodexMcp.Domain;
using ClaudeCodexMcp.Workflows;
using Microsoft.Extensions.Options;

namespace ClaudeCodexMcp.Configuration;

public sealed class ProfilePolicyValidator : IProfilePolicyValidator
{
    private readonly ManagerOptions options;

    public ProfilePolicyValidator(IOptions<ManagerOptions> options)
    {
        this.options = options.Value;
    }

    public PolicyValidationResult<ValidatedDispatchPolicy> ValidateStartDispatch(StartDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<PolicyValidationError>();
        if (!TryGetProfile(request.Profile, errors, out var profileName, out var profile))
        {
            return PolicyValidationResult<ValidatedDispatchPolicy>.Failure(errors);
        }

        var title = ValidateTitle(request.Title, errors);
        var repo = ResolveAndValidateRepo(profile, request.Repo, errors);
        var workflow = ResolveAndValidateWorkflow(profile, request.Workflow, errors);
        var selectedOptions = ResolveAndValidateDispatchOptions(profile, request.Options ?? new DispatchOptions(), errors);

        if (errors.Count > 0)
        {
            return PolicyValidationResult<ValidatedDispatchPolicy>.Failure(errors);
        }

        return PolicyValidationResult<ValidatedDispatchPolicy>.Success(new ValidatedDispatchPolicy(
            profileName,
            repo,
            workflow,
            title,
            NullIfWhiteSpace(profile.TaskPrefix),
            NullIfWhiteSpace(profile.Backend),
            profile.ReadOnly,
            new Dictionary<string, string>(profile.Permissions, StringComparer.OrdinalIgnoreCase),
            NormalizeMaxConcurrentJobs(profile),
            new ChannelNotificationPolicy(profile.ChannelNotifications.Enabled),
            selectedOptions));
    }

    public PolicyValidationResult<ProfilePolicySummary> GetProfileSummary(string? profileName)
    {
        var errors = new List<PolicyValidationError>();
        if (!TryGetProfile(profileName, errors, out var normalizedProfileName, out var profile))
        {
            return PolicyValidationResult<ProfilePolicySummary>.Failure(errors);
        }

        var serviceTier = ResolveDefaultServiceTier(profile, errors);
        var allowedEfforts = NormalizeAllowedEfforts(profile, errors);
        var defaultEffort = NormalizeOptionalEffort(profile.DefaultEffort, "defaultEffort", errors);
        if (errors.Count > 0)
        {
            return PolicyValidationResult<ProfilePolicySummary>.Failure(errors);
        }

        return PolicyValidationResult<ProfilePolicySummary>.Success(new ProfilePolicySummary(
            normalizedProfileName,
            NullIfWhiteSpace(profile.Repo),
            profile.AllowedRepos.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(),
            NullIfWhiteSpace(profile.TaskPrefix),
            NullIfWhiteSpace(profile.Backend),
            profile.ReadOnly,
            new Dictionary<string, string>(profile.Permissions, StringComparer.OrdinalIgnoreCase),
            NullIfWhiteSpace(profile.DefaultWorkflow),
            profile.AllowedWorkflows.Where(workflow => !string.IsNullOrWhiteSpace(workflow)).ToArray(),
            NormalizeMaxConcurrentJobs(profile),
            new ChannelNotificationPolicy(profile.ChannelNotifications.Enabled),
            NullIfWhiteSpace(profile.DefaultModel),
            profile.AllowedModels.Where(model => !string.IsNullOrWhiteSpace(model)).ToArray(),
            profile.AllowModelOverride,
            defaultEffort,
            allowedEfforts,
            profile.AllowEffortOverride,
            profile.FastMode,
            profile.AllowFastModeOverride,
            profile.RequireFastMode,
            serviceTier));
    }

    private bool TryGetProfile(
        string? requestedProfile,
        List<PolicyValidationError> errors,
        out string profileName,
        out ProfileOptions profile)
    {
        profileName = string.Empty;
        profile = new ProfileOptions();

        if (string.IsNullOrWhiteSpace(requestedProfile))
        {
            errors.Add(new PolicyValidationError("blank_profile", "Profile name is required.", "profile"));
            return false;
        }

        var trimmedName = requestedProfile.Trim();
        if (!options.Profiles.TryGetValue(trimmedName, out var matchedProfile))
        {
            errors.Add(new PolicyValidationError("unknown_profile", $"Profile '{trimmedName}' is not configured.", "profile"));
            return false;
        }

        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            errors.Add(new PolicyValidationError("blank_profile", "Profile name is required.", "profile"));
            return false;
        }

        profileName = trimmedName;
        profile = matchedProfile;
        return true;
    }

    private static string ValidateTitle(string? title, List<PolicyValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            errors.Add(new PolicyValidationError("blank_title", "Dispatch title is required.", "title"));
            return string.Empty;
        }

        return title.Trim();
    }

    private static string ResolveAndValidateRepo(ProfileOptions profile, string? requestedRepo, List<PolicyValidationError> errors)
    {
        var selectedRepo = NullIfWhiteSpace(requestedRepo) ?? NullIfWhiteSpace(profile.Repo);
        if (selectedRepo is null)
        {
            errors.Add(new PolicyValidationError("missing_repo", "A repo must be provided by the request or profile.", "repo"));
            return string.Empty;
        }

        var normalizedRepo = NormalizePath(selectedRepo);
        var allowedRepos = profile.AllowedRepos
            .Select(NullIfWhiteSpace)
            .Where(path => path is not null)
            .Select(path => NormalizePath(path!))
            .ToArray();

        if (allowedRepos.Length == 0 && !string.IsNullOrWhiteSpace(profile.Repo))
        {
            allowedRepos = [NormalizePath(profile.Repo)];
        }

        if (allowedRepos.Length == 0)
        {
            errors.Add(new PolicyValidationError("missing_allowed_repos", "Profile must define allowed repos or a default repo.", "allowedRepos"));
            return normalizedRepo;
        }

        if (!allowedRepos.Contains(normalizedRepo, PathComparer))
        {
            errors.Add(new PolicyValidationError("repo_not_allowed", "Requested repo is outside the selected profile allowlist.", "repo"));
        }

        return normalizedRepo;
    }

    private static string ResolveAndValidateWorkflow(ProfileOptions profile, string? requestedWorkflow, List<PolicyValidationError> errors)
    {
        var selectedWorkflow = NullIfWhiteSpace(requestedWorkflow) ?? NullIfWhiteSpace(profile.DefaultWorkflow);
        if (selectedWorkflow is null)
        {
            errors.Add(new PolicyValidationError("missing_workflow", "A workflow must be provided by the request or profile.", "workflow"));
            return string.Empty;
        }

        if (!CanonicalWorkflows.TryNormalize(selectedWorkflow, out var normalizedWorkflow))
        {
            errors.Add(new PolicyValidationError("invalid_workflow", $"Workflow '{selectedWorkflow}' is not supported.", "workflow"));
            return selectedWorkflow;
        }

        var allowedWorkflows = profile.AllowedWorkflows
            .Select(NullIfWhiteSpace)
            .Where(workflow => workflow is not null)
            .Select(workflow => CanonicalWorkflows.TryNormalize(workflow, out var normalized) ? normalized : workflow!)
            .ToArray();

        if (allowedWorkflows.Length == 0)
        {
            errors.Add(new PolicyValidationError("missing_allowed_workflows", "Profile must allow at least one workflow.", "allowedWorkflows"));
            return normalizedWorkflow;
        }

        if (!allowedWorkflows.Contains(normalizedWorkflow, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(new PolicyValidationError("workflow_not_allowed", "Requested workflow is not allowed by the selected profile.", "workflow"));
        }

        return normalizedWorkflow;
    }

    private static SelectedDispatchOptions ResolveAndValidateDispatchOptions(
        ProfileOptions profile,
        DispatchOptions request,
        List<PolicyValidationError> errors)
    {
        var selectedModel = ResolveAndValidateModel(profile, request.Model, errors);
        var selectedEffort = ResolveAndValidateEffort(profile, request.Effort, errors);
        var selectedFastMode = ResolveAndValidateFastMode(profile, request.FastMode, errors);
        var selectedServiceTier = selectedFastMode ? CodexServiceTiers.Fast : ResolveDefaultServiceTier(profile, errors);

        return new SelectedDispatchOptions(selectedModel, selectedEffort, selectedFastMode, selectedServiceTier);
    }

    private static string? ResolveAndValidateModel(ProfileOptions profile, string? requestedModel, List<PolicyValidationError> errors)
    {
        var defaultModel = NullIfWhiteSpace(profile.DefaultModel);
        if (profile.DefaultModel is not null && defaultModel is null)
        {
            errors.Add(new PolicyValidationError("invalid_model", "Default model cannot be blank.", "defaultModel"));
        }

        var requested = NullIfWhiteSpace(requestedModel);
        if (requestedModel is not null && requested is null)
        {
            errors.Add(new PolicyValidationError("invalid_model", "Model override cannot be blank.", "model"));
            return defaultModel;
        }

        if (requested is not null)
        {
            if (!profile.AllowModelOverride)
            {
                errors.Add(new PolicyValidationError("model_override_disallowed", "Model override is not allowed by the selected profile.", "model"));
            }

            if (profile.AllowedModels.Count > 0
                && !profile.AllowedModels.Contains(requested, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(new PolicyValidationError("model_not_allowed", "Requested model is not allowed by the selected profile.", "model"));
            }

            return requested;
        }

        if (defaultModel is not null
            && profile.AllowedModels.Count > 0
            && !profile.AllowedModels.Contains(defaultModel, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(new PolicyValidationError("model_not_allowed", "Default model is not allowed by the selected profile.", "defaultModel"));
        }

        return defaultModel;
    }

    private static string? ResolveAndValidateEffort(ProfileOptions profile, string? requestedEffort, List<PolicyValidationError> errors)
    {
        var defaultEffort = NormalizeOptionalEffort(profile.DefaultEffort, "defaultEffort", errors);
        var allowedEfforts = NormalizeAllowedEfforts(profile, errors);

        string? requested = null;
        if (requestedEffort is not null)
        {
            if (!CodexEfforts.TryNormalize(requestedEffort, out var normalized))
            {
                errors.Add(new PolicyValidationError("invalid_effort", "Effort must be one of: none, minimal, low, medium, high, xhigh.", "effort"));
                return defaultEffort;
            }

            requested = normalized;
        }

        var selectedEffort = requested ?? defaultEffort;
        if (requested is not null && !profile.AllowEffortOverride)
        {
            errors.Add(new PolicyValidationError("effort_override_disallowed", "Effort override is not allowed by the selected profile.", "effort"));
        }

        if (selectedEffort is not null
            && allowedEfforts.Count > 0
            && !allowedEfforts.Contains(selectedEffort, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(new PolicyValidationError("effort_not_allowed", "Selected effort is not allowed by the selected profile.", requested is null ? "defaultEffort" : "effort"));
        }

        return selectedEffort;
    }

    private static bool ResolveAndValidateFastMode(ProfileOptions profile, bool? requestedFastMode, List<PolicyValidationError> errors)
    {
        var defaultFastMode = profile.FastMode
            || string.Equals(profile.DefaultServiceTier, CodexServiceTiers.Fast, StringComparison.OrdinalIgnoreCase);
        var selectedFastMode = requestedFastMode ?? defaultFastMode;

        if (requestedFastMode.HasValue && !profile.AllowFastModeOverride)
        {
            errors.Add(new PolicyValidationError("fast_mode_override_disallowed", "Fast mode override is not allowed by the selected profile.", "fastMode"));
        }

        if (profile.RequireFastMode && selectedFastMode is false)
        {
            errors.Add(new PolicyValidationError("fast_mode_required", "Selected profile requires fast mode.", "fastMode"));
        }

        return selectedFastMode;
    }

    private static string ResolveDefaultServiceTier(ProfileOptions profile, List<PolicyValidationError> errors)
    {
        if (profile.FastMode)
        {
            return CodexServiceTiers.Fast;
        }

        if (string.IsNullOrWhiteSpace(profile.DefaultServiceTier))
        {
            return CodexServiceTiers.Normal;
        }

        if (!CodexServiceTiers.TryNormalize(profile.DefaultServiceTier, out var serviceTier))
        {
            errors.Add(new PolicyValidationError("invalid_service_tier", "Service tier must be fast, normal, or flex.", "defaultServiceTier"));
            return CodexServiceTiers.Normal;
        }

        return serviceTier;
    }

    private static string? NormalizeOptionalEffort(string? effort, string field, List<PolicyValidationError> errors)
    {
        if (effort is null)
        {
            return null;
        }

        if (!CodexEfforts.TryNormalize(effort, out var normalized))
        {
            errors.Add(new PolicyValidationError("invalid_effort", "Effort must be one of: none, minimal, low, medium, high, xhigh.", field));
            return null;
        }

        return normalized;
    }

    private static IReadOnlyList<string> NormalizeAllowedEfforts(ProfileOptions profile, List<PolicyValidationError> errors)
    {
        var normalizedEfforts = new List<string>();
        foreach (var effort in profile.AllowedEfforts)
        {
            if (string.IsNullOrWhiteSpace(effort))
            {
                continue;
            }

            if (!CodexEfforts.TryNormalize(effort, out var normalized))
            {
                errors.Add(new PolicyValidationError("invalid_effort", "Allowed effort must be one of: none, minimal, low, medium, high, xhigh.", "allowedEfforts"));
                continue;
            }

            normalizedEfforts.Add(normalized);
        }

        return normalizedEfforts;
    }

    private static int NormalizeMaxConcurrentJobs(ProfileOptions profile) =>
        profile.MaxConcurrentJobs <= 0 ? 1 : profile.MaxConcurrentJobs;

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
