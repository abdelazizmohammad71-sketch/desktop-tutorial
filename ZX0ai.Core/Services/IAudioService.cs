using ZX0ai.Core.Audio;

namespace ZX0ai.Core.Services;

/// <summary>
/// A single analysed audio quantum: broadband loudness plus a coarse spectrum.
/// </summary>
/// <param name="Rms">Normalised loudness, 0..1, already mapped through the dB window.</param>
/// <param name="Bands">Eight normalised FFT band magnitudes, low to high.</param>
/// <param name="RawRms">
/// Linear RMS before normalisation. Not used for rendering, but it is the only value
/// that distinguishes a silent room from a dead capture path, so the debug overlay
/// shows it and future auto-gain will read it.
/// </param>
public readonly record struct AudioLevel(
    float Rms,
    IReadOnlyList<float> Bands,
    float RawRms = 0f)
{
    public const int BandCount = SpectrumAnalyzer.BandCount;

    public static readonly AudioLevel Silent = new(0f, new float[BandCount]);
}

/// <summary>Why capture could not start, or why it stopped.</summary>
public enum AudioFailureReason
{
    /// <summary>Microphone access is blocked by privacy settings.</summary>
    AccessDenied,

    /// <summary>No capture device is present or it was unplugged.</summary>
    DeviceUnavailable,

    /// <summary>
    /// The device is open and delivering audio, but every sample is an exact zero:
    /// the input is muted or switched off in hardware.
    /// </summary>
    Muted,

    /// <summary>The device offers no format the graph can consume.</summary>
    FormatNotSupported,

    Unknown,
}

/// <param name="Reason">Machine-readable cause.</param>
/// <param name="ResourceKey">.resw key for the message shown to the user.</param>
public readonly record struct AudioFailure(AudioFailureReason Reason, string ResourceKey);

/// <summary>
/// Live microphone capture and analysis. The orb subscribes to
/// <see cref="AudioLevelChanged"/> and maps it onto displacement and glow.
/// </summary>
public interface IAudioService
{
    /// <summary>
    /// Raised on the audio thread for each analysed block. Handlers must marshal to
    /// the UI thread before touching anything XAML.
    /// </summary>
    event EventHandler<AudioLevel>? AudioLevelChanged;

    /// <summary>Raised when capture cannot start, or stops unexpectedly.</summary>
    event EventHandler<AudioFailure>? CaptureFailed;

    bool IsCapturing { get; }

    /// <summary>
    /// Starts capture. Returns false and raises <see cref="CaptureFailed"/> rather
    /// than throwing, so a missing or blocked microphone degrades gracefully.
    /// </summary>
    Task<bool> StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}
