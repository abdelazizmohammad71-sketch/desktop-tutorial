using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using ZX0ai.Core.Models;
using ZX0ai.Core.Services;
using ZX0ai.Services;
using ZX0ai.ViewModels;
using ZX0ai.Views.Controls;

namespace ZX0ai.Views;

/// <summary>
/// The shell: history on the left, the conversation in the middle, the run on the right.
/// </summary>
/// <remarks>
/// <para>
/// The surface has two states. <b>Empty</b> is the question, the orb and a centred
/// composer. <b>Answering</b> is the transcript with the composer pinned to the foot.
/// Everything else is identical across both, so starting a conversation moves one thing
/// rather than replacing the screen.
/// </para>
/// <para>
/// The transcript is built in code rather than bound. <c>ChatMessage</c> is a Core record
/// with no change notification — by design, since Core carries no UI framework — so a
/// bound list would show the first token of a reply and then never update. Building the
/// bubbles here lets the streaming one be mutated in place, which is also far cheaper
/// than re-templating the whole transcript on every token.
/// </para>
/// </remarks>
public sealed partial class ShellPage : Page
{
    /// <summary>Below this the run panel is the first thing to go; the rail follows.</summary>
    private const double RunPanelBreakpoint = 1180;
    private const double RailBreakpoint = 900;

    /// <summary>180-220ms native easing, per the design system's own panel-motion range.</summary>
    private const double PanelAnimationMs = 200;

    private static double RailOpenWidth => (double)Application.Current.Resources["RailWidth"];

    private static double ToolPanelOpenWidth => (double)Application.Current.Resources["ToolPanelWidth"];

    private readonly ConversationViewModel _vm;

    private bool _isDark;
    private bool _railOpen = true;
    private bool _runPanelOpen = true;

    /// <summary>Set when the user closed a panel, so a resize does not reopen it.</summary>
    private bool _railUserClosed;
    private bool _runPanelUserClosed;

    // The view binds directly to the conversation collection and updates only the
    // message that is streaming, so the transcript no longer rebuilds on every delta.

    /// <summary>Which dock view is showing: run, terminal or files.</summary>
    private string _dockTab = "changes";

    /// <summary>Effort steps, with the one caveat worth stating on the menu.</summary>
    private static readonly (string Value, string Label, string Note)[] EffortLevels =
    [
        ("low", "Low", ""),
        ("medium", "Medium", ""),
        ("high", "High", ""),
        ("ultra", "Ultra", "Uses more of your limit"),
    ];

    private static readonly (string Value, string Note)[] SpeedLevels =
    [
        ("Standard", "Default speed"),
        ("Fast", "Faster, more usage"),
    ];

    private static readonly (AccessMode Mode, string Label, string Note)[] AccessModes =
    [
        (AccessMode.AskForApproval, "Ask for approval", "Always ask before writing or running"),
        (AccessMode.ApproveForMe, "Approve for me", "Only ask for anything risky"),
        (AccessMode.FullAccess, "Full access", "Run and write freely inside the folder"),
    ];

    public ShellPage()
    {
        InitializeComponent();

        _vm = App.GetService<ConversationViewModel>();
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.TranscriptChanged += OnTranscriptChanged;

        HistoryList.ItemsSource = _vm.History;
        _vm.History.CollectionChanged += (_, _) => ApplyHistoryState();

        ThreadListView.ItemsSource = _vm.Messages;

        // Tool execution list removed — operations now appear inline in assistant responses.
        _vm.ToolRuns.CollectionChanged += (_, _) => ApplyRunPanel();

        ApplyTheme();
        ApplyHistoryState();
        ApplyModelChip();
        ApplyAccessChip();
        ApplyWorkspace();

        SelectDockTab("changes");
        ApplyRunPanel();
        ApplySurfaceState();

        // Opening a project is why someone would click it in the first place; showing
        // what just got bound is more useful than leaving the dock on Run.
        RailProjects.ProjectOpened += (_, _) =>
        {
            SelectDockTab("files");
        };

        // Credential notice removed — no provider config UI remains.

        Loaded += (_, _) => Prompt.Focus(FocusState.Programmatic);

        RegisterShortcuts();
    }

