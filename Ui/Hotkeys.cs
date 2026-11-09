using System.Runtime.InteropServices;
using System.Windows.Input;

namespace DynaIsland.Ui;

/// <summary>Global keyboard shortcuts (RegisterHotKey): "Ctrl+Alt+Space" style strings.</summary>
public sealed class Hotkeys(IntPtr hwnd) : IDisposable
{
    public const int WM_HOTKEY = 0x0312;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 0x5100;

    /// <summary>Registers a shortcut; returns false if it's empty, invalid or taken by another app.</summary>
    public bool Register(string combo, Action action)
    {
        if (!TryParse(combo, out uint mods, out uint vk)) return false;
        int id = _nextId++;
        if (!RegisterHotKey(hwnd, id, mods | 0x4000 /* MOD_NOREPEAT */, vk))
        {
            Log.Write($"[hotkey] {combo} is already used by another app");
            return false;
        }
        _actions[id] = action;
        return true;
    }

    public void Clear()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(hwnd, id);
        _actions.Clear();
    }

    /// <summary>Call from the window procedure; returns true if the message was one of ours.</summary>
    public bool Handle(int msg, IntPtr wParam)
    {
        if (msg != WM_HOTKEY || !_actions.TryGetValue(wParam.ToInt32(), out var action)) return false;
        action();
        return true;
    }

    public void Dispose() => Clear();

    public static string Format(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        foreach (var part in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= 0x2; break;
                case "alt": mods |= 0x1; break;
                case "shift": mods |= 0x4; break;
                case "win": mods |= 0x8; break;
                default:
                    if (!Enum.TryParse<Key>(part, true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0 && mods != 0;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
