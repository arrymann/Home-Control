using HomeControl.Core.Hotkeys;
using HomeControl.Interop;
using static HomeControl.Interop.NativeMethods;

namespace HomeControl.Services;

/// <summary>A global shortcut and what it does.</summary>
internal sealed record HotkeyBinding(Hotkey Hotkey, string Description, Action Action);

/// <summary>Registers system-wide shortcuts with RegisterHotKey on the tray window.</summary>
internal sealed class HotkeyService : IDisposable
{
    private const int ProbeId = 0xBFFF;

    private readonly MessageWindow _window;
    private readonly Dictionary<int, HotkeyBinding> _registered = [];
    private IReadOnlyList<HotkeyBinding> _bindings = [];
    private int _suspendCount;

    public HotkeyService(MessageWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
    }

    /// <summary>Bindings that Windows refused (usually because another app owns the shortcut).</summary>
    public IReadOnlyList<HotkeyBinding> Failed { get; private set; } = [];

    /// <summary>Replaces all registrations.</summary>
    public void Apply(IEnumerable<HotkeyBinding> bindings)
    {
        _bindings = bindings.Where(b => b.Hotkey.IsValid).ToList();
        if (_suspendCount == 0)
        {
            RegisterAll();
        }
    }

    /// <summary>Temporarily releases every shortcut, e.g. while the user records a new one.</summary>
    public void Suspend()
    {
        if (_suspendCount++ == 0)
        {
            UnregisterAll();
        }
    }

    public void Resume()
    {
        if (_suspendCount > 0 && --_suspendCount == 0)
        {
            RegisterAll();
        }
    }

    /// <summary>Checks whether another application already owns a shortcut.</summary>
    public bool IsAvailable(Hotkey hotkey)
    {
        if (!hotkey.IsValid)
        {
            return false;
        }

        if (_registered.Values.Any(b => b.Hotkey == hotkey))
        {
            return true; // ours
        }

        if (!RegisterHotKey(_window.Handle, ProbeId, (uint)hotkey.Modifiers | MOD_NOREPEAT, (uint)hotkey.Key))
        {
            return false;
        }

        UnregisterHotKey(_window.Handle, ProbeId);
        return true;
    }

    private void RegisterAll()
    {
        UnregisterAll();
        var failed = new List<HotkeyBinding>();
        var id = 1;
        foreach (var binding in _bindings)
        {
            if (_registered.Values.Any(b => b.Hotkey == binding.Hotkey) ||
                !RegisterHotKey(_window.Handle, id, (uint)binding.Hotkey.Modifiers | MOD_NOREPEAT, (uint)binding.Hotkey.Key))
            {
                failed.Add(binding);
                continue;
            }

            _registered[id++] = binding;
        }

        Failed = failed;
        foreach (var binding in failed)
        {
            Log.Info($"Shortcut {binding.Hotkey} for {binding.Description} could not be registered.");
        }
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
        {
            UnregisterHotKey(_window.Handle, id);
        }

        _registered.Clear();
    }

    private void OnMessage(uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_HOTKEY && _registered.TryGetValue((int)(long)wParam, out var binding))
        {
            handled = true;
            binding.Action();
        }
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.MessageReceived -= OnMessage;
    }
}
