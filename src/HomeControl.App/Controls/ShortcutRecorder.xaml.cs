using HomeControl.Core.Hotkeys;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace HomeControl.Controls;

/// <summary>
/// Records a global shortcut: click the button, press the keys. While recording, the app's
/// own global shortcuts are released so they can be captured (and re-assigned) too.
/// </summary>
public sealed partial class ShortcutRecorder : UserControl
{
    private Hotkey? _hotkey;
    private bool _recording;

    public ShortcutRecorder()
    {
        InitializeComponent();
        Unloaded += (_, _) => StopRecording();
    }

    /// <summary>Raised when the user recorded or removed a shortcut.</summary>
    public event EventHandler? HotkeyChanged;

    public Hotkey? Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value is { IsValid: true } ? value : null;
            UpdateText();
        }
    }

    /// <summary>Checks a candidate; returns an error message to reject it.</summary>
    public Func<Hotkey, string?>? Validate { get; set; }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            StopRecording();
        }
        else
        {
            StartRecording();
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        StopRecording();
        _hotkey = null;
        UpdateText();
        ShowMessage(null);
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRecordLostFocus(object sender, RoutedEventArgs e) => StopRecording();

    private void OnRecordPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                StopRecording();
                ShowMessage(null);
                return;
            case VirtualKey.Tab:
                StopRecording(); // let focus move on
                return;
        }

        e.Handled = true;
        var modifiers = GetModifiers();
        var key = (int)e.Key;

        if (VirtualKeys.IsModifierKey(key))
        {
            ShortcutText.Text = modifiers == HotkeyModifiers.None ? "Press a shortcut…" : FormatModifiers(modifiers) + " + …";
            return;
        }

        if (!VirtualKeys.IsAssignable(key))
        {
            ShowMessage("This key can't be used in a shortcut.", isError: true);
            return;
        }

        var hotkey = new Hotkey(modifiers, key);
        if (!hotkey.IsValid)
        {
            ShowMessage("Combine the key with Ctrl, Alt, Shift or Win.", isError: true);
            return;
        }

        var error = Validate?.Invoke(hotkey);
        if (error is not null)
        {
            ShowMessage($"{hotkey}: {error}", isError: true);
            return;
        }

        _hotkey = hotkey;
        StopRecording();
        ShowMessage(null);
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartRecording()
    {
        _recording = true;
        App.Host.Hotkeys.Suspend();
        ShortcutText.Text = "Press a shortcut…";
        ShowMessage("Press the keys together. Esc cancels.");
    }

    private void StopRecording()
    {
        if (!_recording)
        {
            return;
        }

        _recording = false;
        App.Host.Hotkeys.Resume();
        UpdateText();
    }

    private void UpdateText()
    {
        ShortcutText.Text = _hotkey?.ToString() ?? "Not set";
        ClearButton.Visibility = _hotkey is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowMessage(string? message, bool isError = false)
    {
        HintText.Text = isError ? string.Empty : message ?? string.Empty;
        ErrorText.Text = isError ? message ?? string.Empty : string.Empty;
        HintText.Visibility = message is not null && !isError ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Visibility = message is not null && isError ? Visibility.Visible : Visibility.Collapsed;
    }

    private static HotkeyModifiers GetModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (IsDown(VirtualKey.Control)) modifiers |= HotkeyModifiers.Control;
        if (IsDown(VirtualKey.Menu)) modifiers |= HotkeyModifiers.Alt;
        if (IsDown(VirtualKey.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= HotkeyModifiers.Windows;
        return modifiers;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static string FormatModifiers(HotkeyModifiers modifiers)
    {
        var parts = new Hotkey(modifiers, 0x41).GetDisplayParts();
        return string.Join(" + ", parts.Take(parts.Count - 1));
    }
}
