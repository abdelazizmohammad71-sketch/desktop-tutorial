using Xunit;
using ZX0ai.Core.Audio;
using ZX0ai.Core.Services;

namespace ZX0ai.Tests;

/// <summary>
/// Covers the audio maths behind the reactive orb. Everything here is deterministic:
/// no device, no graph, no timing.
/// </summary>
public sealed class SpectrumAnalyzerTests
{
    private const float SampleRate = 48000f;

    private static float[] Sine(float frequency, float amplitude, int length, float sampleRate = SampleRate)
    {
        var samples = new float[length];
        for (var i = 0; i < length; i++)
        {
            samples[i] = amplitude * MathF.Sin(2f * MathF.PI * frequency * i / sampleRate);
        }

        return samples;
    }

    // ------------------------------------------------------------------ //
    // RMS
    // ------------------------------------------------------------------ //

    [Fact]
    public void ComputeRms_OfSilence_IsZero()
    {
        Assert.Equal(0f, SpectrumAnalyzer.ComputeRms(new float[512]));
    }

    [Fact]
    public void ComputeRms_OfEmptyBlock_IsZero()
    {
        Assert.Equal(0f, SpectrumAnalyzer.ComputeRms(ReadOnlySpan<float>.Empty));
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(0.5f)]
    [InlineData(0.1f)]
    public void ComputeRms_OfSine_IsAmplitudeOverRootTwo(float amplitude)
    {
        // A whole number of cycles keeps the expectation exact.
        var samples = Sine(frequency: 1000f, amplitude, length: 4800);

        var rms = SpectrumAnalyzer.ComputeRms(samples);

        Assert.Equal(amplitude / MathF.Sqrt(2f), rms, 0.001f);
    }

    [Fact]
    public void ComputeRms_OfDc_IsTheDcLevel()
    {
        var samples = new float[256];
        Array.Fill(samples, 0.25f);

        Assert.Equal(0.25f, SpectrumAnalyzer.ComputeRms(samples), 0.0001f);
    }

    // ------------------------------------------------------------------ //
    // Loudness normalisation
    // ------------------------------------------------------------------ //

    [Fact]
    public void NormalizeLoudness_OfSilence_IsZero()
    {
        Assert.Equal(0f, SpectrumAnalyzer.NormalizeLoudness(0f));
    }

    [Fact]
    public void NormalizeLoudness_OfFullScale_IsOne()
    {
        Assert.Equal(1f, SpectrumAnalyzer.NormalizeLoudness(1f));
    }

    [Fact]
    public void NormalizeLoudness_IsMonotonic()
    {
        var previous = -1f;

        foreach (var rms in new[] { 0f, 0.0005f, 0.005f, 0.02f, 0.08f, 0.3f, 0.7f, 1f })
        {
            var value = SpectrumAnalyzer.NormalizeLoudness(rms);
            Assert.InRange(value, 0f, 1f);
            Assert.True(value >= previous, $"Loudness went backwards at rms {rms}.");
            previous = value;
        }
    }

    [Fact]
    public void NormalizeLoudness_UsesDecibels_SoQuietSpeechStillMoves()
    {
        // A linear mapping would put this near 0.02 and the orb would look dead.
        var quiet = SpectrumAnalyzer.NormalizeLoudness(0.02f);

        Assert.InRange(quiet, 0.2f, 0.8f);
    }

    // ------------------------------------------------------------------ //
    // Spectrum
    // ------------------------------------------------------------------ //

