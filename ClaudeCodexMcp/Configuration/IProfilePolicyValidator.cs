using ClaudeCodexMcp.Domain;

namespace ClaudeCodexMcp.Configuration;

public interface IProfilePolicyValidator
{
    PolicyValidationResult<ValidatedDispatchPolicy> ValidateStartDispatch(StartDispatchRequest request);

    PolicyValidationResult<ProfilePolicySummary> GetProfileSummary(string? profileName);
}
