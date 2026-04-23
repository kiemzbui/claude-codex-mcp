using System.Linq;
using ClaudeCodexMcp.Workflows;

namespace ClaudeCodexMcp.Tests.Workflows;

public sealed class CanonicalWorkflowTests
{
    [Fact]
    public void CanonicalWorkflowSetContainsEveryMvpWorkflow()
    {
        Assert.Contains(CanonicalWorkflows.Direct, CanonicalWorkflows.All);
        Assert.Contains(CanonicalWorkflows.SubagentManager, CanonicalWorkflows.All);
        Assert.Contains(CanonicalWorkflows.PrepareOrchestratePlan, CanonicalWorkflows.All);
        Assert.Contains(CanonicalWorkflows.ManagedPlan, CanonicalWorkflows.All);
        Assert.Contains(CanonicalWorkflows.OrchestrateExecute, CanonicalWorkflows.All);
        Assert.Contains(CanonicalWorkflows.OrchestrateRevise, CanonicalWorkflows.All);
        Assert.Equal(6, CanonicalWorkflows.All.Distinct().Count());
    }

    [Fact]
    public void WorkflowNormalizationRejectsUnknownNames()
    {
        var accepted = CanonicalWorkflows.TryNormalize("DIRECT", out var normalized);
        var rejected = CanonicalWorkflows.TryNormalize("invented_workflow", out _);

        Assert.True(accepted);
        Assert.Equal(CanonicalWorkflows.Direct, normalized);
        Assert.False(rejected);
    }
}
