using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ZX0ai.Views.Controls;

/// <summary>
/// The orb. Two looks over one silhouette: an iridescent pastel body in light, and a
/// magenta particle shell in dark.
/// </summary>
/// <remarks>
/// <para>
/// Both modes are the same object seen two ways, which is why they share the harmonic
/// deformation. Light fills the silhouette with drifting pastel lobes and blurs it;
/// dark scatters points over a sphere, deforms it with the same harmonics and lets the
/// silhouette emerge from where the projection crowds points at the rim.
/// </para>
/// <para>
/// <b>Why <see cref="CanvasControl"/> and a manual clock</b> rather than
/// <c>CanvasAnimatedControl</c>. The animated control renders through a swap chain,
/// which composites as an opaque rectangle over the page — the orb sat in a white box.
/// <see cref="CanvasControl"/> renders into an image source that carries alpha, so the
/// halo fades into whatever is behind it. The cost is the frame clock, which the
/// animated control would have provided: <see cref="CompositionTarget.Rendering"/> and a
/// stopwatch replace it, and drawing moves to the UI thread — which in turn removes the
/// snapshot plumbing the game-loop thread would have required.
/// </para>
/// <para>
/// <b>Bounded source.</b> The body is composed into a <see cref="CanvasRenderTarget"/>
/// rather than a command list. Blur over a command list's unbounded extent samples
/// outside the drawn geometry and hollows the body out.
/// </para>
/// </remarks>
public sealed partial class OrbControl : UserControl
{
    /// <summary>Points in the dark-mode shell. Enough to read as a surface, few enough to stay smooth.</summary>
    private const int ParticleCount = 1500;

    /// <summary>Samples around the light-mode silhouette. Past this the outline is already smooth.</summary>
    private const int OutlineSamples = 180;

    private static readonly float GoldenAngle = MathF.PI * (3f - MathF.Sqrt(5f));

    /// <summary>Pastel lobes of the light body, with the speed and phase each one drifts at.</summary>
    private static readonly (Color Color, float Speed, float Phase, float Orbit, float Spread)[] Lobes =
    [
        (Color.FromArgb(255, 167, 199, 255), 0.23f, 0.0f, 0.34f, 0.62f),
        (Color.FromArgb(255, 255, 186, 219), 0.19f, 2.1f, 0.30f, 0.58f),
        (Color.FromArgb(255, 205, 180, 245), 0.27f, 4.0f, 0.38f, 0.66f),
        (Color.FromArgb(255, 176, 240, 233), 0.21f, 5.4f, 0.28f, 0.54f),
    ];

    private readonly Vector3[] _sphere = new Vector3[ParticleCount];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private CanvasRenderTarget? _offscreen;
    private CanvasRadialGradientBrush?[] _lobeBrushes = new CanvasRadialGradientBrush?[Lobes.Length];
    private bool _running;

