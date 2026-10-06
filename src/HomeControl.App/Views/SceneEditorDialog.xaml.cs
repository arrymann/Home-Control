using HomeControl.Core.Models;
using HomeControl.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HomeControl.Views;

/// <summary>A device that can be put in a scene.</summary>
public sealed class SceneDeviceChoice(DeviceConfig device, bool isSelected)
{
    public string Id { get; } = device.Id;

    public string Label { get; } = device.Label.Trim();

    /// <summary>Its room, with the home in front.</summary>
    public string Place { get; } = string.Join(" › ", new[] { device.Home, device.Room }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));

    /// <summary>A fresh geometry per read, as for <see cref="KindOption.Icon"/>.</summary>
    public Geometry Icon => Ui.DeviceIcon(_kind);

    /// <summary>Bound two-way to the check box.</summary>
    public bool IsSelected { get; set; } = isSelected;

    private readonly DeviceKind _kind = device.Kind;
}

/// <summary>Adds or edits a scene.</summary>
public sealed partial class SceneEditorDialog : ContentDialog
{
    private readonly SceneConfig _scene;
    private readonly List<KindOption> _kinds = Enum.GetValues<DeviceKind>().Select(k => new KindOption(k)).ToList();
    private readonly List<SceneDeviceChoice> _choices;

    /// <param name="scene">The scene to edit, or null to add a new one.</param>
    public SceneEditorDialog(SceneConfig? scene)
    {
        InitializeComponent();
        _scene = scene?.Clone() ?? new SceneConfig();
        Title = scene is null ? "New scene" : "Edit scene";

        NameBox.Text = _scene.Name;
        KindBox.ItemsSource = _kinds;
        KindBox.SelectedItem = _kinds.First(k => k.Kind == _scene.Kind);
        ShortcutBox.Hotkey = _scene.Hotkey;
        ShortcutBox.Validate = hotkey => App.Host.ValidateHotkey(hotkey, _scene.Id, isPopupHotkey: false);
        ShowInTrayBox.IsOn = _scene.ShowInTray;

        // Every device that still exists (also those not shown in the tray), by home and room.
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        _choices = App.Host.Settings.Devices
            .Where(d => !d.Missing)
            .OrderBy(d => string.IsNullOrWhiteSpace(d.Home)).ThenBy(d => d.Home ?? string.Empty, comparer)
            .ThenBy(d => string.IsNullOrWhiteSpace(d.Room)).ThenBy(d => d.Room ?? string.Empty, comparer)
            .ThenBy(d => d.Label, comparer)
            .Select(d => new SceneDeviceChoice(d, _scene.DeviceIds.Contains(d.Id)))
            .ToList();
        DeviceChoices.ItemsSource = _choices;
    }

    /// <summary>The edited scene (valid after the dialog returned Primary).</summary>
    public SceneConfig Result => _scene;

    /// <summary>Copies what this dialog edits onto <paramref name="target"/>.</summary>
    internal void ApplyTo(SceneConfig target)
    {
        target.Name = _scene.Name;
        target.Kind = _scene.Kind;
        target.DeviceIds = [.. _scene.DeviceIds];
        target.Hotkey = _scene.Hotkey;
        target.ShowInTray = _scene.ShowInTray;
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e) => ErrorText.Visibility = Visibility.Collapsed;

    private void OnDeviceChoiceChanged(object sender, RoutedEventArgs e) => ErrorText.Visibility = Visibility.Collapsed;

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var selected = _choices.Where(c => c.IsSelected).Select(c => c.Id).ToList();
        var error = string.IsNullOrWhiteSpace(NameBox.Text) ? "Give the scene a name."
            : selected.Count == 0 ? "Pick at least one device."
            : null;
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        _scene.Name = NameBox.Text.Trim();
        _scene.Kind = (KindBox.SelectedItem as KindOption)?.Kind ?? DeviceKind.Scene;
        _scene.Hotkey = ShortcutBox.Hotkey;
        _scene.ShowInTray = ShowInTrayBox.IsOn;

        // Devices it had that aren't listed (gone from Google Home) are kept: they may come back.
        var listed = _choices.Select(c => c.Id).ToHashSet();
        _scene.DeviceIds = [.. _scene.DeviceIds.Where(id => !listed.Contains(id)), .. selected];
    }
}