    /// <summary>
    /// Panel shortcuts that work regardless of where focus currently is.
    /// </summary>
    /// <remarks>
    /// Attached to the page itself rather than to the toggle buttons: a
    /// <see cref="KeyboardAccelerator"/> on a button only fires while that button (or
    /// something inside it) has focus, and the entire point of a panel shortcut is that
    /// it works while the user is typing in the composer, which is where focus actually
    /// sits most of the time.
    /// </remarks>
    private void RegisterShortcuts()
    {
        var toggleRail = new KeyboardAccelerator
        {
            Key = VirtualKey.B,
            Modifiers = VirtualKeyModifiers.Control,
        };
        toggleRail.Invoked += (_, args) =>
        {
            args.Handled = true;
            OnToggleRailClick(this, new RoutedEventArgs());
        };
        KeyboardAccelerators.Add(toggleRail);

        var toggleDock = new KeyboardAccelerator
        {
            Key = VirtualKey.R,
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
        };
        toggleDock.Invoked += (_, args) =>
        {
            args.Handled = true;
            OnToggleRunPanelClick(this, new RoutedEventArgs());
        };
        KeyboardAccelerators.Add(toggleDock);
    }

    /// <summary>Only the inert centre of the title bar is draggable.</summary>
    public UIElement DragRegion => TitleDragRegion;

    /// <summary>True once the shell has flipped itself to the dark treatment.</summary>
    public bool IsDark => _isDark;

    /// <summary>Raised after a theme flip so the host can repaint the caption buttons.</summary>
    public event EventHandler<bool>? ThemeChanged;

    /// <summary>Keeps the header clear of the system caption buttons.</summary>
    public void SetCaptionInsets(double left, double right) =>
        TopBar.Padding = new Thickness(14 + left, 0, 14 + right, 0);

