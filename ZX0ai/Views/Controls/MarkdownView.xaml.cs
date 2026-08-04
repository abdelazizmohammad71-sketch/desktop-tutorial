using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ZX0ai.Views.Controls;

public sealed partial class MarkdownView : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarkdownView),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public MarkdownView()
    {
        InitializeComponent();
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((MarkdownView)sender).RenderMarkdown((string?)args.NewValue);
    }

    private void RenderMarkdown(string? markdown)
    {
        Root.Children.Clear();
        MarkdownRenderer.RenderInto(Root, markdown);
    }
}
