using Xunit;
using ZX0ai.Core.Agents;
using ZX0ai.Core.Models;

namespace ZX0ai.Tests;

/// <summary>
/// Guards the product surface: DXM is one model, and that is all the customer sees.
/// </summary>
/// <remarks>
/// Provider slugs and agent roles stay on the domain objects, because orchestration,
/// logging and capability negotiation genuinely need them. Nothing derived for display
/// may carry either a vendor name or evidence that a team exists.
/// </remarks>
public sealed class ZxaBrandingTests
{
    [Fact]
    public void ProductName_IsDxm()
    {
        Assert.Equal("DXM", ZxaBranding.ProductName);
    }

    /// <summary>
    /// Attribution is deliberately identical for every role. If it varied, the
    /// transcript would let a customer count the agents.
    /// </summary>
    [Fact]
    public void EveryRole_IsAttributedToTheSameSingleModel()
    {
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            Assert.Equal("DXM", ZxaBranding.AttributionFor(role));
            Assert.Equal("DXM", ZxaBranding.CallsignFor(role));
        }
    }

    [Fact]
    public void EveryAttribution_IsSafeToRender()
    {
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var label = ZxaBranding.AttributionFor(role);

            Assert.True(
                ZxaBranding.IsSafeForCustomer(label),
                $"Attribution for {role} is not customer-safe: {label}");
        }
    }

    [Theory]
    [InlineData("anthropic/claude-fable-5")]
    [InlineData("openai/gpt-5.6-sol")]
    [InlineData("moonshotai/kimi-k3")]
    [InlineData("google/gemma-4-31b-it:free")]
    [InlineData("nvidia/nemotron-3-ultra-550b-a55b:free")]
    [InlineData("Claude Fable 5")]
    [InlineData("GPT-5.6 Sol")]
    public void LooksLikeVendorIdentifier_CatchesSlugsAndVendorNames(string candidate)
    {
        Assert.True(ZxaBranding.LooksLikeVendorIdentifier(candidate));
    }

    /// <summary>
    /// The second class of leak: labels with no vendor in them that still reveal the
    /// machinery. These were all legitimate UI strings before the DXM rebrand.
    /// </summary>
    [Theory]
    [InlineData("zxa-Lite")]
    [InlineData("zxa-Medium")]
    [InlineData("zxa-Ultra-full-max")]
    [InlineData("ZXA Lead")]
    [InlineData("ZXA · zxa-Medium")]
    [InlineData("Leader")]
    [InlineData("Reviewer")]
    [InlineData("debate-then-synthesize")]
    [InlineData("leader-delegate")]
    [InlineData("pipeline")]
    [InlineData("Agent Team")]
    public void LooksLikeInternalIdentifier_CatchesTheOldSurface(string candidate)
    {
        Assert.True(
            ZxaBranding.LooksLikeInternalIdentifier(candidate),
            $"'{candidate}' would tell the customer there is a team behind DXM.");
        Assert.False(ZxaBranding.IsSafeForCustomer(candidate));
    }

    [Theory]
    [InlineData("DXM")]
    [InlineData("DXM Ultra")]
    [InlineData("48 tok/s")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSafeForCustomer_AcceptsTheDxmSurface(string? candidate)
    {
        Assert.True(ZxaBranding.IsSafeForCustomer(candidate));
    }

    [Theory]
    [InlineData("zxa-Ultra-full-max", "DXM Ultra")]
    [InlineData("ZXA-ULTRA", "DXM Ultra")]
    [InlineData("zxa-Pro", "DXM Pro")]
    [InlineData("zxa-Medium", "DXM Standard")]
    [InlineData("zxa-medim", "DXM Standard")]
    [InlineData("zxa-Lite", "DXM Fast")]
    [InlineData("zxa-Light", "DXM Fast")]
    [InlineData("zxa-Low", "DXM Mini")]
    [InlineData("zxa-very-low-free", "DXM Mini")]
    [InlineData("zxa-something-unmapped", "DXM")]
    [InlineData(null, "DXM")]
    public void TierTag_NeverEchoesTheTierKey(string? tierKey, string expected)
    {
        var tag = ZxaBranding.TierTag(tierKey);

        Assert.Equal(expected, tag);
        Assert.True(ZxaBranding.IsSafeForCustomer(tag));
    }

    /// <summary>
    /// The team is built from provider slugs, and every label derived from it for
    /// display still comes back as the one product name.
    /// </summary>
    [Fact]
    public void TeamBuiltFromVendorSlugs_StillRendersAsOneModel()
    {
        var tier = new ModelTier
        {
            Key = "zxa-Ultra-full-max",
            DisplayName = "zxa-Ultra-full-max",
            Mode = TeamMode.Team,
            Leader = "anthropic/claude-fable-5",
            Members =
            [
                new TeamMember { Role = AgentRole.Coder, Model = "openai/gpt-5.6-sol" },
                new TeamMember { Role = AgentRole.Researcher, Model = "moonshotai/kimi-k3" },
            ],
        };

        var team = AgentFactory.BuildTeam(tier, Constitution.Default());

        Assert.NotEmpty(team);

        // Distinct roles and distinct vendors inside...
        Assert.True(team.Select(a => a.Role).Distinct().Count() > 1);

        foreach (var agent in team)
        {
            Assert.True(ZxaBranding.LooksLikeVendorIdentifier(agent.Model));
        }

        // ...and exactly one name outside.
        var rendered = team.Select(a => ZxaBranding.AttributionFor(a.Role)).Distinct().ToList();

        Assert.Equal(["DXM"], rendered);
    }

    /// <summary>
    /// The tier the customer selects is surfaced as a capability, so the raw key must
    /// never be the thing rendered.
    /// </summary>
    [Theory]
    [InlineData("zxa-Lite")]
    [InlineData("zxa-Medium")]
    [InlineData("zxa-Ultra-full-max")]
    public void ShippedTierKeys_AreNeverRenderedRaw(string tierKey)
    {
        Assert.False(ZxaBranding.IsSafeForCustomer(tierKey));
        Assert.True(ZxaBranding.IsSafeForCustomer(ZxaBranding.TierTag(tierKey)));
    }
}
