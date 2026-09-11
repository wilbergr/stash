using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Stash.Interop;
using Stash.Models;
using Stash.Services;
using Stash.ViewModels;

namespace Stash.Views;

/// <summary>
/// The stash: a panel that slides in from whichever edge it is docked to.
/// </summary>
/// <remarks>
/// <para>
/// The window is intentionally larger than the visible panel. The extra room on
/// the docked side is the distance the panel travels during the slide, and the
/// surplus hangs off the edge of the screen. That lets the slide be a pure GPU
/// transform on the content rather than a per-frame move of the window, which
/// would judder.
/// </para>
/// <para>
/// Placement goes through SetWindowPos in real pixels rather than Window.Left and
/// Window.Top. On a desktop mixing DPI scales there is no single
/// device-independent coordinate space covering every monitor, so setting Left in
/// WPF units puts the window on the wrong display at the wrong size.
/// </para>
/// </remarks>
public partial class StashWindow : Window
{
    private static readonly Duration ShowDuration = new(TimeSpan.FromMilliseconds(210));
    private static readonly Duration HideDuration = new(TimeSpan.FromMilliseconds(140));

    private readonly StashViewModel _vm;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _toastTimer;

    private DockEdge _edge = DockEdge.Bottom;
    private StashLayout _layout;
    private bool _isClosing;
    private bool _isDragging;
    private bool _applyingLayout;

    /// <summary>
    /// Monitor the panel was dropped on, if it has just been dragged. Placement
    /// normally follows the monitor of the window the user came from, which is
    /// right when opening but wrong immediately after a drag: it would re-dock the
    /// panel on the original monitor and yank it away from where it was dropped.
    /// Cleared on the next show.
    /// </summary>
    private MonitorArea? _dropMonitor;

    /// <summary>Raised when the user asks for the settings window.</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised when the user asks for the help window.</summary>
    public event Action? HelpRequested;

    public StashWindow(StashViewModel viewModel, SettingsStore settings)
    {
        _vm = viewModel;
        _settings = settings;

        InitializeComponent();
        DataContext = _vm;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _vm.Toast = null;
        };

        _vm.RequestClose += HidePanel;
        _vm.RequestScrollTo += ScrollTo;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        SettingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        HelpButton.Click += (_, _) => HelpRequested?.Invoke();
        CloseButton.Click += (_, _) => HidePanel();

        HeaderBar.MouseLeftButtonDown += OnHeaderDragStart;
        ResizeGrip.DragDelta += OnResizeDrag;
        ResizeGrip.DragCompleted += (_, _) => _settings.Save();

        PanelRoot.MouseEnter += (_, _) => SetGripVisible(true);
        PanelRoot.MouseLeave += (_, _) => SetGripVisible(false);

        CardList.PreviewMouseWheel += OnCardsMouseWheel;

        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => HidePanel();

        // Moving between monitors of different scale changes every derived size.
        DpiChanged += (_, _) => ApplyLayout(_vm.TargetWindow);

        // Create the HWND up front so SetWindowPos can place the window before it
        // is ever shown, and so the first hotkey press does not pay for it.
        new WindowInteropHelper(this).EnsureHandle();

        _edge = DockEdgeExtensions.Parse(_settings.Current.Edge);
        _vm.Edge = _edge;
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    // ---- Showing and hiding -------------------------------------------------

    /// <summary>
    /// Slides the stash in on the monitor belonging to <paramref name="target"/>,
    /// which is also the window a chosen clip will be pasted into.
    /// </summary>
    public void ShowPanel(IntPtr target)
    {
        if (IsVisible && !_isClosing)
        {
            // Pressing the hotkey again dismisses it, matching Win+V.
            HidePanel();
            return;
        }

        _isClosing = false;
        _vm.TargetWindow = target;

        // Opening follows the app the user is in, so forget where it was last
        // dropped.
        _dropMonitor = null;

        _edge = DockEdgeExtensions.Parse(_settings.Current.Edge);
        _vm.Edge = _edge;

        _vm.ResetForShow();
        _vm.RefreshTimestamps();

        ApplyLayout(target);
        ApplyItemPresentation();

        Show();

        // Show() can reassert the stale Left/Top from WPF's own bookkeeping, so
        // reapply the real placement once the window is up.
        ApplyLayout(target);

        Activate();
        AnimateIn();

        // Focus the search box so the user can type to filter immediately.
        SearchBox.Focus();
        SearchBox.CaretIndex = 0;
    }

