using System.Globalization;

namespace ZX0ai.Core.Models;

/// <summary>
/// The rule behind the throughput readout in the title bar.
/// </summary>
/// <remarks>
/// <para>
/// The readout is a claim about DXM Ultra, not a status light: it appears only while
/// Ultra is actually producing tokens, and it always carries a rate that was measured.
/// A placeholder such as "– tok/s" would be worse than nothing, because the customer
/// would read it as a real reading of zero.
/// </para>
/// <para>
/// The rule lives in Core rather than in the view so it can be tested without a
/// dispatcher, and so the view has no room to reinterpret it.
/// </para>
/// </remarks>
public static class SpeedIndicator
{
    /// <summary>
    /// Below one token per second there is no measurement yet, only the first few
    /// characters of a stream. Nothing is shown until the sampler has a real figure.
    /// </summary>
    public const double MinimumMeasuredRate = 1d;

    /// <summary>
    /// True when the readout may be shown: Ultra, generating, and measured.
    /// </summary>
    /// <param name="tierKey">The active tier key.</param>
    /// <param name="isGenerating">True while a response is streaming.</param>
    /// <param name="tokensPerSecond">The sampled rate; zero before the first sample.</param>
    public static bool ShouldShow(string? tierKey, bool isGenerating, double tokensPerSecond) =>
        isGenerating &&
        ZxaBranding.IsUltraTier(tierKey) &&
        tokensPerSecond >= MinimumMeasuredRate;

    /// <summary>
    /// Formats a measured rate as an LTR island: western digits and a Latin unit, so it
    /// reads identically inside an Arabic right-to-left interface.
    /// </summary>
    public static string Format(double tokensPerSecond) =>
        string.Create(CultureInfo.InvariantCulture, $"{tokensPerSecond:F0} tok/s");
}
