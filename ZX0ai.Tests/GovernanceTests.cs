using Xunit;
using ZX0ai.Core.Agents;
using ZX0ai.Core.Governance;
using ZX0ai.Core.Models;

namespace ZX0ai.Tests;

/// <summary>
/// The governance layer: how work gets classified, and what cannot happen without a
/// human saying yes.
/// </summary>
public sealed class GovernanceTests
{
    [Theory]
    [InlineData("Update the README wording", RiskTier.Low)]
    [InlineData("Add a new internal endpoint", RiskTier.Medium)]
    [InlineData("Add a dependency on Serilog", RiskTier.Medium)]
    [InlineData("Change the auth middleware", RiskTier.High)]
    [InlineData("Store the customer data in a new column", RiskTier.High)]
    [InlineData("Run a schema migration", RiskTier.High)]
    [InlineData("Drop table orders", RiskTier.Critical)]
    [InlineData("Rotate key for the payment provider", RiskTier.Critical)]
    [InlineData("rm -rf the build output", RiskTier.Critical)]
    public void Classify_AssignsTheTierTheWorkDeserves(string task, RiskTier expected)
    {
        Assert.Equal(expected, RiskClassifier.Classify(task).Tier);
    }

    /// <summary>
    /// The central safety property. A model has every incentive to under-report risk to
    /// get on with the work, so its own claim can only ever raise the tier.
    /// </summary>
    [Fact]
    public void DeclaredTier_CanRaiseButNeverLower()
    {
        var understated = RiskClassifier.Classify(
            "Drop table orders",
            declared: RiskTier.Low);

        Assert.Equal(RiskTier.Critical, understated.Tier);

        var overstated = RiskClassifier.Classify(
            "Fix a typo in the README",
            declared: RiskTier.High);

        Assert.Equal(RiskTier.High, overstated.Tier);
    }

    [Theory]
    [InlineData(RiskTier.Low, false)]
    [InlineData(RiskTier.Medium, false)]
    [InlineData(RiskTier.High, true)]
    [InlineData(RiskTier.Critical, true)]
    public void ApprovalIsRequired_ForHighAndAbove(RiskTier tier, bool expected)
    {
        Assert.Equal(expected, new RiskAssessment(tier, "test").RequiresApproval);
    }

    [Fact]
    public void RollbackPlan_IsMandatoryOnlyForCritical()
    {
        Assert.True(new RiskAssessment(RiskTier.Critical, "t").RequiresRollbackPlan);
        Assert.False(new RiskAssessment(RiskTier.High, "t").RequiresRollbackPlan);
    }

    [Fact]
    public void UnknownTierName_IsIgnoredRatherThanTrusted()
    {
        Assert.Null(RiskClassifier.ParseTier("catastrophic"));
        Assert.Null(RiskClassifier.ParseTier(null));
        Assert.Equal(RiskTier.Critical, RiskClassifier.ParseTier("critical"));
    }

    [Fact]
    public void Plan_CarriesRiskAndRollbackOutOfTheBlock()
    {
        var plan = LeaderPlan.Parse("""
            ```dxm-plan
            {
              "mode": "delegate",
              "summary": "Rotate the signing key.",
              "risk": "Critical",
              "rollback": "Restore the previous key from the vault.",
              "assignments": [ { "role": "Coder", "task": "Swap the key reference." } ]
            }
            ```
            """);

        Assert.Equal(RiskTier.Critical, plan.Risk.Tier);
        Assert.Equal("Restore the previous key from the vault.", plan.RollbackPlan);
        Assert.True(plan.Risk.RequiresApproval);
    }

    /// <summary>
    /// A plan that says "Low" while describing a production deploy is classified from
    /// what it describes, not from what it claims.
    /// </summary>
    [Fact]
    public void Plan_IsClassifiedFromItsContentNotItsClaim()
    {
        var plan = LeaderPlan.Parse("""
            ```dxm-plan
            {
              "mode": "delegate",
              "summary": "Routine change.",
              "risk": "Low",
              "assignments": [ { "role": "Coder", "task": "Deploy to production." } ]
            }
            ```
            """);

        Assert.Equal(RiskTier.Critical, plan.Risk.Tier);
        Assert.True(plan.Risk.RequiresApproval);
    }

    [Fact]
    public void OrdinaryPlan_NeedsNoApproval()
    {
        var plan = LeaderPlan.Parse("""
            ```dxm-plan
            { "mode": "direct", "summary": "Explain how the parser works." }
            ```
            """);

        Assert.False(plan.Risk.RequiresApproval);
    }

    [Fact]
    public void RunOptions_DoNotGrantApprovalByDefault()
    {
        Assert.False(AgentRunOptions.Default.ApprovalGranted);
        Assert.False(AgentRunOptions.Plan.ApprovalGranted);
        Assert.True(AgentRunOptions.Approved.ApprovalGranted);
    }
}
