using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ZX0ai.Views.Controls;

/// <summary>
/// The opening question, set very large and blurred back into the surface.
/// </summary>
/// <remarks>
/// <para>
/// Drawn rather than laid out because WinUI has no blur for text: a <c>TextBlock</c> can
/// be faded but not softened, and the reference depends on the softening — the question
/// has to sit far enough behind the composer to read as backdrop rather than as content,
/// while still being legible. Opacity alone would leave crisp edges competing with the
/// prompt bar in front of it.
/// </para>
/// <para>
/// <see cref="CanvasControl"/>, not the animated variant: this is a still frame that
/// changes only with size and theme, so it costs nothing between those events.
/// </para>
/// </remarks>
public sealed partial class HeroText : UserControl
{
    /// <summary>
    /// The ink to set the question in.
    /// </summary>
    /// <remarks>
    /// A property rather than a resource lookup. The theme is flipped on the shell root,
    /// not on the application, so <c>Application.Current.Resources</c> would keep serving
    /// whichever theme the app launched in. Bound with <c>{ThemeResource}</c> by the
    /// caller, this arrives through the element's own resource chain and is therefore
    /// always the theme actually on screen.
    /// </remarks>
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(
        nameof(Ink),
        typeof(Brush),
        typeof(HeroText),
        new PropertyMetadata(null, static (d, _) => ((HeroText)d).Refresh()));

    /// <summary>
    /// Vertical space opened after the first line for the composer to sit in.
    /// </summary>
    /// <remarks>
    /// The composer floats over this control, and at display sizes the natural gap
    /// between two lines is a fraction of the composer's height — so centring both would
    /// bury the middle line completely. Reserving the gap here, rather than nudging
    /// either element, is what lets all three lines stay readable with the composer
    /// between the first and the second, as in the reference.
    /// </remarks>
    public static readonly DependencyProperty ComposerGapProperty = DependencyProperty.Register(
        nameof(ComposerGap),
        typeof(double),
        typeof(HeroText),
        new PropertyMetadata(0d, static (d, _) => ((HeroText)d).Refresh()));

    /// <summary>Set as three explicit lines; the break is a composition decision, not a wrap.</summary>
    private static readonly string[] Lines = ["What's on", "Your mind", "today?"];

    public HeroText()
    {
        InitializeComponent();

        Unloaded += (_, _) => Canvas.RemoveFromVisualTree();
    }

    public Brush? Ink
    {
        get => (Brush?)GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    public double ComposerGap
    {
        get => (double)GetValue(ComposerGapProperty);
        set => SetValue(ComposerGapProperty, value);
    }

    /// <summary>Repaints the question. Safe to call at any time from the UI thread.</summary>
    public void Refresh() => Canvas?.Invalidate();

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;
        if (width < 8 || height < 8)
        {
            return;
        }

        // Proportional to the surface, capped so it stops growing on a wide monitor —
        // past this the question reads as a banner rather than as a held thought.
        var fontSize = MathF.Min(width * 0.085f, 84f);
        var lineHeight = fontSize * 1.3f;
        var gap = (float)ComposerGap;

        // The gap is centred on the surface, because that is where the composer is. That
        // puts the block itself half a line lower than centre, which is the offset below.
        var blockHeight = (lineHeight * Lines.Length) + gap;
        var top = ((height - blockHeight) / 2f) + (lineHeight / 2f);

        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Display",
            FontSize = fontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiLight,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Top,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        var ink = Ink is SolidColorBrush brush
            ? brush.Color
            : Color.FromArgb(255, 185, 185, 190);

        // Composed into a command list, then blurred as one image. Blurring each line
        // separately would darken the overlaps where ascenders and descenders meet.
        using var text = new CanvasCommandList(sender);
        using (var session = text.CreateDrawingSession())
        {
            for (var i = 0; i < Lines.Length; i++)
            {
                // Everything after the first line is pushed down past the composer.
                var y = top + (i * lineHeight) + (i > 0 ? gap : 0f);

                session.DrawText(
                    Lines[i],
                    new Windows.Foundation.Rect(0, y, width, lineHeight),
                    ink,
                    format);
            }
        }

        // A command list has unbounded extent, so the blur has nothing to clip against
        // and the glyph edges stay soft in every direction — the one place an unbounded
        // source is exactly what you want.
        using var blur = new GaussianBlurEffect
        {
            Source = text,
            BlurAmount = MathF.Max(2f, fontSize * 0.055f),
            BorderMode = EffectBorderMode.Soft,
        };

        args.DrawingSession.DrawImage(blur);
    }
}