    public void HidePanel()
    {
        if (!IsVisible || _isClosing)
        {
            return;
        }

        _isClosing = true;
        AnimateOut();
    }

    private void AnimateIn()
    {
        var from = _edge.SlideFrom(_layout.PanelSize);

        if (!_settings.Current.SlideAnimation)
        {
            SlideTransform.X = 0;
            SlideTransform.Y = 0;
            PanelRoot.Opacity = 1;
            return;
        }

        // Caching the panel to a bitmap makes the slide a single GPU blit,
        // shadow and all, instead of re-rendering text and effects every frame.
        BeginCache();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        SlideTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(from.X, 0, ShowDuration) { EasingFunction = ease });
        SlideTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(from.Y, 0, ShowDuration) { EasingFunction = ease });

        var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(130)));
        fade.Completed += (_, _) => EndCache();
        PanelRoot.BeginAnimation(OpacityProperty, fade);
    }

    private void AnimateOut()
    {
        var to = _edge.SlideFrom(_layout.PanelSize);

        if (!_settings.Current.SlideAnimation)
        {
            FinishHide();
            return;
        }

        BeginCache();

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

        SlideTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(SlideTransform.X, to.X, HideDuration) { EasingFunction = ease });
        SlideTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(SlideTransform.Y, to.Y, HideDuration) { EasingFunction = ease });

        var fade = new DoubleAnimation(1, 0, HideDuration);
        fade.Completed += (_, _) => FinishHide();
        PanelRoot.BeginAnimation(OpacityProperty, fade);
    }

    private void FinishHide()
    {
        EndCache();

        // Clear the animations so the next show starts from a known state.
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        PanelRoot.BeginAnimation(OpacityProperty, null);

        Hide();
        _isClosing = false;
        _vm.Toast = null;
    }

    private void BeginCache()
    {
        PanelRoot.CacheMode = new BitmapCache
        {
            // Render the cache at the monitor's scale, or the slide would look
            // soft on a high-DPI display.
            RenderAtScale = _layout.Scale <= 0 ? 1.0 : _layout.Scale,
            SnapsToDevicePixels = true,
        };
    }

    /// <summary>Drops the bitmap cache so text renders at full sharpness again.</summary>
    private void EndCache() => PanelRoot.CacheMode = null;

    // ---- Layout -------------------------------------------------------------

    /// <summary>
    /// Positions the window in real pixels and lays the panel out inside it.
    /// </summary>
    private void ApplyLayout(IntPtr target)
    {
        if (_applyingLayout)
        {
            // SetWindowPos across a DPI boundary raises DpiChanged, which lands
            // back here; one pass is enough.
            return;
        }

        _applyingLayout = true;

        try
        {
            _layout = _dropMonitor is { } dropped
                ? StashPlacement.Compute(_edge, _settings.Current, dropped)
                : StashPlacement.Compute(_edge, _settings.Current, target);

            PushWindowRect();

            PanelRoot.Margin = _layout.PanelMargin;

            // Confine painting to this monitor so the travel overhang cannot
            // appear on the display next door.
            WindowRoot.Clip = new RectangleGeometry(_layout.MonitorClip);

            ApplyGripPlacement();
        }
        finally
        {
            _applyingLayout = false;
        }

        // WPF keeps its own Left/Top/Width/Height and will happily reassert the
        // stale XAML values after a move that crosses a DPI boundary, leaving the
        // window at 600x300 in the wrong place. Checking the real rect once the
        // dispatcher has drained and correcting it is what makes re-docking
        // reliable; without this, docking across monitors intermittently lands
        // the panel at the wrong size.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, EnsureWindowRect);
    }

    private void PushWindowRect()
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            (int)Math.Round(_layout.WindowPixels.Left),
            (int)Math.Round(_layout.WindowPixels.Top),
            (int)Math.Round(_layout.WindowPixels.Width),
            (int)Math.Round(_layout.WindowPixels.Height),
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    /// <summary>
    /// Re-applies the intended rectangle if something moved the window behind our
    /// back. Deliberately does not reschedule itself: one corrective pass, so a
    /// disagreement with WPF cannot become an endless loop.
    /// </summary>
    private void EnsureWindowRect()
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero || _layout.WindowPixels.IsEmpty)
        {
            return;
        }

        var actual = ScreenGeometry.WindowRect(hwnd);
        if (actual.IsEmpty)
        {
            return;
        }

        const double tolerance = 1.5;
        var matches =
            Math.Abs(actual.Left - _layout.WindowPixels.Left) <= tolerance &&
            Math.Abs(actual.Top - _layout.WindowPixels.Top) <= tolerance &&
            Math.Abs(actual.Width - _layout.WindowPixels.Width) <= tolerance &&
            Math.Abs(actual.Height - _layout.WindowPixels.Height) <= tolerance;

        if (matches)
        {
            return;
        }

        AppPaths.Log(
            $"Placement corrected: window was at {actual}, expected {_layout.WindowPixels}.");

        PushWindowRect();
    }

    /// <summary>Swaps the card template and scroll axis to match the dock edge.</summary>
    private void ApplyItemPresentation()
    {
        var vertical = _edge.IsVertical();

        CardList.ItemTemplate = (DataTemplate)FindResource(vertical ? "RowCardTemplate" : "TileCardTemplate");
        CardList.ItemsPanel = (ItemsPanelTemplate)FindResource(vertical ? "VerticalCardsPanel" : "HorizontalCardsPanel");

        ScrollViewer.SetHorizontalScrollBarVisibility(CardList,
            vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(CardList,
            vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);

        // A right-aligned TextBox sizes to its content, which made the search
        // field visibly grow and shrink as the user typed. On a wide dock an
        // explicit width pins it against the buttons; in the narrow left/right
        // docks 320 would not fit, so let it stretch to whatever the column has.
        if (vertical)
        {
            SearchBox.Width = double.NaN;
            SearchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            SearchBox.Width = 320;
            SearchBox.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private void ApplyGripPlacement()
    {
        if (_edge == DockEdge.Floating)
        {
            ResizeGrip.Visibility = Visibility.Collapsed;
            return;
        }

        ResizeGrip.Visibility = Visibility.Visible;

        // The grip always sits on the edge facing into the screen.
        switch (_edge.InnerEdge())
        {
            case Dock.Top:
                Configure(VerticalAlignment.Top, HorizontalAlignment.Stretch, Cursors.SizeNS, 6, double.NaN, new Thickness(14, 0, 14, 0));
                break;
            case Dock.Bottom:
                Configure(VerticalAlignment.Bottom, HorizontalAlignment.Stretch, Cursors.SizeNS, 6, double.NaN, new Thickness(14, 0, 14, 0));
                break;
            case Dock.Right:
                Configure(VerticalAlignment.Stretch, HorizontalAlignment.Right, Cursors.SizeWE, double.NaN, 6, new Thickness(0, 14, 0, 14));
                break;
            default:
                Configure(VerticalAlignment.Stretch, HorizontalAlignment.Left, Cursors.SizeWE, double.NaN, 6, new Thickness(0, 14, 0, 14));
                break;
        }

        void Configure(
            VerticalAlignment v,
            HorizontalAlignment h,
            Cursor cursor,
            double height,
            double width,
            Thickness margin)
        {
            ResizeGrip.VerticalAlignment = v;
            ResizeGrip.HorizontalAlignment = h;
            ResizeGrip.Cursor = cursor;
            ResizeGrip.Height = height;
            ResizeGrip.Width = width;
            ResizeGrip.Margin = margin;
        }
    }

    private void SetGripVisible(bool visible)
    {
        if (_edge == DockEdge.Floating)
        {
            return;
        }

        ResizeGrip.BeginAnimation(OpacityProperty,
            new DoubleAnimation(visible ? 0.5 : 0.0, new Duration(TimeSpan.FromMilliseconds(120))));
    }

    /// <summary>Re-docks the stash, persisting the choice.</summary>
    public void DockTo(DockEdge edge)
    {
        _edge = edge;
        _vm.Edge = edge;

        _settings.Update(s => s.Edge = edge.ToString());

        ApplyLayout(_vm.TargetWindow);
        ApplyItemPresentation();

        // Land in place rather than replaying the slide, which would look like the
        // stash had closed and reopened.
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        SlideTransform.X = 0;
        SlideTransform.Y = 0;

        _vm.Toast = $"Docked {edge.ToString().ToLowerInvariant()}";
    }

    // ---- Dragging and resizing ---------------------------------------------

    private void OnHeaderDragStart(object sender, MouseButtonEventArgs e)
    {
        // _isDragging guards re-entrancy: DragMove pumps messages, so a second
        // mouse-down can arrive while the first call is still blocked inside it.
        if (e.ClickCount != 1 || _isClosing || _isDragging)
        {
            return;
        }

        _isDragging = true;

        // The clip that keeps the slide overhang off the neighbouring monitor is
        // aligned to that monitor but expressed in window coordinates, so it
        // travels with the window: during a drag it slices pieces off the panel.
        // Drop it for the duration; ApplyLayout restores it on drop.
        WindowRoot.Clip = null;

        try
        {
            // DragMove blocks until the button is released.
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Raised if the button was already up by the time we got here.
        }
        finally
        {
            _isDragging = false;
        }

        SnapAfterDrag();
    }

    /// <summary>
    /// Decides where the panel landed: re-dock if it was dropped near an edge,
    /// otherwise remember the free position.
    /// </summary>
    private void SnapAfterDrag()
    {
        var hwnd = Handle;
        var windowPixels = ScreenGeometry.WindowRect(hwnd);

        if (windowPixels.IsEmpty)
        {
            return;
        }

        // Locate the panel inside the window using the scale the layout was built
        // with, then ask which monitor that rectangle actually sits on.
        //
        // Deliberately not MonitorFromWindow: the window is far larger than the
        // panel and deliberately hangs off the screen edge, so the monitor with
        // the largest slice of *window* is frequently not the monitor the user
        // sees the panel on. Snapping then measured against the wrong work area
        // and appeared to do nothing.
        var panelPixels = PanelRectFrom(windowPixels, _layout.Scale);

        var monitor = ScreenGeometry.ForPoint(new Point(
            panelPixels.Left + (panelPixels.Width / 2),
            panelPixels.Top + (panelPixels.Height / 2)));

        // Re-measure with that monitor's scale in case the drag crossed a DPI
        // boundary, which changes where the panel sits inside the window.
        if (Math.Abs(monitor.Scale - _layout.Scale) > 0.01)
        {
            panelPixels = PanelRectFrom(windowPixels, monitor.Scale);
        }

        // Re-dock relative to where it was dropped, not where it came from.
        _dropMonitor = monitor;

        var target = StashPlacement.SnapTarget(panelPixels, monitor, _settings.Current.SnapDistance);

        if (target == DockEdge.Floating)
        {
            _edge = DockEdge.Floating;
            _vm.Edge = _edge;

            _settings.Update(s =>
            {
                s.Edge = nameof(DockEdge.Floating);
                s.FloatingLeft = panelPixels.Left;
                s.FloatingTop = panelPixels.Top;
            });

            ApplyLayout(_vm.TargetWindow);
            ApplyItemPresentation();
            _vm.Toast = "Floating — drag near an edge to dock";
        }
        else
        {
            DockTo(target);
        }
    }

    /// <summary>
    /// Where the visible panel sits inside the window, in real pixels. The panel
    /// is inset by the shadow pad plus, on the docked side, the slide travel.
    /// </summary>
    private Rect PanelRectFrom(Rect windowPixels, double scale) => new(
        windowPixels.Left + (_layout.PanelMargin.Left * scale),
        windowPixels.Top + (_layout.PanelMargin.Top * scale),
        Math.Max(1, _layout.PanelSize.Width * scale),
        Math.Max(1, _layout.PanelSize.Height * scale));

    private void OnResizeDrag(object sender, DragDeltaEventArgs e)
    {
        // Dragging the inner edge outward makes the panel thicker, so the sign
        // depends on which side the grip is on.
        var delta = _edge switch
        {
            DockEdge.Bottom => -e.VerticalChange,
            DockEdge.Top => e.VerticalChange,
            DockEdge.Left => e.HorizontalChange,
            DockEdge.Right => -e.HorizontalChange,
            _ => 0,
        };

        if (Math.Abs(delta) < 0.5)
        {
            return;
        }

        var current = _edge.IsVertical() ? _settings.Current.PanelWidth : _settings.Current.PanelHeight;
        StashPlacement.StoreThickness(_settings.Current, _edge, current + delta);

        ApplyLayout(_vm.TargetWindow);
    }

    // ---- Scrolling ----------------------------------------------------------

    /// <summary>
    /// Turns the wheel into horizontal movement while the stash is a strip: WPF
    /// would otherwise try to scroll a vertical axis that is disabled.
    /// </summary>
    private void OnCardsMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_edge.IsVertical())
        {
            return;
        }

        var scroller = FindScrollViewer(CardList);
        if (scroller is null)
        {
            return;
        }

        scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - (e.Delta * 0.7));
        e.Handled = true;
    }

    private void ScrollTo(int index)
    {
        if (index < 0 || index >= CardList.Items.Count)
        {
            return;
        }

        // Deferred: the container may not exist yet right after a rebuild.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (index < CardList.Items.Count)
            {
                CardList.ScrollIntoView(CardList.Items[index]);
            }
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer found)
        {
            return found;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    // ---- Keyboard -----------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // With Alt held, WPF reports Key.System and moves the real key to
        // SystemKey. Without this every Alt chord below would be unreachable.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ctrl+arrows re-dock the stash.
        if (ctrl && StashPlacement.EdgeForArrow(key) is { } edge)
        {
            DockTo(edge);
            e.Handled = true;
            return;
        }

        // Ctrl+1..9 pastes the Nth visible card; Alt+1..9 assigns a quick slot.
        if (TryReadDigit(key, out var digit) && digit >= 1)
        {
            if (ctrl)
            {
                _ = _vm.ActivateAtAsync(digit);
                e.Handled = true;
                return;
            }

            if (alt)
            {
                _vm.AssignSlot(digit);
                e.Handled = true;
                return;
            }
        }

        switch (key)
        {
            case Key.Escape:
                HidePanel();
                e.Handled = true;
                break;

            case Key.Enter:
                _ = _vm.ActivateAsync(null);
                e.Handled = true;
                break;

            // Arrows navigate the cards even though the search box holds focus,
            // matching the Windows clipboard panel. Shift+arrow is left alone so
            // text selection still works.
            case Key.Left or Key.Up when !shift:
                _vm.MoveSelection(-1);
                e.Handled = true;
                break;

            case Key.Right or Key.Down when !shift:
                _vm.MoveSelection(1);
                e.Handled = true;
                break;

            case Key.Home when !shift && _vm.SearchText.Length == 0:
                _vm.SelectFirst();
                e.Handled = true;
                break;

            case Key.End when !shift && _vm.SearchText.Length == 0:
                _vm.SelectLast();
                e.Handled = true;
                break;

            case Key.Tab:
                _vm.CycleView(shift ? -1 : 1);
                e.Handled = true;
                break;

            case Key.D when ctrl:
                _vm.ToggleFavorite(null);
                e.Handled = true;
                break;

            case Key.Delete when alt:
                _vm.Delete(null);
                e.Handled = true;
                break;

            case Key.C when alt:
                _vm.CopyOnlyCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.O when alt:
                _vm.OpenCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.OemComma when ctrl:
                SettingsRequested?.Invoke();
                e.Handled = true;
                break;

            case Key.F1:
                HelpRequested?.Invoke();
                e.Handled = true;
                break;
        }
    }

    private static bool TryReadDigit(Key key, out int digit)
    {
        if (key is >= Key.D0 and <= Key.D9)
        {
            digit = key - Key.D0;
            return true;
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            digit = key - Key.NumPad0;
            return true;
        }

        digit = -1;
        return false;
    }

    // ---- Misc ---------------------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StashViewModel.Toast))
        {
            return;
        }

        _toastTimer.Stop();

        if (_vm.HasToast)
        {
            _toastTimer.Start();
        }
    }

    /// <summary>The stash is only ever hidden; closing it would end the session.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current.ShutdownMode != ShutdownMode.OnExplicitShutdown)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        HidePanel();
    }
}
