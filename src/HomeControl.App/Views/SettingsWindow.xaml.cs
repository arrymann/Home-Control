using HomeControl.Core.Settings;
using HomeControl.Helpers;
using HomeControl.Interop;
using HomeControl.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;

namespace HomeControl.Views;

/// <summary>Windows 11 style settings window: Mica, custom title bar and a navigation pane.</summary>
public sealed partial class SettingsWindow : Window
{
    private static readonly (string Tag, Type Type)[] Pages =
    [
        ("devices", typeof(DevicesPage)),
        ("scenes", typeof(ScenesPage)),
        ("automations", typeof(AutomationsPage)),
        ("account", typeof(AccountPage)),
        ("general", typeof(GeneralPage)),
    ];

    private readonly IntPtr _hwnd;
    private BackdropKind? _backdrop;
    private Exception? _navigationError;

    public SettingsWindow()
    {
        InitializeComponent();
        _hwnd = WindowHelpers.GetHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(AppPaths.Asset("AppIcon.ico"));
        TitleBarIcon.Source = new BitmapImage(new Uri(AppPaths.Asset("AppIcon.png")));

        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var size = new SizeInt32((int)(1100 * scale), (int)(780 * scale)); // room for the automation editor
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        size.Width = Math.Min(size.Width, work.Width);
        size.Height = Math.Min(size.Height, work.Height);
        AppWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - size.Width) / 2,
            work.Y + (work.Height - size.Height) / 2,
            size.Width,
            size.Height));

        // Leave the current page so it unsubscribes from the app's events (OnNavigatedFrom).
        Closed += (_, _) =>
        {
            try
            {
                ContentFrame.Navigate(typeof(Page));
            }
            catch (Exception ex)
            {
                Log.Error("Leaving the settings page", ex);
            }
        };
    }

    public void Show(string? page)
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Navigate(page ?? CurrentPage ?? "devices");
        AppWindow.Show();
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    /// <summary>The page shown ("devices", "scenes"…): what the frame holds, not what was last asked for.</summary>
    internal string? CurrentPage => Pages.FirstOrDefault(p => p.Type == ContentFrame.Content?.GetType()).Tag;

    public void Navigate(string page)
    {
        if (ItemFor(page) is not { } item)
        {
            return;
        }

        Navigation.SelectedItem = item; // raises SelectionChanged when the selection changes
        Open(page);                     // and when it doesn't, the page must still be the one shown
    }

    /// <summary>Where a page's entry in the navigation pane is, in screen pixels (for the smoke test's clicks).</summary>
    internal Windows.Foundation.Point? ItemCenterOnScreen(string page)
    {
        if (ItemFor(page) is not { ActualWidth: > 0 } item || Content.XamlRoot is not { } root)
        {
            return null;
        }

        var center = item.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(item.ActualWidth / 2, item.ActualHeight / 2));
        var point = new NativeMethods.POINT { X = (int)Math.Round(center.X * root.RasterizationScale), Y = (int)Math.Round(center.Y * root.RasterizationScale) };
        NativeMethods.ClientToScreen(_hwnd, ref point);
        return new Windows.Foundation.Point(point.X, point.Y);
    }

    internal void ApplyAppearance(ElementTheme theme, BackdropKind backdrop)
    {
        Root.RequestedTheme = theme;
        var dark = theme == ElementTheme.Dark;
        WindowHelpers.SetDarkFrame(_hwnd, dark);

        WindowHelpers.SetCaptionButtonColors(AppWindow.TitleBar, dark);

        if (_backdrop != backdrop)
        {
            _backdrop = backdrop;
            SystemBackdrop = Backdrops.Create(backdrop);
        }

        SolidBackground.Visibility = SystemBackdrop is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private NavigationViewItem? ItemFor(string page) =>
        Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == page);

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if ((args.SelectedItem as NavigationViewItem)?.Tag is string page)
        {
            Open(page);
        }
    }

    /// <summary>Every click opens its page, also on the entry that is already selected.</summary>
    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if ((args.InvokedItemContainer as NavigationViewItem)?.Tag is string page)
        {
            Open(page);
        }
    }

    private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        _navigationError = e.Exception;
        e.Handled = true; // Open reports it
    }

    /// <summary>
    /// Shows a page unless it is shown already. When it can't be shown, says so on the window
    /// and in the log, and the pane goes back to the page that is still up, so every entry can
    /// be clicked again.
    /// </summary>
    private void Open(string page)
    {
        var type = Pages.FirstOrDefault(p => p.Tag == page).Type;
        if (type is null || ContentFrame.Content?.GetType() == type)
        {
            return;
        }

        _navigationError = null;
        try
        {
            ContentFrame.Navigate(type, null, new EntranceNavigationTransitionInfo());
        }
        catch (Exception ex)
        {
            _navigationError = ex;
        }

        var shown = ContentFrame.Content?.GetType() == type;
        if (shown && _navigationError is null)
        {
            PageErrorBar.IsOpen = false;
            PageErrorBar.Visibility = Visibility.Collapsed; // and its margin with it
            return;
        }

        var error = _navigationError ?? new InvalidOperationException("The frame didn't show it.");
        Log.Error($"Opening the {page} page", error);
        var name = ItemFor(page)?.Content as string ?? page;
        PageErrorBar.Title = shown ? $"Part of {name} didn't load" : $"{name} didn't open";
        PageErrorBar.Message = $"{error.GetType().Name}: {error.Message} (0x{error.HResult:X8}). It's in the log too: General › Data folder › home-control.log.";
        PageErrorBar.Visibility = Visibility.Visible;
        PageErrorBar.IsOpen = true;

        if (!shown)
        {
            // Not from inside the pane's own selection change.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (CurrentPage is { } current && ItemFor(current) is { } item && !ReferenceEquals(Navigation.SelectedItem, item))
                {
                    Navigation.SelectedItem = item;
                }
            });
        }
    }
}
