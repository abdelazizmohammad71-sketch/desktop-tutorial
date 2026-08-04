using System.Globalization;
using Xunit;
using ZX0ai.Core.Models;

namespace ZX0ai.Tests;

/// <summary>
/// The throughput readout is the one performance claim DXM makes, so it is held to
/// the strict reading: Ultra only, while generating only, and only a measured rate.
/// </summary>
public sealed class SpeedIndicatorTests
{
    private const string Ultra = "zxa-Ultra-full-max";
    private const string Standard = "zxa-Medium";

    [Fact]
    public void Shown_WhenUltraIsGeneratingAtAMeasuredRate()
    {
        Assert.True(SpeedIndicator.ShouldShow(Ultra, isGenerating: true, tokensPerSecond: 48));
    }

    /// <summary>Idle is idle: the last rate of a finished turn is not news.</summary>
    [Fact]
    public void Hidden_WhenIdle()
    {
        Assert.False(SpeedIndicator.ShouldShow(Ultra, isGenerating: false, tokensPerSecond: 48));
    }

    [Theory]
    [InlineData(Standard)]
    [InlineData("zxa-Lite")]
    [InlineData("zxa-very-low-free")]
    [InlineData(null)]
    public void Hidden_OutsideUltra(string? tierKey)
    {
        Assert.False(SpeedIndicator.ShouldShow(tierKey, isGenerating: true, tokensPerSecond: 48));
    }

    /// <summary>
    /// Before the first sample the rate is zero. Zero is not a measurement, and showing
    /// it — or a dash in its place — would be a fabricated reading.
    /// </summary>
    [Theory]
    [InlineData(0d)]
    [InlineData(0.4d)]
    [InlineData(0.999d)]
    public void Hidden_BeforeTheFirstRealMeasurement(double rate)
    {
        Assert.False(SpeedIndicator.ShouldShow(Ultra, isGenerating: true, tokensPerSecond: rate));
    }

    [Fact]
    public void Shown_AtTheMeasurementThreshold()
    {
        Assert.True(SpeedIndicator.ShouldShow(
            Ultra, isGenerating: true, tokensPerSecond: SpeedIndicator.MinimumMeasuredRate));
    }

    /// <summary>
    /// A failed turn stops generating, which alone is enough to clear the readout: the
    /// view does not need a separate failure path to keep the value from lingering.
    /// </summary>
    [Fact]
    public void Hidden_AfterAFailedTurnLeavesAStaleRate()
    {
        var lastMeasuredRate = 62d;

        Assert.False(SpeedIndicator.ShouldShow(
            Ultra, isGenerating: false, tokensPerSecond: lastMeasuredRate));
    }

    [Theory]
    [InlineData(48d, "48 tok/s")]
    [InlineData(7.4d, "7 tok/s")]
    [InlineData(120.6d, "121 tok/s")]
    public void Format_IsAWholeNumberAndAUnit(double rate, string expected)
    {
        Assert.Equal(expected, SpeedIndicator.Format(rate));
    }

    /// <summary>
    /// The readout is an LTR island. Rendered under an Arabic culture it must still be
    /// western digits and a Latin unit, so it reads the same in either interface.
    /// </summary>
    [Fact]
    public void Format_IsCultureIndependent()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal("48 tok/s", SpeedIndicator.Format(48));

            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("48 tok/s", SpeedIndicator.Format(48));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>The readout names a rate and nothing else — no model, no provider.</summary>
    [Fact]
    public void Format_NamesNothingInternal()
    {
        Assert.True(ZxaBranding.IsSafeForCustomer(SpeedIndicator.Format(48)));
    }
}