    [Fact]
    public void Analyze_OfSilence_ProducesNoEnergy()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate);
        var bands = new float[SpectrumAnalyzer.BandCount];

        analyzer.Analyze(new float[SpectrumAnalyzer.FftSize], bands);

        Assert.All(bands, band => Assert.Equal(0f, band));
    }

    [Fact]
    public void Analyze_PutsALowToneInALowBand()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate);
        var bands = new float[SpectrumAnalyzer.BandCount];

        analyzer.Analyze(Sine(120f, 0.8f, SpectrumAnalyzer.FftSize), bands);

        var loudest = Array.IndexOf(bands, bands.Max());
        Assert.InRange(loudest, 0, 2);
    }

    [Fact]
    public void Analyze_PutsAHighToneInAHighBand()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate);
        var bands = new float[SpectrumAnalyzer.BandCount];

        analyzer.Analyze(Sine(6000f, 0.8f, SpectrumAnalyzer.FftSize), bands);

        var loudest = Array.IndexOf(bands, bands.Max());
        Assert.InRange(loudest, 5, SpectrumAnalyzer.BandCount - 1);
    }

    [Fact]
    public void Analyze_SeparatesTones_SoBandsAreNotAllTheSame()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate);
        var low = new float[SpectrumAnalyzer.BandCount];
        var high = new float[SpectrumAnalyzer.BandCount];

        analyzer.Analyze(Sine(120f, 0.8f, SpectrumAnalyzer.FftSize), low);
        analyzer.Analyze(Sine(6000f, 0.8f, SpectrumAnalyzer.FftSize), high);

        // The band that dominates for bass must not be the one that dominates for treble.
        Assert.NotEqual(Array.IndexOf(low, low.Max()), Array.IndexOf(high, high.Max()));
    }

    [Fact]
    public void Analyze_AlwaysProducesNormalisedValues()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate);
        var bands = new float[SpectrumAnalyzer.BandCount];

        // Deliberately over-driven: clipping must not push bands outside 0..1.
        analyzer.Analyze(Sine(1000f, 8f, SpectrumAnalyzer.FftSize), bands);

        Assert.All(bands, band => Assert.InRange(band, 0f, 1f));
    }

    [Fact]
    public void Analyze_LouderToneProducesAHigherBand()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate);
        var quiet = new float[SpectrumAnalyzer.BandCount];
        var loud = new float[SpectrumAnalyzer.BandCount];

        analyzer.Analyze(Sine(1000f, 0.02f, SpectrumAnalyzer.FftSize), quiet);
        analyzer.Analyze(Sine(1000f, 0.60f, SpectrumAnalyzer.FftSize), loud);

        Assert.True(loud.Max() > quiet.Max());
    }

    [Fact]
    public void Analyze_AcceptsShortBlocks_ByZeroPadding()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate);
        var bands = new float[SpectrumAnalyzer.BandCount];

        analyzer.Analyze(Sine(1000f, 0.5f, 480), bands);

        Assert.All(bands, band => Assert.InRange(band, 0f, 1f));
        Assert.True(bands.Max() > 0f);
    }

    [Fact]
    public void Analyze_UsesTheMostRecentSamplesOfAnOversizedBlock()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate);
        var bands = new float[SpectrumAnalyzer.BandCount];

        // Silence first, then a tone: the tone is what the orb should react to.
        var samples = new float[SpectrumAnalyzer.FftSize * 2];
        Sine(6000f, 0.8f, SpectrumAnalyzer.FftSize).CopyTo(samples, SpectrumAnalyzer.FftSize);

        analyzer.Analyze(samples, bands);

        Assert.True(bands.Max() > 0.3f);
    }

    [Fact]
    public void Analyze_RejectsAnUndersizedDestination()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate);

        Assert.Throws<ArgumentException>(() =>
        {
            Span<float> tooSmall = new float[SpectrumAnalyzer.BandCount - 1];
            analyzer.Analyze(new float[SpectrumAnalyzer.FftSize], tooSmall);
        });
    }

    [Theory]
    [InlineData(16000f)]
    [InlineData(44100f)]
    [InlineData(48000f)]
    public void Analyze_WorksAcrossDeviceSampleRates(float sampleRate)
    {
        var analyzer = new SpectrumAnalyzer(sampleRate);
        var bands = new float[SpectrumAnalyzer.BandCount];

        analyzer.Analyze(Sine(1000f, 0.6f, SpectrumAnalyzer.FftSize, sampleRate), bands);

        Assert.All(bands, band => Assert.InRange(band, 0f, 1f));
        Assert.True(bands.Max() > 0f);
    }

    [Fact]
    public void Constructor_FallsBackWhenTheDeviceReportsNoSampleRate()
    {
        Assert.Equal(48000f, new SpectrumAnalyzer(0f).SampleRate);
    }

    // ------------------------------------------------------------------ //
    // Contract
    // ------------------------------------------------------------------ //

    [Fact]
    public void AudioLevel_Silent_MatchesTheAnalyzerBandCount()
    {
        Assert.Equal(SpectrumAnalyzer.BandCount, AudioLevel.BandCount);
        Assert.Equal(AudioLevel.BandCount, AudioLevel.Silent.Bands.Count);
        Assert.Equal(0f, AudioLevel.Silent.Rms);
    }
}
