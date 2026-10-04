using HomeControl.Core.Settings;
using HomeControl.Helpers;
using HomeControl.Interop;
using HomeControl.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace HomeControl.Views;

/// <summary>Windows 11 style settings window: Mica, custom title bar and a navigation pane.</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly IntPtr _hwnd;
    private BackdropKind? _backdrop;
    private string? _currentPage;

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
        var size = new SizeInt32((int)(1000 * scale), (int)(740 * scale));
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        size.Width = Math.Min(size.Width, work.Width);
        size.Height = Math.Min(size.Height, work.Height);
        AppWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - size.Width) / 2,
            work.Y + (work.Height - size.Height) / 2,
            size.Width,
            size.Height));
    }

    public void Show(string? page)
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Navigate(page ?? _currentPage ?? "devices");
        AppWindow.Show();
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    public void Navigate(string page)
    {
        foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == page)
            {
                Navigation.SelectedItem = item; // raises SelectionChanged
                return;
            }
        }
    }

    internal void ApplyAppearance(ElementTheme theme, BackdropKind backdrop)
    {
        Root.RequestedTheme = theme;
        var dark = theme == ElementTheme.Dark;
        WindowHelpers.SetDarkFrame(_hwnd, dark);

        // The caption buttons are drawn by the system; color them to match the theme.
        var titleBar = AppWindow.TitleBar;
        var foreground = dark ? Colors.White : Colors.Black;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = dark ? ColorHelper.FromArgb(0x5D, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x5C, 0x00, 0x00, 0x00);
        titleBar.ButtonHoverBackgroundColor = dark ? ColorHelper.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x09, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedBackgroundColor = dark ? ColorHelper.FromArgb(0x0A, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x06, 0x00, 0x00, 0x00);

        if (_backdrop != backdrop)
        {
            _backdrop = backdrop;
            SystemBackdrop = Backdrops.Create(backdrop);
        }

        SolidBackground.Visibility = SystemBackdrop is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (page is null || page == _currentPage)
        {
            return;
        }

        var pageType = page switch
        {
            "account" => typeof(AccountPage),
            "general" => typeof(GeneralPage),
            _ => typeof(DevicesPage),
        };

        _currentPage = page;
        ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
    }
}
