using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Numerics;

namespace ZX0ai.Views.Controls;

/// <summary>
/// Fire-inspired particle effect for the Ultra Mode token counter.
/// Uses GPU-accelerated CanvasAnimatedControl — no GIF, no video, no CPU bitmap loops.
/// Activity scales with generation state; idle glow persists while Ultra is active.
/// Degrades to a static warm glow when reduced motion or energy saver is on.
/// </summary>
public sealed partial class UltraTokenEffect : UserControl
{
    private float _intensity = 0.3f;
    private float _targetIntensity = 0.3f;
    private bool _isGenerating;
    private bool _reducedMotion;
    private float _time;

    private static readonly Vector2[] _emberColors =
    [
        new(240, 176, 80),   // amber
        new(240, 133, 64),   // orange
        new(208, 74, 40),    // red
        new(245, 208, 64),   // gold
    ];

    public UltraTokenEffect()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    /// <summary>True while the model is actively generating — increases particle activity.</summary>
    public bool IsGenerating
    {
        get => _isGenerating;
        set
        {
            _isGenerating = value;
            _targetIntensity = value ? 1.0f : 0.3f;
        }
    }

    /// <summary>Disables animation, showing only a static warm glow.</summary>
    public bool ReducedMotion
    {
        get => _reducedMotion;
        set => _reducedMotion = value;
    }

    private void OnCreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
    {
        // Resources are device-dependent; CanvasAnimatedControl handles recreation on device loss.
    }

    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var size = sender.Size;
        _time += (float)args.Timing.ElapsedTime.TotalSeconds;

        // Smooth intensity toward target
        _intensity += (_targetIntensity - _intensity) * 0.05f;

        if (_reducedMotion)
        {
            // Static warm glow fallback
            using var glow = new CanvasRadialGradientBrush(ds, 
                Microsoft.Graphics.Canvas.Brushes.CanvasEdgeBehavior.Clamp,
                Microsoft.Graphics.Canvas.Brushes.CanvasAlphaMode.Premultiplied)
            {
                Center = new Vector2((float)(size.Width / 2), (float)(size.Height / 2)),
                RadiusX = (float)(size.Width / 2),
                RadiusY = (float)(size.Height / 2),
            };
            glow.Stops = new[]
            {
                new CanvasGradientStop { Position = 0f, Color = Microsoft.UI.ColorHelper.FromArgb(120, 240, 176, 80) },
                new CanvasGradientStop { Position = 1f, Color = Microsoft.UI.ColorHelper.FromArgb(0, 240, 176, 80) },
            };
            ds.FillEllipse(new Vector2((float)(size.Width / 2), (float)(size.Height / 2)),
                (float)(size.Width / 2), (float)(size.Height / 2), glow);
            return;
        }

        // Particle-like primitives: drifting glowing dots with fire colors
        var cx = (float)(size.Width / 2);
        var cy = (float)(size.Height / 2);
        var maxR = Math.Min(cx, cy);

        var rng = new Random(42);
        var particleCount = (int)(12 + _intensity * 24);

        for (var i = 0; i < particleCount; i++)
        {
            var seed = rng.NextDouble();
            var phase = _time * (0.5f + (float)seed * 1.5f) + i * 0.7f;
            var angle = (float)(seed * Math.PI * 2 + phase * 0.3);
            var radius = (float)(maxR * (0.2 + 0.6 * Math.Sin(phase) * Math.Sin(phase)));
            var x = cx + MathF.Cos(angle) * radius;
            var y = cy + MathF.Sin(angle) * radius * 0.6f - (float)(phase % 1.0 * maxR * 0.3);
            var sizeP = (float)(2 + _intensity * 4 * rng.NextDouble());
            var alpha = (byte)(100 + _intensity * 120 * (1 - Math.Abs(Math.Sin(phase))));

            var colorIdx = i % _emberColors.Length;
            var c = _emberColors[colorIdx];
            var color = Microsoft.UI.ColorHelper.FromArgb(alpha, (byte)c.X, (byte)c.Y, (byte)c.Z);

            ds.FillCircle(x, y, sizeP, color);
        }

        // Central glow
        using var centerGlow = new CanvasRadialGradientBrush(ds,
            Microsoft.Graphics.Canvas.Brushes.CanvasEdgeBehavior.Clamp,
            Microsoft.Graphics.Canvas.Brushes.CanvasAlphaMode.Premultiplied)
        {
            Center = new Vector2(cx, cy),
            RadiusX = maxR * 0.5f,
            RadiusY = maxR * 0.5f,
        };
        centerGlow.Stops = new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Microsoft.UI.ColorHelper.FromArgb((byte)(60 + _intensity * 80), 240, 176, 80) },
            new CanvasGradientStop { Position = 1f, Color = Microsoft.UI.ColorHelper.FromArgb(0, 240, 176, 80) },
        };
        ds.FillEllipse(new Vector2(cx, cy), maxR * 0.5f, maxR * 0.5f, centerGlow);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Canvas.RemoveFromVisualTree();
    }
}
