namespace ZX0ai.Core.Audio;

/// <summary>
/// Turns a block of mono PCM into the two numbers the orb consumes: a normalised
/// broadband loudness and eight normalised log-spaced spectrum bands.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately free of WinRT and device access so the whole audio pipeline can be
/// exercised in unit tests. <see cref="ZX0ai"/>'s AudioService only feeds it samples.
/// </para>
/// <para>
/// Levels are mapped through decibels rather than linear amplitude. Linear RMS spends
/// almost its entire range in the top few percent of loudness, so a linear mapping
/// makes the orb look dead until the user shouts.
/// </para>
/// </remarks>
public sealed class SpectrumAnalyzer
{
    /// <summary>Analysis window length. Power of two, required by the radix-2 FFT.</summary>
    public const int FftSize = 1024;

    public const int BandCount = 8;

    private const float LowestBandHz = 60f;
    private const float HighestBandHz = 8000f;

    // Loudness windows in dBFS, mapped onto 0..1.
    private const float RmsFloorDb = -62f;
    private const float RmsCeilingDb = -8f;
    private const float BandFloorDb = -78f;
    private const float BandCeilingDb = -18f;

    /// <summary>Coherent gain of the Hann window; magnitudes are divided by it.</summary>
    private const float HannCoherentGain = 0.5f;

    private const float Epsilon = 1e-9f;

    private readonly float[] _window = new float[FftSize];
    private readonly float[] _real = new float[FftSize];
    private readonly float[] _imag = new float[FftSize];
    private readonly int[] _reversed = new int[FftSize];
    private readonly int[] _bandStartBin = new int[BandCount];
    private readonly int[] _bandEndBin = new int[BandCount];

    public SpectrumAnalyzer(float sampleRate)
    {
        SampleRate = sampleRate > 0f ? sampleRate : 48000f;

        BuildHannWindow();
        BuildBitReversalTable();
        BuildBandEdges();
    }

    public float SampleRate { get; }

    // ------------------------------------------------------------------ //
    // Public analysis
    // ------------------------------------------------------------------ //

