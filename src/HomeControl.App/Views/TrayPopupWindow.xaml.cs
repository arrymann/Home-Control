using System.ComponentModel;
using HomeControl.Core.Settings;
using HomeControl.Helpers;
using HomeControl.Interop;
using HomeControl.Services;
using HomeControl.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace HomeControl.Views;

/// <summary>
/// The flyout-style window that opens above the tray icon: borderless, always on top,
/// hidden from Alt+Tab, sized to its content and closed as soon as it loses focus.
/// </summary>
public sealed partial class TrayPopupWindow : Window
{
    private const double PopupWidth = 360;
    private const double ScreenMargin = 12;

    private enum Edge { Bottom, Top, Left, Right }

    private readonly IntPtr _hwnd;
    private BackdropKind? _backdrop;
    private PointInt32 _anchor;
    private Edge _edge;
    private bool _isOpen;
    private bool _allowClose;
    private bool _resizePending;
    private long _hiddenAt;

    internal TrayPopupWindow(HomeViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        HomeIcon.Data = Ui.HomeGeometry();

        _hwnd = WindowHelpers.GetHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false); // border (for the shadow and rounded corners), no title bar
        }

        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(AppPaths.Asset("AppIcon.ico"));
        AppWindow.Closing += OnClosing;
        WindowHelpers.SetRoundedCorners(_hwnd);
        Activated += OnActivated;

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            Hide();
        };
        Root.KeyboardAccelerators.Add(escape);

        ContentRoot.Loaded += (_, _) => RequestResize();
        ViewModel.LayoutChanged += (_, _) => RequestResize();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public HomeViewModel ViewModel { get; }

    public bool IsOpen => _isOpen;

    /// <summary>The user wants the settings window (argument: page tag or null).</summary>
    internal event EventHandler<string?>? SettingsRequested;

    /// <summary>Opens the popup, or closes it when it is already open (tray icon click).</summary>
    internal void Toggle(PointInt32 anchor)
    {
        if (_isOpen)
        {
            Hide();
            return;
        }

        // Clicking the tray icon while the popup is open first deactivates (and hides) it;
        // that same click must not immediately reopen it.
        if (Environment.TickCount64 - _hiddenAt < 300)
        {
            return;
        }

        ShowAt(anchor);
    }

    internal void ShowAt(PointInt32 anchor)
    {
        _anchor = anchor;
        UpdateBounds(beforeShow: true);

        _isOpen = true;
        AppWindow.Show(true);
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
        PlayShowAnimation();

        // The first time, the content is laid out only now: re-fit to its real height.
        RequestResize();
    }

    internal void Hide()
    {
        if (!_isOpen)
        {
            return;
        }

        _isOpen = false;
        _hiddenAt = Environment.TickCount64;
        AppWindow.Hide();
    }

    internal void ApplyAppearance(ElementTheme theme, BackdropKind backdrop)
    {
        Root.RequestedTheme = theme;
        WindowHelpers.SetDarkFrame(_hwnd, theme == ElementTheme.Dark);

        if (_backdrop != backdrop)
        {
            _backdrop = backdrop;
            SystemBackdrop = Backdrops.Create(backdrop);
        }

        SolidBackground.Visibility = SystemBackdrop is null ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            Hide();
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true; // Alt+F4 just hides the popup
            Hide();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HomeViewModel.IsErrorOpen) or nameof(HomeViewModel.NeedsSignIn) or nameof(HomeViewModel.ShowDevices))
        {
            RequestResize();
        }
    }

    /// <summary>Re-fits the window after the content changed, once layout has settled.</summary>
    private void RequestResize()
    {
        if (!_isOpen || _resizePending)
        {
            return;
        }

        _resizePending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _resizePending = false;
            if (_isOpen)
            {
                UpdateBounds(beforeShow: false);
            }
        });
    }

    /// <summary>Sizes the window to its content and places it next to the taskbar.</summary>
    private void UpdateBounds(bool beforeShow)
    {
        var display = DisplayArea.GetFromPoint(_anchor, DisplayAreaFallback.Nearest);
        var work = display.WorkArea;
        _edge = GetTaskbarEdge(work, display.OuterBounds);

        var scale = WindowHelpers.GetScaleForPoint(_anchor);
        var margin = (int)Math.Round(ScreenMargin * scale);
        var maxHeight = Math.Max(160, work.Height / scale - 2 * ScreenMargin);

        // Not visible yet: move onto the target monitor first so a DPI change happens before sizing.
        if (beforeShow)
        {
            AppWindow.Move(new PointInt32(work.X + margin, work.Y + margin));
        }

        var frame = GetFrameSize();
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(PopupWidth * scale) + frame.Width,
            (int)Math.Round(Math.Min(MeasureContentHeight(), maxHeight) * scale) + frame.Height));
        var width = AppWindow.Size.Width;
        var height = AppWindow.Size.Height;

        int x, y;
        switch (_edge)
        {
            case Edge.Top:
                x = Clamp(_anchor.X - width / 2, work.X + margin, work.X + work.Width - width - margin);
                y = work.Y + margin;
                break;
            case Edge.Left:
                x = work.X + margin;
                y = Clamp(_anchor.Y - height / 2, work.Y + margin, work.Y + work.Height - height - margin);
                break;
            case Edge.Right:
                x = work.X + work.Width - width - margin;
                y = Clamp(_anchor.Y - height / 2, work.Y + margin, work.Y + work.Height - height - margin);
                break;
            default:
                x = Clamp(_anchor.X - width / 2, work.X + margin, work.X + work.Width - width - margin);
                y = work.Y + work.Height - height - margin;
                break;
        }

        AppWindow.Move(new PointInt32(x, y));
    }

    /// <summary>
    /// Pixels the window frame adds around the XAML content (border and invisible resize
    /// borders). Measured from the last layout, because AppWindow.ResizeClient assumes a
    /// caption that this borderless window doesn't have.
    /// </summary>
    private SizeInt32 GetFrameSize()
    {
        if (Root.XamlRoot is { } xamlRoot && Root.ActualWidth > 0 && Root.ActualHeight > 0)
        {
            var size = AppWindow.Size;
            return new SizeInt32(
                Math.Max(0, size.Width - (int)Math.Round(Root.ActualWidth * xamlRoot.RasterizationScale)),
                Math.Max(0, size.Height - (int)Math.Round(Root.ActualHeight * xamlRoot.RasterizationScale)));
        }

        return new SizeInt32(16, 16); // first open; corrected once the content is laid out
    }

    private double MeasureContentHeight()
    {
        if (ContentRoot.XamlRoot is null)
        {
            // Not loaded yet (first open): estimate, then refit once loaded.
            return 120 + (ViewModel.ShowDevices ? ViewModel.Devices.Count * 64 + 8 : 220);
        }

        ContentRoot.Measure(new Size(PopupWidth, double.PositiveInfinity));
        var height = ContentRoot.DesiredSize.Height;
        ContentRoot.InvalidateMeasure();
        return height;
    }

    private static Edge GetTaskbarEdge(RectInt32 work, RectInt32 outer)
    {
        if (work.Y + work.Height < outer.Y + outer.Height) return Edge.Bottom;
        if (work.Y > outer.Y) return Edge.Top;
        if (work.X > outer.X) return Edge.Left;
        if (work.X + work.Width < outer.X + outer.Width) return Edge.Right;
        return Edge.Bottom; // auto-hide taskbar
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(value, max));

    private void PlayShowAnimation()
    {
        var horizontal = _edge is Edge.Left or Edge.Right;
        var from = _edge is Edge.Top or Edge.Left ? -16.0 : 16.0;
        SlideTransform.X = 0;
        SlideTransform.Y = 0;

        var slide = new DoubleAnimation
        {
            From = from,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 },
        };
        Storyboard.SetTarget(slide, SlideTransform);
        Storyboard.SetTargetProperty(slide, horizontal ? "X" : "Y");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(150)),
        };
        Storyboard.SetTarget(fade, ContentRoot);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    private void OnSignInClick(object sender, RoutedEventArgs e) => RequestSettings("account");

    private void OnAddDevicesClick(object sender, RoutedEventArgs e) => RequestSettings("devices");

    private void OnSettingsClick(object sender, RoutedEventArgs e) => RequestSettings(null);

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = ViewModel.RefreshStatesAsync();

    private void OnAllOffClick(object sender, RoutedEventArgs e) => _ = ViewModel.TurnAllOffAsync();

    private void RequestSettings(string? page)
    {
        Hide();
        SettingsRequested?.Invoke(this, page);
    }
}
