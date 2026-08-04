namespace ZX0ai.Core.Models;

/// <summary>
/// Visual/behavioural state of the signature orb. Drives the Win2D render loop.
/// </summary>
public enum OrbState
{
    /// <summary>Ambient breathing, gentle swirl, soft bloom.</summary>
    Idle,

    /// <summary>Surface ripples driven by live microphone RMS and FFT bands.</summary>
    Listening,

    /// <summary>Faster turbulence, tighter core — a model or team is working.</summary>
    Thinking,

    /// <summary>Pulses to output cadence while text streams or TTS plays.</summary>
    Speaking,
}
