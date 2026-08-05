using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Numerics;

namespace ZX0ai.Views.Controls;

/// <summary>
/// Restrained animated energy border for the composer when Ultra Mode is active.
/// Effect follows the border only — no fire inside the text area.
/// Slow continuous movement while idle; stronger while typing; brief pulse on send.
/// Degrades to a static warm glow when reduced motion is requested.
/// </summary>
public sealed partial class UltraComposerEffect : UserControl
{
    private float _intensity = 0.4f;
    private float _targetIntensity = 0.4f;
    private bool _isTyping;
    private bool _isSending;
    private bool _reducedMotion;
    private float _time;
    private float _sendPulse;

    private static readonly Vector2[] _emberColors =
    [
        new(240, 176, 80),
        new(240, 133, 64),
        new(208, 74, 40),
        new(245, 208, 64),
    ];

    public UltraComposerEffect()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public bool IsTyping
    {
        get => _isTyping;
        set
        {
            _isTyping = value;
            _targetIntensity = value ? 0.7f : 0.4f;
        }
    }

    public bool IsSending
    {
        get => _isSending;
        set
        {
            _isSending = value;
            if (value) _sendPulse = 1.0f;
        }
    }

    public bool ReducedMotion
    {
        get => _reducedMotion;
        set => _reducedMotion = value;
    }

    private void OnCreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
    {
    }

    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var size = sender.Size;
        _time += (float)args.Timing.ElapsedTime.TotalSeconds;

        _intensity += (_targetIntensity - _intensity) * 0.04f;
        _sendPulse *= 0.92f;
        var totalIntensity = _intensity + _sendPulse * 0.5f;

        var w = (float)size.Width;
        var h = (float)size.Height;
        var thickness = 2.0f + totalIntensity * 1.5f;

        if (_reducedMotion)
        {
            // Static warm border glow
            var color = Microsoft.UI.ColorHelper.FromArgb(80, 240, 176, 80);
            ds.DrawRectangle(thickness / 2, thickness / 2, w - thickness, h - thickness,
                color, thickness);
            return;
        }

        // Animated energy border: drifting glowing segments along the rectangle perimeter
        var perimeter = 2 * (w + h);
        var segmentCount = 24;
        var rng = new Random(7);

        for (var i = 0; i < segmentCount; i++)
        {
            var seed = rng.NextDouble();
            var phase = _time * (0.3f + (float)seed * 0.8f) + i;
            var pos = (float)((phase % perimeter) / perimeter) * perimeter;

            // Map perimeter position to (x, y) on the rectangle border
            var (x, y) = PerimeterToPoint(pos, w, h);

            var colorIdx = i % _emberColors.Length;
            var c = _emberColors[colorIdx];
            var alpha = (byte)(60 + totalIntensity * 120 * (float)rng.NextDouble());
            var color = Microsoft.UI.ColorHelper.FromArgb(alpha, (byte)c.X, (byte)c.Y, (byte)c.Z);
            var dotSize = 1.5f + totalIntensity * 2.5f * (float)rng.NextDouble();

            ds.FillCircle(x, y, dotSize, color);
        }

        // Base border line
        var baseColor = Microsoft.UI.ColorHelper.FromArgb(
            (byte)(40 + totalIntensity * 50), 240, 176, 80);
        ds.DrawRectangle(thickness / 2, thickness / 2, w - thickness, h - thickness,
            baseColor, thickness);
    }

    private static (float x, float y) PerimeterToPoint(float pos, float w, float h)
    {
        if (pos < w) return (pos, 0);
        pos -= w;
        if (pos < h) return (w, pos);
        pos -= h;
        if (pos < w) return (w - pos, h);
        pos -= w;
        return (0, h - pos);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Canvas.RemoveFromVisualTree();
    }
}
