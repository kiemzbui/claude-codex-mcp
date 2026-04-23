using System.Collections.Generic;

namespace ClaudeCodexMcp.Workflows;

public static class CanonicalWorkflows
{
    public const string Direct = "direct";
    public const string SubagentManager = "subagent_manager";
    public const string PrepareOrchestratePlan = "prepare_orchestrate_plan";
    public const string ManagedPlan = "managed_plan";
    public const string OrchestrateExecute = "orchestrate_execute";
    public const string OrchestrateRevise = "orchestrate_revise";

    private static readonly Dictionary<string, string> CanonicalByName = new(StringComparer.OrdinalIgnoreCase)
    {
        [Direct] = Direct,
        [SubagentManager] = SubagentManager,
        [PrepareOrchestratePlan] = PrepareOrchestratePlan,
        [ManagedPlan] = ManagedPlan,
        [OrchestrateExecute] = OrchestrateExecute,
        [OrchestrateRevise] = OrchestrateRevise
    };

    public static IReadOnlyCollection<string> All => CanonicalByName.Values;

    public static bool TryNormalize(string? workflow, out string normalized)
    {
        if (!string.IsNullOrWhiteSpace(workflow)
            && CanonicalByName.TryGetValue(workflow.Trim(), out var canonical))
        {
            normalized = canonical;
            return true;
        }

        normalized = string.Empty;
        return false;
    }
}

