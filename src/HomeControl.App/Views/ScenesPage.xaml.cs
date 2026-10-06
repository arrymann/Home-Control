using HomeControl.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HomeControl.Views;

/// <summary>A row on the Scenes page.</summary>
public sealed class SceneListItem
{
    internal SceneListItem(SceneConfig scene, IReadOnlyList<DeviceConfig> devices, int index, int count)
    {
        Id = scene.Id;
        Name = string.IsNullOrWhiteSpace(scene.Name) ? "Scene" : scene.Name.Trim();
        Kind = scene.Kind;
        ShowInTray = scene.ShowInTray;
        CanMoveUp = index > 0;
        CanMoveDown = index < count - 1;

        // The devices it switches, the shortcut, and whether it is in the tray.
        var members = scene.DeviceIds
            .Select(id => devices.FirstOrDefault(d => d.Id == id && !d.Missing))
            .OfType<DeviceConfig>()
            .Select(d => d.Label.Trim())
            .ToList();
        var parts = new List<string>
        {
            members.Count switch
            {
                0 => "No devices",
                <= 4 => string.Join(", ", members),
                _ => string.Join(", ", members.Take(3)) + $" and {members.Count - 3} more",
            },
        };
        if (scene.DeviceIds.Count > members.Count)
        {
            parts.Add(scene.DeviceIds.Count - members.Count == 1 ? "1 device is gone" : $"{scene.DeviceIds.Count - members.Count} devices are gone");
        }

        parts.Add(scene.Hotkey is { IsValid: true } hotkey ? $"Shortcut: {hotkey}" : "No shortcut");
        if (!scene.ShowInTray)
        {
            parts.Add("Not in the tray");
        }

        Description = string.Join("  ·  ", parts);
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public DeviceKind Kind { get; }

    public bool ShowInTray { get; }

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }
}

/// <summary>Settings › Scenes: groups of devices with one toggle in the tray.</summary>
public sealed partial class ScenesPage : Page
{
    public ScenesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        App.Host.SettingsApplied += OnSettingsApplied;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Host.SettingsApplied -= OnSettingsApplied;
    }

    private void OnSettingsApplied(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var settings = App.Host.Settings;
        var scenes = settings.Scenes;
        SceneList.ItemsSource = scenes.Select((s, i) => new SceneListItem(s, settings.Devices, i, scenes.Count)).ToList();
        ListHeader.Visibility = scenes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = scenes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnNewClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SceneEditorDialog(null) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            App.Host.Settings.Scenes.Add(dialog.Result);
            App.Host.SaveSettings();
        }
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is not { } scene)
        {
            return;
        }

        var dialog = new SceneEditorDialog(scene) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
            App.Host.Settings.Scenes.FirstOrDefault(s => s.Id == scene.Id) is { } current)
        {
            dialog.ApplyTo(current);
            App.Host.SaveSettings();
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is not { } scene)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"Delete the scene {(string.IsNullOrWhiteSpace(scene.Name) ? "" : scene.Name.Trim())}?",
            Content = "Its devices stay as they are.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            App.Host.Settings.Scenes.RemoveAll(s => s.Id == scene.Id);
            App.Host.SaveSettings();
        }
    }

    private void OnShowInTrayToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || Find(sender) is not { } scene || scene.ShowInTray == toggle.IsOn)
        {
            return; // initial binding, or nothing changed
        }

        scene.ShowInTray = toggle.IsOn;
        App.Host.SaveSettings();
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => Move(sender, -1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int offset)
    {
        var scenes = App.Host.Settings.Scenes;
        var index = Find(sender) is { } scene ? scenes.IndexOf(scene) : -1;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= scenes.Count)
        {
            return;
        }

        (scenes[index], scenes[target]) = (scenes[target], scenes[index]);
        App.Host.SaveSettings();
    }

    private static SceneConfig? Find(object sender) =>
        (sender as FrameworkElement)?.DataContext is SceneListItem item
            ? App.Host.Settings.Scenes.FirstOrDefault(s => s.Id == item.Id)
            : null;
}