    // ============================== Theme ==============================

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        _isDark = !_isDark;
        ApplyTheme();
        ThemeChanged?.Invoke(this, _isDark);
    }

    /// <summary>
    /// Flips the whole product between the two treatments.
    /// </summary>
    /// <remarks>
    /// Set on the shell root, not on the application. An element theme cascades to every
    /// descendant and re-evaluates every <c>{ThemeResource}</c> beneath it, which is why
    /// no colour in this product is resolved from C#.
    /// </remarks>
    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _isDark ? ElementTheme.Dark : ElementTheme.Light;

        // Sun on the dark treatment, moon on the light one: the icon names what the
        // button will do, not what is currently on screen.
        ThemeIcon.Data = Geometry(_isDark
            ? "M12 7.5 A4.5 4.5 0 1 0 12 16.5 A4.5 4.5 0 1 0 12 7.5 Z M12 2 V4 M12 20 V22 M2 12 H4 M20 12 H22 M5 5 L6.5 6.5 M17.5 17.5 L19 19 M19 5 L17.5 6.5 M6.5 17.5 L5 19"
            : "M20 14.5 A8.5 8.5 0 1 1 9.5 4 A6.8 6.8 0 0 0 20 14.5 Z");

        // The question is drawn, not laid out, so it has to be told to repaint. The
        // transcript does not: its bubbles carry styles, and the references inside a
        // style are re-evaluated against the element on every theme change.
        Hero.Refresh();
    }

    // ========================= Rail and panels =========================

    private void OnToggleRailClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        _railOpen = !_railOpen;
        _railUserClosed = !_railOpen;
        ApplyPanelState(animate: true);
    }

    private void OnCollapseRailClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        _railOpen = false;
        _railUserClosed = true;
        ApplyPanelState(animate: true);
    }

    private void OnToggleRunPanelClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        _runPanelOpen = !_runPanelOpen;
        _runPanelUserClosed = !_runPanelOpen;
        ApplyPanelState(animate: true);
    }

    /// <summary>
    /// Shows or hides the rail and the dock, animated on a deliberate toggle and instant
    /// on a window resize.
    /// </summary>
    /// <remarks>
    /// A resize fires this continuously while the window is being dragged; animating
    /// every one of those ticks would look like the panel is fighting the cursor. A
    /// click is a single, deliberate action, and that is the one worth 200ms of motion.
    /// </remarks>
    private void ApplyPanelState(bool animate)
    {
        AnimatePanel(Rail, RailOpenWidth, _railOpen, animate);
        AnimatePanel(RunPanel, ToolPanelOpenWidth, _runPanelOpen, animate);
    }

    /// <summary>
    /// Slides one side panel open or closed.
    /// </summary>
    /// <remarks>
    /// Animates the <em>Border's own</em> <c>Width</c>, not the Grid column — the column
    /// stays <c>Auto</c> throughout and simply tracks whatever the border currently
    /// measures, including mid-animation, and a collapsed child contributes nothing to
    /// an Auto column's size, which is what lets the centre pane reclaim the space
    /// without any separate column-width bookkeeping.
    /// </remarks>
    private static void AnimatePanel(FrameworkElement panel, double openWidth, bool open, bool animate)
    {
        if (!animate)
        {
            panel.Width = open ? openWidth : 0;
            panel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (open)
        {
            panel.Visibility = Visibility.Visible;
        }

        var animation = new DoubleAnimation
        {
            To = open ? openWidth : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(PanelAnimationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, panel);
        Storyboard.SetTargetProperty(animation, "Width");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (!open)
            {
                panel.Visibility = Visibility.Collapsed;
            }
        };
        storyboard.Begin();
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _ = sender;

        var width = e.NewSize.Width;

        // The conversation is the only region that is never optional, so the panels give
        // way to it in order. A panel the user closed by hand stays closed when the
        // window grows back — reopening it would undo a decision they made.
        _runPanelOpen = width >= RunPanelBreakpoint && !_runPanelUserClosed;
        _railOpen = width >= RailBreakpoint && !_railUserClosed;

        ApplyPanelState(animate: false);
    }

    // ============================= History =============================

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;

        // One search box for both lists rather than a second field a few pixels below
        // the first, asking the same question twice.
        _vm.SearchText = SearchBox.Text;
        RailProjects.ApplyFilter(SearchBox.Text);
    }

    private void OnHistoryRowClick(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Button { Tag: string id })
        {
            _vm.Open(id);
        }
    }

    private void OnNewChatClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        _vm.StartNew();
        Prompt.Focus(FocusState.Programmatic);
    }

    private void OnDeleteChatClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _vm.DeleteCurrent();
    }

    private void ApplyHistoryState()
    {
        HistoryEmpty.Visibility = _vm.HasHistory ? Visibility.Collapsed : Visibility.Visible;
        DeleteChatButton.Visibility = _vm.HasMessages ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============================= Menus ===============================

    /// <summary>
    /// Capability, effort and speed.
    /// </summary>
    /// <remarks>
    /// The menu names capabilities — <c>zax-pro</c>, <c>zax-ultra-full-max</c> — and
    /// never the models behind them. Which providers answer a turn, how many of them
    /// there are and what each one is for is routing: it changes as models are
    /// deprecated and fallbacks engage, and surfacing it would make the product look
    /// like it changed when only the routing did.
    /// <para>
    /// The current value sits right-aligned on each row via
    /// <c>KeyboardAcceleratorTextOverride</c>, which is the only way to get a second
    /// column in a <c>MenuFlyoutSubItem</c> without hand-templating the whole menu.
    /// </para>
    /// </remarks>
    private void OnModelClick(object sender, RoutedEventArgs e)
    {
        _ = e;

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Top };

        var model = new MenuFlyoutSubItem { Text = $"Model  ·  {_vm.ModelName}" };
        foreach (var tier in _vm.Tiers)
        {
            var key = tier;
            model.Items.Add(new ToggleMenuFlyoutItem
            {
                Text = key,
                IsChecked = key == _vm.ModelName,
                Command = new RelayCommand(() =>
                {
                    _vm.SelectTier(key);
                    ApplyModelChip();
                }),
            });
        }

        var effort = new MenuFlyoutSubItem { Text = $"Effort  ·  {_vm.ModelBadge}" };
        foreach (var (value, label, note) in EffortLevels)
        {
            var chosen = value;
            effort.Items.Add(new ToggleMenuFlyoutItem
            {
                Text = label,
                KeyboardAcceleratorTextOverride = note,
                IsChecked = chosen == _vm.Effort,
                Command = new RelayCommand(() =>
                {
                    _vm.Effort = chosen;
                    ApplyModelChip();
                }),
            });
        }

        var speed = new MenuFlyoutSubItem { Text = $"Speed  ·  {_vm.Speed}" };
        foreach (var (value, note) in SpeedLevels)
        {
            var chosen = value;
            speed.Items.Add(new ToggleMenuFlyoutItem
            {
                Text = chosen,
                KeyboardAcceleratorTextOverride = note,
                IsChecked = chosen == _vm.Speed,
                Command = new RelayCommand(() =>
                {
                    _vm.Speed = chosen;
                    ApplyModelChip();
                }),
            });
        }

        flyout.Items.Add(model);
        flyout.Items.Add(effort);
        flyout.Items.Add(speed);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Reset to default",
            Command = new RelayCommand(() =>
            {
                _vm.SelectTier(_vm.Tiers.Count > 1 ? _vm.Tiers[1] : _vm.Tiers[0]);
                _vm.Effort = "high";
                _vm.Speed = "Standard";
                ApplyModelChip();
            }),
        });

        flyout.ShowAt((FrameworkElement)sender);
    }

    /// <summary>
    /// What the agent may do without stopping to ask.
    /// </summary>
    /// <remarks>
    /// Three steps rather than a switch, because the middle one is the useful default
    /// and a two-state control would force a choice between "interrupts constantly" and
    /// "never asks". Each row states its consequence: this is the setting that decides
    /// whether an autonomous model can change a disk, and it should not have to be
    /// inferred from a label.
    /// </remarks>
    private void OnAccessClick(object sender, RoutedEventArgs e)
    {
        _ = e;

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Top };

        foreach (var (mode, label, note) in AccessModes)
        {
            var chosen = mode;
            flyout.Items.Add(new ToggleMenuFlyoutItem
            {
                Text = label,
                KeyboardAcceleratorTextOverride = note,
                IsChecked = chosen == _vm.AccessMode,
                Command = new RelayCommand(() => _ = ApplyAccessSelectionAsync(chosen)),
            });
        }

        flyout.ShowAt((FrameworkElement)sender);
    }

    /// <summary>
    /// Applies a chosen access mode, confirming first when it newly grants full access.
    /// </summary>
    /// <remarks>
    /// Only guards the transition, not the state: re-opening the menu while full access
    /// is already on and re-selecting it does not prompt again. Asking every single time
    /// is exactly the repeated-nag pattern that trains someone to click through a warning
    /// without reading it — the moment that matters is the one where the grant changes.
    /// </remarks>
    private async Task ApplyAccessSelectionAsync(AccessMode chosen)
    {
        if (chosen == AccessMode.FullAccess &&
            _vm.AccessMode != AccessMode.FullAccess &&
            !await ConfirmFullAccessAsync())
        {
            return;
        }

        _vm.AccessMode = chosen;
        ApplyAccessChip();
        ApplyWorkspace();
    }

    /// <summary>Names exactly what full access grants, before it is granted.</summary>
    private async Task<bool> ConfirmFullAccessAsync()
    {
        var body = new StackPanel { Spacing = 14 };

        body.Children.Add(new TextBlock
        {
            Style = SharedStyle("SectionCaptionTextStyle"),
            Text = "The agent will be able to run commands, use the internet, and create or " +
                   "edit files anywhere on this computer without asking first. This includes " +
                   "but is not limited to:",
            TextWrapping = TextWrapping.Wrap,
        });

        var capabilities = new StackPanel { Spacing = 10 };
        capabilities.Children.Add(BuildCapabilityRow(
            "Files and folders",
            "Read, create, modify or delete files anywhere on this computer."));
        capabilities.Children.Add(BuildCapabilityRow(
            "Terminal commands",
            "Run commands, install software, and change system settings."));
        capabilities.Children.Add(BuildCapabilityRow(
            "Internet and connected apps",
            "Access websites, send data, and use enabled tools."));
        body.Children.Add(new Border
        {
            Style = SharedStyle("MarkdownCodeFrameStyle"),
            Padding = new Thickness(12),
            Child = capabilities,
        });

        body.Children.Add(new TextBlock
        {
            Style = SharedStyle("SectionCaptionTextStyle"),
            Text = "This carries real risk, including loss or exposure of sensitive data. " +
                   "You can turn it off again at any time.",
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Turn on full access?",
            Content = body,
            PrimaryButtonText = "Turn on",
            CloseButtonText = "Cancel",

            // The safe choice is the default, so Enter/Escape both decline.
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static StackPanel BuildCapabilityRow(string title, string detail)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Style = SharedStyle("RowTextStyle"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = title,
        });
        stack.Children.Add(new TextBlock
        {
            Style = SharedStyle("SectionCaptionTextStyle"),
            Text = detail,
            TextWrapping = TextWrapping.Wrap,
        });
        return stack;
    }

    /// <summary>Attachments, workspace and plan mode.</summary>
    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        _ = e;

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Top };

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Work in a folder",
            KeyboardAcceleratorTextOverride = _vm.IsWorkspaceBound ? _vm.WorkspaceName : "none",
            Command = new RelayCommand(() => OnWorkspaceClick(this, new RoutedEventArgs())),
        });

        flyout.Items.Add(new ToggleMenuFlyoutItem
        {
            Text = "Plan mode",
            KeyboardAcceleratorTextOverride = "Plan without changing anything",
            IsChecked = _vm.PlanMode,
            Command = new RelayCommand(() => _vm.PlanMode = !_vm.PlanMode),
        });

        flyout.ShowAt((FrameworkElement)sender);
    }

    private void ApplyModelChip()
    {
        ModelChip.Text = _vm.ModelName;
        EffortChip.Text = _vm.ModelBadge;
    }

    /// <summary>Paints the access chip, amber once nothing will stop the agent.</summary>
    private void ApplyAccessChip()
    {
        var full = _vm.AccessMode == AccessMode.FullAccess;

        AccessLabel.Text = _vm.AccessMode switch
        {
            AccessMode.AskForApproval => "Ask for approval",
            AccessMode.ApproveForMe => "Approve for me",
            _ => "Full access",
        };

        AccessIcon.Data = Geometry(_vm.AccessMode switch
        {
            AccessMode.AskForApproval => "M12 3 L21 19 H3 Z M12 10 V14 M12 16.5 V16.6",
            AccessMode.ApproveForMe => "M5 12.5 L10 17.5 L19 7",
            _ => "M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 Z M12 7 V13 M12 16 V16.1",
        });

        AccessLabel.Style = SharedStyle(full ? "WarnTextStyle" : "ChipTextStyle");
        AccessIcon.Style = SharedStyle(full ? "IconWarnStyle" : "IconSmallStyle");
    }

    // ============================== Dock ===============================

    private void OnDockTabClick(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Button { Tag: string tab })
        {
            SelectDockTab(tab);
        }
    }

    /// <summary>
    /// Shows one dock view.
    /// </summary>
    /// <remarks>
    /// The views are siblings whose visibility is toggled rather than content that is
    /// swapped, so the terminal keeps its scrollback and the file tree keeps its
    /// expansion state when the user looks at something else and comes back.
    /// </remarks>
    private void SelectDockTab(string tab)
    {
        _dockTab = tab;

        ChangesView.Visibility = tab == "changes" ? Visibility.Visible : Visibility.Collapsed;
        FilesView.Visibility = tab == "files" ? Visibility.Visible : Visibility.Collapsed;

        ApplyTabState(ChangesTabButton, ChangesTabIcon, tab == "changes");
        ApplyTabState(FilesTabButton, FilesTabIcon, tab == "files");

        DockTitle.Text = tab switch
        {
            "files" => "Files",
            _ => "Changes",
        };
    }

    /// <summary>Paints one icon-only tab of a segmented control.</summary>
    private static void ApplyTabState(Button tab, Microsoft.UI.Xaml.Shapes.Path icon, bool selected)
    {
        tab.Style = SharedStyle(selected ? "TabSelectedStyle" : "TabStyle");
        icon.Style = SharedStyle(selected ? "TabIconSelectedStyle" : "TabIconStyle");
    }

    /// <summary>Paints one tab of a segmented control. See the notes on theme flipping.</summary>
    private static void ApplyTabState(Button tab, TextBlock label, bool selected)
    {
        tab.Style = SharedStyle(selected ? "TabSelectedStyle" : "TabStyle");
        label.Style = SharedStyle(selected ? "TabLabelSelectedStyle" : "TabLabelStyle");
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        // Settings dialog removed per product direction. The about button now
        // cycles the theme — the same action as the title-bar sun/moon button —
        // so the sidebar footer stays useful without exposing provider config.
        OnThemeClick(this, new RoutedEventArgs());
    }

    // =========================== Workspace =============================

    private async void OnWorkspaceClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        try
        {
            if (await App.GetService<FolderPickerService>().PickAsync() is { } path)
            {
                _vm.BindWorkspace(path);
                ApplyWorkspace();
            }
        }
        catch (Exception ex) when (ex is IOException or
                                        UnauthorizedAccessException or
                                        DirectoryNotFoundException or
                                        ArgumentException)
        {
            ErrorText.Text = $"Could not use that folder: {ex.Message}";
            ErrorBanner.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Shows which folder is bound and what the agent may do inside it.
    /// </summary>
    /// <remarks>
    /// Stated in full rather than implied by a switch position. This is the one setting
    /// in the product that decides whether an autonomous model can modify a disk, and
    /// somebody reading the panel should not have to infer its consequences.
    /// </remarks>
    private void ApplyWorkspace()
    {
        WorkspaceLabel.Text = _vm.WorkspaceName;
        ToolTipService.SetToolTip(
            WorkspaceButton,
            _vm.WorkspacePath ?? "Choose a folder the agent may work in");

        Breadcrumb.Visibility = _vm.IsWorkspaceBound ? Visibility.Visible : Visibility.Collapsed;
        BreadcrumbProject.Content = _vm.WorkspaceName;
    }

    /// <summary>
    /// Looks up the git branch for the composer breadcrumb.
    /// </summary>
    /// <remarks>
    /// Read-only and best-effort: no git, no repository, or a folder that has since
    /// vanished all mean the same thing here — the breadcrumb quietly omits the branch
    /// segment rather than showing an error for a purely decorative label.
    /// </remarks>
    // ========================== Turn lifecycle =========================

    private void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _ = sender;

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = SubmitAsync();
        }
    }

    // Lightweight auto-grow: measure the text in a transient TextBlock and expand the TextBox height up to MaxHeight.
    private void OnPromptTextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            if (Prompt is null) return;

            // Use the available width inside the composer: Prompt.ActualWidth minus padding
            var padding = Prompt.Padding.Left + Prompt.Padding.Right + 8; // small safety
            var available = Math.Max(100, Prompt.ActualWidth - padding);

            var tb = new TextBlock
            {
                Text = Prompt.Text + "\n", // ensure last line measured
                TextWrapping = TextWrapping.Wrap,
                FontFamily = Prompt.FontFamily,
                FontSize = Prompt.FontSize,
                FontWeight = Prompt.FontWeight,
                Width = available
            };

            tb.Measure(new Windows.Foundation.Size(available, double.PositiveInfinity));
            var desired = tb.DesiredSize.Height + 12; // small padding

            var target = Math.Min(desired, Prompt.MaxHeight);

            // Only set if changed to avoid layout churn
            if (Math.Abs(Prompt.Height - target) > 1.0)
            {
                Prompt.Height = target;
            }
        }
        catch
        {
            // Swallow any measurement errors — this feature is cosmetic.
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        // The same control stops the turn it started; a separate Stop button would sit
        // disabled for the whole time the user is not streaming, which is most of it.
        if (_vm.IsStreaming)
        {
            _vm.Cancel();
            return;
        }

        _ = SubmitAsync();
    }

    // Keyboard accelerator handler for Send (Ctrl+Enter)
    private void OnSendKeyboardAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _ = sender;
        _ = args;

        // Mirror the button click behaviour: cancel if streaming, otherwise submit.
        if (_vm.IsStreaming)
        {
            _vm.Cancel();
            args.Handled = true;
            return;
        }

        _ = SubmitAsync();
        args.Handled = true;
    }

    /// <summary>
    /// Runs a turn from the composer.
    /// </summary>
    /// <remarks>
    /// The catch is load-bearing rather than defensive. This is started from a click
    /// handler and not awaited, so without it any failure the view model does not model
    /// becomes an unobserved task exception: the composer would clear, nothing would
    /// happen, and there would be no indication anywhere that a turn had been attempted.
    /// </remarks>
    /// <summary>The costly tiers: worth a last look before spending on them.</summary>
    private static readonly HashSet<string> ConfirmBeforeSendTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "zax-2.5 pro", "zax-3.4 ultra",
    };

    private async Task SubmitAsync()
    {
        var prompt = Prompt.Text;
        if (string.IsNullOrWhiteSpace(prompt) || _vm.IsStreaming)
        {
            return;
        }

        if (ConfirmBeforeSendTiers.Contains(_vm.ModelName))
        {
            var confirmed = await ConfirmSendAsync(prompt);
            if (confirmed is null)
            {
                // Cancelled or edited-then-cancelled: the composer keeps whatever text
                // was showing, exactly as if Send had never been pressed.
                return;
            }

            prompt = confirmed;
        }

        Prompt.Text = string.Empty;

        try
        {
            await _vm.SendAsync(prompt).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorBanner.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// A last look before spending on the costly tiers.
    /// </summary>
    /// <remarks>
    /// The message is editable right in the dialog rather than being a plain yes/no —
    /// most of the time a second look is wanted because something about the message
    /// should change, not because the user is unsure whether to send at all. Returns the
    /// (possibly edited) text to send, or null when cancelled.
    /// </remarks>
    private async Task<string?> ConfirmSendAsync(string prompt)
    {
        var box = new TextBox
        {
            Text = prompt,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            MaxHeight = 220,
            Style = SharedStyle("MarkdownCodeTextStyle"),
        };
        box.SelectionStart = box.Text.Length;

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Style = SharedStyle("SectionCaptionTextStyle"),
            Text = $"{_vm.ModelName} is the most capable, most expensive tier. Review or edit " +
                   "the message before it is sent.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new Border
        {
            Style = SharedStyle("MarkdownCodeFrameStyle"),
            Padding = new Thickness(10),
            Child = box,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Send this message?",
            Content = body,
            PrimaryButtonText = "Send",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? box.Text.Trim()
            : null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;

        switch (e.PropertyName)
        {
            case nameof(ConversationViewModel.IsStreaming):
                ApplySendButton();
                ApplyRunPanel();
                break;

            case nameof(ConversationViewModel.RunState):
                ApplyRunPanel();
                FinalizeStreamingRender();
                break;

            case nameof(ConversationViewModel.TokensPerSecond):
            case nameof(ConversationViewModel.TurnTokens):
            case nameof(ConversationViewModel.ElapsedSeconds):
                ApplyRunPanel();
                break;

            case nameof(ConversationViewModel.ErrorMessage):
                ApplyError();
                break;

            case nameof(ConversationViewModel.Session):
            case nameof(ConversationViewModel.HasMessages):
                ApplySurfaceState();
                ApplyHistoryState();
                break;

            // The view model raises these whenever AgentWorkspace changes, which until
            // now only ever happened from the two handlers in this file that already
            // called ApplyWorkspace/ApplyAccessChip directly. The Projects panel binds
            // the workspace independently of either handler, and without these cases
            // the chip and the composer breadcrumb went stale the moment it did — the
            // fix belongs here so every future caller gets it for free, rather than in
            // one more call site required to remember to refresh the chrome by hand.
            case nameof(ConversationViewModel.WorkspaceName):
            case nameof(ConversationViewModel.IsWorkspaceBound):
                ApplyWorkspace();
                break;

            case nameof(ConversationViewModel.AccessMode):
                ApplyAccessChip();
                break;

            default:
                break;
        }
    }

    private ScrollViewer? GetScrollViewer(DependencyObject dep)
    {
        if (dep == null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(dep); i++)
        {
            var child = VisualTreeHelper.GetChild(dep, i);
            if (child is ScrollViewer sv) return sv;
            var result = GetScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }

    private void OnTranscriptChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;

        ApplySurfaceState();

        if (_vm.Messages.Count == 0)
        {
            return;
        }

        // Auto-scroll only when the user is already near the bottom to avoid disrupting
        // the user when they are reading earlier messages.
        var sv = GetScrollViewer(ThreadListView);
        if (sv is null)
        {
            ThreadListView.ScrollIntoView(_vm.Messages[^1]);
            return;
        }

        const double threshold = 48.0; // pixels from bottom considered "at bottom"
        var atBottom = sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - threshold;
        if (atBottom)
        {
            ThreadListView.ScrollIntoView(_vm.Messages[^1]);
        }
    }

    // ============================= Rendering ===========================

    /// <summary>
    /// Renders whatever the streaming message ended on, bypassing the throttle.
    /// </summary>
    /// <remarks>
    /// Called whenever a run leaves an active state. Without it, the last render could
    /// be a throttled one from partway through the final few tokens, and the reply
    /// would sit on screen missing its own closing characters until the user did
    /// something else that happened to trigger a rebuild.
    /// </remarks>
    private void FinalizeStreamingRender()
    {
        // The transcript now updates through the bound list and per-message property change
        // notifications, so there is no separate throttled render to flush here.
    }

    // ============================== State ==============================

    private void ApplySurfaceState()
    {
        var answering = _vm.HasMessages;

        Hero.Visibility = answering ? Visibility.Collapsed : Visibility.Visible;
        ThreadListView.Visibility = answering ? Visibility.Visible : Visibility.Collapsed;

        ConversationTitle.Text = answering ? _vm.Session.Title : "New chat";
        DeleteChatButton.Visibility = answering ? Visibility.Visible : Visibility.Collapsed;

        // Composer positioning: when there are no messages, centre the composer vertically
        // over the hero. When there are messages, pin it to the foot of the transcript.
        if (!answering)
        {
            Grid.SetRow(ComposerHost, 0);
            ComposerHost.VerticalAlignment = VerticalAlignment.Center;
            ComposerHost.Margin = new Thickness(32, 0, 32, 0);
        }
        else
        {
            Grid.SetRow(ComposerHost, 1);
            ComposerHost.VerticalAlignment = VerticalAlignment.Bottom;
            ComposerHost.Margin = new Thickness(32, 0, 32, 22);
        }
    }

    private void ApplySendButton()
    {
        // An arrow to send, a square to stop.
        SendIcon.Data = Geometry(_vm.IsStreaming
            ? "M8 8 H16 V16 H8 Z"
            : "M12 19 V5 M5.5 11.5 L12 5 L18.5 11.5");

        ToolTipService.SetToolTip(SendButton, _vm.IsStreaming ? "Stop" : "Send");
        AutomationProperties.SetName(SendButton, _vm.IsStreaming ? "Stop" : "Send");
    }

    /// <summary>
    /// The one banner above the composer, showing a failure in plain language.
    /// </summary>
    private void ApplyError()
    {
        var message = _vm.ErrorMessage;
        var has = !string.IsNullOrWhiteSpace(message);

        ErrorText.Text = message ?? string.Empty;
        ErrorBanner.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Updates the compact tokens/speed indicators in the right drawer.</summary>
    private void ApplyRunPanel()
    {
        RunTokens.Text = _vm.TurnTokens.ToString("N0");
        RunRate.Text = $"{_vm.TokensPerSecond:F1}/s";
    }

    // ============================= Helpers =============================

    private static Geometry Geometry(string data) =>
        (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data);

    /// <summary>
    /// A shared style by key. Styles are theme-neutral objects — the references inside
    /// them resolve later, against whichever element they are applied to, so an element
    /// built in code still follows the shell's theme flip.
    /// </summary>
    private static Style SharedStyle(string key) => (Style)Application.Current.Resources[key];
}
