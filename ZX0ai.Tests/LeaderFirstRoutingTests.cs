using Xunit;
using ZX0ai.Core.Agents;
using ZX0ai.Core.Models;

namespace ZX0ai.Tests;

/// <summary>
/// The routing contract: the request reaches the leader and nobody else, and the leader
/// decides who — if anyone — is woken.
/// </summary>
public sealed class LeaderFirstRoutingTests
{
    [Fact]
    public void Direct_MeansNoOneElseRuns()
    {
        var plan = LeaderPlan.Parse("""
            I can answer this myself.

            ```dxm-plan
            { "mode": "direct", "summary": "Answer from what I already know." }
            ```
            """);

        Assert.Equal(LeaderIntent.Direct, plan.Intent);
        Assert.False(plan.HasAssignments);
        Assert.Equal("Answer from what I already know.", plan.Summary);
    }

    [Fact]
    public void Delegate_CarriesTheLeadersOwnWording()
    {
        var plan = LeaderPlan.Parse("""
            ```dxm-plan
            {
              "mode": "delegate",
              "assignments": [
                { "role": "Coder", "task": "Port the parser to the new schema." },
                { "role": "Reviewer", "task": "Check the migration for data loss." }
              ]
            }
            ```
            """);

        Assert.Equal(LeaderIntent.Delegate, plan.Intent);
        Assert.Equal(2, plan.Assignments.Count);
        Assert.Equal(AgentRole.Coder, plan.Assignments[0].Role);
        Assert.Equal("Port the parser to the new schema.", plan.Assignments[0].Task);
        Assert.Equal(AgentRole.Reviewer, plan.Assignments[1].Role);
    }

    /// <summary>
    /// Delegating to nobody is a contradiction, and running the whole team on it would
    /// be the opposite of what the leader asked for.
    /// </summary>
    [Fact]
    public void DelegateWithNoAssignments_BecomesDirect()
    {
        var plan = LeaderPlan.Parse("""
            ```dxm-plan
            { "mode": "delegate", "assignments": [] }
            ```
            """);

        Assert.Equal(LeaderIntent.Direct, plan.Intent);
    }

    [Theory]
    [InlineData("no block here at all")]
    [InlineData("```dxm-plan\nnot json\n```")]
    [InlineData("```dxm-plan\n{ \"mode\": }\n```")]
    [InlineData("")]
    [InlineData(null)]
    public void UnreadablePlan_FallsBackToConsultingTheTeam(string? response)
    {
        var plan = LeaderPlan.Parse(response);

        Assert.Equal(LeaderIntent.Delegate, plan.Intent);
        Assert.False(plan.HasAssignments);
    }

    [Fact]
    public void UnknownRoles_AreDropped()
    {
        var plan = LeaderPlan.Parse("""
            ```dxm-plan
            {
              "mode": "delegate",
              "assignments": [
                { "role": "Sorcerer", "task": "Something" },
                { "role": "Coder", "task": "Write the adapter." },
                { "role": "Reviewer", "task": "   " }
              ]
            }
            ```
            """);

        var assignment = Assert.Single(plan.Assignments);
        Assert.Equal(AgentRole.Coder, assignment.Role);
    }

    /// <summary>The block is internal routing and must not survive into the transcript.</summary>
    [Fact]
    public void StripBlock_RemovesTheMachineReadablePart()
    {
        const string Response = """
            Here is what I intend to do.

            ```dxm-plan
            { "mode": "delegate", "assignments": [ { "role": "Coder", "task": "x" } ] }
            ```
            """;

        var visible = LeaderPlan.StripBlock(Response);

        Assert.Equal("Here is what I intend to do.", visible);
        Assert.DoesNotContain("dxm-plan", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("Coder", visible, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unterminated fence must still swallow the block. Dropping trailing prose is
    /// the safe failure; printing raw routing JSON to the customer is not.
    /// </summary>
    [Fact]
    public void StripBlock_HandlesAnUnterminatedFence()
    {
        var visible = LeaderPlan.StripBlock("""
            Working on it.

            ```dxm-plan
            { "mode": "delegate", "assignments": [ { "role": "Planner", "task": "x" } ] }
            """);

        Assert.Equal("Working on it.", visible);
        Assert.DoesNotContain("Planner", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void BrainNote_IsCarriedOutOfThePlan()
    {
        var plan = LeaderPlan.Parse("""
            ```dxm-plan
            {
              "mode": "direct",
              "brain": "This project pins Serilog to 3.x; do not upgrade."
            }
            ```
            """);

        Assert.Equal("This project pins Serilog to 3.x; do not upgrade.", plan.BrainNote);
    }

    [Fact]
    public void PlanMode_IsOffByDefault()
    {
        Assert.False(AgentRunOptions.Default.PlanOnly);
        Assert.True(AgentRunOptions.Plan.PlanOnly);
    }
}