    public OrbControl()
    {
        InitializeComponent();

        BuildSphere();

        Loaded += (_, _) => Start();
        Unloaded += (_, _) =>
        {
            Stop();
            Canvas.RemoveFromVisualTree();
            _offscreen?.Dispose();
            _offscreen = null;
        };

        // A collapsed orb still costs a frame's work every frame unless the clock stops.
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible)
            {
                Start();
            }
            else
            {
                Stop();
            }
        });
    }

    private void Start()
    {
        if (_running || Visibility != Visibility.Visible)
        {
            return;
        }

        _running = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        _ = sender;
        _ = e;
        Canvas.Invalidate();
    }

    /// <summary>Fibonacci sphere: even coverage with no clustering at the poles.</summary>
    private void BuildSphere()
    {
        for (var i = 0; i < ParticleCount; i++)
        {
            var z = 1f - (2f * (i + 0.5f) / ParticleCount);
            var ring = MathF.Sqrt(MathF.Max(0f, 1f - (z * z)));
            var theta = GoldenAngle * i;

            _sphere[i] = new Vector3(ring * MathF.Cos(theta), ring * MathF.Sin(theta), z);
        }
    }

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _ = args;

        // Device-dependent resources die with the device and are rebuilt here.
        _offscreen?.Dispose();
        _offscreen = null;

        _lobeBrushes = new CanvasRadialGradientBrush?[Lobes.Length];
        for (var i = 0; i < Lobes.Length; i++)
        {
            _lobeBrushes[i] = new CanvasRadialGradientBrush(sender, Lobes[i].Color, Colors.Transparent);
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;
        if (width < 4 || height < 4)
        {
            return;
        }

        var time = (float)_clock.Elapsed.TotalSeconds;
        var centre = new Vector2(width / 2f, height / 2f);

        // Leave room for the halo, which reaches well past the body.
        var radius = MathF.Min(width, height) * 0.32f;

        // Drawing on the UI thread, so the element's own theme is legal to read here.
        var isDark = ActualTheme == ElementTheme.Dark;

        var offscreen = EnsureOffscreen(sender, width, height);

        using (var session = offscreen.CreateDrawingSession())
        {
            session.Clear(Colors.Transparent);

            if (isDark)
            {
                DrawParticleShell(session, centre, radius, time);
            }
            else
            {
                DrawPastelBody(sender, session, centre, radius, time);
            }
        }

        // Halo first, body over it. Both are the same source at two blur radii, so the
        // glow always agrees with the silhouette that casts it.
        using var halo = new GaussianBlurEffect
        {
            Source = offscreen,
            BlurAmount = radius * (isDark ? 0.30f : 0.34f),
            BorderMode = EffectBorderMode.Soft,
        };

        using var faded = new OpacityEffect
        {
            Source = halo,
            Opacity = isDark ? 0.85f : 0.55f,
        };

        args.DrawingSession.DrawImage(faded);

        using var body = new GaussianBlurEffect
        {
            Source = offscreen,
            BlurAmount = radius * (isDark ? 0.012f : 0.045f),
            BorderMode = EffectBorderMode.Soft,
        };

        args.DrawingSession.DrawImage(body);
    }

    private CanvasRenderTarget EnsureOffscreen(CanvasControl sender, float width, float height)
    {
        if (_offscreen is { } existing &&
            MathF.Abs((float)existing.Size.Width - width) < 0.5f &&
            MathF.Abs((float)existing.Size.Height - height) < 0.5f)
        {
            return existing;
        }

        _offscreen?.Dispose();
        _offscreen = new CanvasRenderTarget(sender, width, height, sender.Dpi);
        return _offscreen;
    }

    /// <summary>
    /// Radius of the silhouette at an angle, as a fraction of the nominal radius.
    /// </summary>
    /// <remarks>
    /// Three harmonics at incommensurable speeds. Their sum never repeats over any
    /// period a viewer will sit through, so the body reads as alive rather than looped.
    /// Amplitudes stay under a fifth of the radius in total: past that the shape stops
    /// being a body with a surface and turns into an amoeba.
    /// </remarks>
    private static float Silhouette(float angle, float time) =>
        1f
        + (0.085f * MathF.Sin((3f * angle) + (time * 0.62f)))
        + (0.055f * MathF.Sin((5f * angle) - (time * 0.44f) + 1.3f))
        + (0.038f * MathF.Sin((7f * angle) + (time * 0.83f) + 2.7f));

    // ============================== Light ==============================

    private void DrawPastelBody(
        ICanvasResourceCreator creator,
        CanvasDrawingSession session,
        Vector2 centre,
        float radius,
        float time)
    {
        using var silhouette = BuildSilhouette(creator, centre, radius, time);

        // Everything is painted inside the silhouette, so the pastels can be drawn as
        // plain overlapping circles and still produce a clean edge.
        using (session.CreateLayer(1f, silhouette))
        {
            session.FillGeometry(silhouette, Color.FromArgb(255, 240, 244, 255));

            for (var i = 0; i < Lobes.Length; i++)
            {
                if (_lobeBrushes[i] is not { } brush)
                {
                    continue;
                }

                var (_, speed, phase, orbit, spread) = Lobes[i];
                var angle = (time * speed) + phase;

                var position = centre + new Vector2(
                    MathF.Cos(angle) * radius * orbit,
                    MathF.Sin(angle * 1.3f) * radius * orbit);

                brush.Center = position;
                brush.RadiusX = radius * spread;
                brush.RadiusY = radius * spread;

                session.FillCircle(position, radius * spread, brush);
            }

            // A specular lift where the light would fall, so the body reads as glass
            // rather than as a flat wash.
            var highlight = centre - new Vector2(radius * 0.30f, radius * 0.38f);
            using var gloss = new CanvasRadialGradientBrush(
                creator,
                Color.FromArgb(200, 255, 255, 255),
                Colors.Transparent)
            {
                Center = highlight,
                RadiusX = radius * 0.52f,
                RadiusY = radius * 0.42f,
            };
            session.FillCircle(highlight, radius * 0.52f, gloss);
        }

        // A pale rim, drawn on top: the edge of a soap film catching light.
        session.DrawGeometry(silhouette, Color.FromArgb(130, 255, 255, 255), 1.6f);
    }

    private static CanvasGeometry BuildSilhouette(
        ICanvasResourceCreator creator,
        Vector2 centre,
        float radius,
        float time)
    {
        using var path = new CanvasPathBuilder(creator);

        for (var i = 0; i < OutlineSamples; i++)
        {
            var angle = MathF.Tau * i / OutlineSamples;
            var r = radius * Silhouette(angle, time);
            var point = centre + new Vector2(MathF.Cos(angle) * r, MathF.Sin(angle) * r);

            if (i == 0)
            {
                path.BeginFigure(point);
            }
            else
            {
                path.AddLine(point);
            }
        }

        path.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(path);
    }

    // =============================== Dark ==============================

    /// <summary>
    /// Points scattered over a deformed sphere, rotated and projected flat.
    /// </summary>
    /// <remarks>
    /// No silhouette is drawn. An orthographic projection crowds points where the
    /// surface turns away from the viewer, so the rim draws itself — which is what makes
    /// the shape read as a volume rather than as a disc of dots.
    /// </remarks>
    private void DrawParticleShell(
        CanvasDrawingSession session,
        Vector2 centre,
        float radius,
        float time)
    {
        var yaw = time * 0.28f;
        var sinYaw = MathF.Sin(yaw);
        var cosYaw = MathF.Cos(yaw);

        // A fixed tilt, so the rotation axis is not the screen vertical and the motion
        // reads as a body turning rather than a texture scrolling.
        const float Tilt = 0.42f;
        var sinTilt = MathF.Sin(Tilt);
        var cosTilt = MathF.Cos(Tilt);

        for (var i = 0; i < ParticleCount; i++)
        {
            var p = _sphere[i];

            // The same harmonics as the light silhouette, evaluated in three dimensions
            // so the deformation is a property of the body and not of the projection.
            var scale = 1f
                + (0.10f * MathF.Sin((3f * p.X) + (time * 0.62f)))
                + (0.07f * MathF.Sin((4f * p.Y) - (time * 0.44f) + 1.3f))
                + (0.05f * MathF.Sin((5f * p.Z) + (time * 0.83f) + 2.7f));

            var x = p.X * scale;
            var y = p.Y * scale;
            var z = p.Z * scale;

            var rx = (x * cosYaw) + (z * sinYaw);
            var rz = (z * cosYaw) - (x * sinYaw);
            var ry = (y * cosTilt) - (rz * sinTilt);
            var depth = (rz * cosTilt) + (y * sinTilt);

            var position = centre + new Vector2(rx * radius, ry * radius);

            // Front points are larger, brighter and whiter; back points fall away into
            // the magenta. That gradient is the only depth cue in an orthographic view.
            var front = (depth + 1f) * 0.5f;
            var dotRadius = 0.55f + (1.15f * front * front);
            var alpha = (byte)(30 + (215 * front * front));

            var colour = Color.FromArgb(
                alpha,
                255,
                (byte)(40 + (150 * front)),
                (byte)(170 + (70 * front)));

            session.FillCircle(position, dotRadius, colour);
        }
    }
}