    /// <summary>Root-mean-square amplitude of a block, in linear units.</summary>
    public static float ComputeRms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return 0f;
        }

        var sum = 0d;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Maps a linear RMS onto 0..1 through the loudness window.</summary>
    public static float NormalizeLoudness(float rms) =>
        NormalizeDecibels(rms, RmsFloorDb, RmsCeilingDb);

    /// <summary>
    /// Fills <paramref name="bands"/> with <see cref="BandCount"/> normalised
    /// magnitudes, lowest frequency first.
    /// </summary>
    /// <param name="samples">
    /// Mono samples. Blocks shorter than <see cref="FftSize"/> are zero-padded;
    /// longer blocks use their most recent <see cref="FftSize"/> samples.
    /// </param>
    public void Analyze(ReadOnlySpan<float> samples, Span<float> bands)
    {
        if (bands.Length < BandCount)
        {
            throw new ArgumentException(
                $"Destination must hold at least {BandCount} bands.", nameof(bands));
        }

        LoadWindowedFrame(samples);
        Transform();
        ReduceToBands(bands);
    }

    // ------------------------------------------------------------------ //
    // Setup
    // ------------------------------------------------------------------ //

    private void BuildHannWindow()
    {
        for (var i = 0; i < FftSize; i++)
        {
            _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (FftSize - 1)));
        }
    }

    private void BuildBitReversalTable()
    {
        var bits = BitOperations_Log2(FftSize);

        for (var i = 0; i < FftSize; i++)
        {
            var reversed = 0;
            for (var bit = 0; bit < bits; bit++)
            {
                if ((i & (1 << bit)) != 0)
                {
                    reversed |= 1 << (bits - 1 - bit);
                }
            }

            _reversed[i] = reversed;
        }
    }

    /// <summary>
    /// Log-spaced band edges. Even spacing would put seven of eight bands above 4 kHz,
    /// where speech carries almost no energy, so the orb would barely move.
    /// </summary>
    private void BuildBandEdges()
    {
        var ratio = HighestBandHz / LowestBandHz;
        var maxBin = (FftSize / 2) - 1;
        var binsPerHz = FftSize / SampleRate;

        for (var band = 0; band < BandCount; band++)
        {
            var low = LowestBandHz * MathF.Pow(ratio, (float)band / BandCount);
            var high = LowestBandHz * MathF.Pow(ratio, (band + 1f) / BandCount);

            var start = (int)MathF.Round(low * binsPerHz);
            var end = (int)MathF.Round(high * binsPerHz);

            // Bin 0 is DC and carries no useful information for a level meter.
            start = Math.Clamp(start, 1, maxBin);
            end = Math.Clamp(end, start, maxBin);

            _bandStartBin[band] = start;
            _bandEndBin[band] = end;
        }
    }

    // ------------------------------------------------------------------ //
    // Pipeline
    // ------------------------------------------------------------------ //

    private void LoadWindowedFrame(ReadOnlySpan<float> samples)
    {
        // Keep the most recent FftSize samples: the orb should react to now, not to
        // whatever happened at the start of an oversized block.
        var source = samples.Length > FftSize ? samples[^FftSize..] : samples;
        var count = source.Length;

        for (var i = 0; i < count; i++)
        {
            _real[i] = source[i] * _window[i];
            _imag[i] = 0f;
        }

        for (var i = count; i < FftSize; i++)
        {
            _real[i] = 0f;
            _imag[i] = 0f;
        }
    }

    /// <summary>In-place iterative radix-2 Cooley-Tukey FFT, decimation in time.</summary>
    private void Transform()
    {
        for (var i = 0; i < FftSize; i++)
        {
            var j = _reversed[i];
            if (i >= j)
            {
                continue;
            }

            (_real[i], _real[j]) = (_real[j], _real[i]);
            (_imag[i], _imag[j]) = (_imag[j], _imag[i]);
        }

        for (var length = 2; length <= FftSize; length <<= 1)
        {
            var angle = -2f * MathF.PI / length;
            var stepReal = MathF.Cos(angle);
            var stepImag = MathF.Sin(angle);
            var half = length / 2;

            for (var start = 0; start < FftSize; start += length)
            {
                var twiddleReal = 1f;
                var twiddleImag = 0f;

                for (var offset = 0; offset < half; offset++)
                {
                    var a = start + offset;
                    var b = a + half;

                    var evenReal = _real[a];
                    var evenImag = _imag[a];

                    var oddReal = (_real[b] * twiddleReal) - (_imag[b] * twiddleImag);
                    var oddImag = (_real[b] * twiddleImag) + (_imag[b] * twiddleReal);

                    _real[a] = evenReal + oddReal;
                    _imag[a] = evenImag + oddImag;
                    _real[b] = evenReal - oddReal;
                    _imag[b] = evenImag - oddImag;

                    var nextReal = (twiddleReal * stepReal) - (twiddleImag * stepImag);
                    twiddleImag = (twiddleReal * stepImag) + (twiddleImag * stepReal);
                    twiddleReal = nextReal;
                }
            }
        }
    }

    private void ReduceToBands(Span<float> bands)
    {
        for (var band = 0; band < BandCount; band++)
        {
            var peak = 0f;

            for (var bin = _bandStartBin[band]; bin <= _bandEndBin[band]; bin++)
            {
                var magnitude = MathF.Sqrt((_real[bin] * _real[bin]) + (_imag[bin] * _imag[bin]));
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            // Single-sided amplitude, corrected for the window's coherent gain.
            var amplitude = 2f * peak / (FftSize * HannCoherentGain);
            bands[band] = NormalizeDecibels(amplitude, BandFloorDb, BandCeilingDb);
        }
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static float NormalizeDecibels(float amplitude, float floorDb, float ceilingDb)
    {
        var decibels = 20f * MathF.Log10(MathF.Abs(amplitude) + Epsilon);
        return Math.Clamp((decibels - floorDb) / (ceilingDb - floorDb), 0f, 1f);
    }

    private static int BitOperations_Log2(int value)
    {
        var bits = 0;
        while ((1 << bits) < value)
        {
            bits++;
        }

        return bits;
    }
}
