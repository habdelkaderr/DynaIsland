using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Imaging;

namespace DynaIsland.Services;

public enum AiKind
{
    Claude,
    ChatGPT,
    Antigravity,
}

public sealed record AiApp(AiKind Kind, string Name, string Aumid, BitmapSource? Icon);

/// <summary>
/// Hands a prompt typed in the island to an AI's desktop app. DynaIsland itself never talks to the
/// internet: it copies the prompt, opens or focuses the app, and pastes it into the app's message box.
/// </summary>
public static class AiHandoff
{
    private static readonly Dictionary<AiKind, string[]> StartNames = new()
    {
        [AiKind.Claude] = ["Claude"],
        [AiKind.ChatGPT] = ["ChatGPT", "ChatGPT Classic"],
        [AiKind.Antigravity] = ["Antigravity"],
    };

    // Process-name fallbacks for apps whose windows don't expose an AppUserModelID.
    private static readonly Dictionary<AiKind, string[]> ProcessHints = new()
    {
        [AiKind.Claude] = ["claude"],
        [AiKind.ChatGPT] = ["chatgpt", "codex"],
        [AiKind.Antigravity] = ["antigravity"],
    };

    /// <summary>Finds the installed AI apps in Start (UI thread).</summary>
    public static List<AiApp> Discover()
    {
        var installed = ShellApps.InstalledApps();
        var result = new List<AiApp>();
        foreach (var (kind, names) in StartNames)
        {
            foreach (var name in names)
            {
                var match = installed.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match.Aumid is null) continue;
                result.Add(new AiApp(kind, kind.ToString(), match.Aumid, ShellApps.Icon(@"shell:AppsFolder\" + match.Aumid)));
                break;
            }
        }
        return result;
    }

    /// <summary>
    /// Sends the prompt to the app. Returns a message for the island when the user needs to finish by hand.
    /// </summary>
    public static async Task<string?> SendAsync(AiApp app, string prompt, bool autoSend)
    {
        CopyToClipboard(prompt);

        if (app.Kind == AiKind.Claude)
        {
            // Claude's desktop app opens a new chat with the text filled in.
            ShellApps.Start($"claude://claude.ai/new?q={Uri.EscapeDataString(prompt)}");
        }
        else if (WindowTracker.Snapshot().FirstOrDefault(w => Belongs(app, w)) is { } existing)
        {
            WindowTracker.Activate(existing.Handle);
        }
        else
        {
            ShellApps.Launch(app.Aumid);
        }

        if (!await WaitForForegroundAsync(app, TimeSpan.FromSeconds(12)))
            return $"Prompt copied. Paste it into {app.Name} (Ctrl+V)";

        await Task.Delay(800); // let the app focus its message box

        // Antigravity opens to the code editor; pasting there would edit a file, so leave it to the user.
        if (app.Kind == AiKind.Antigravity) return "Prompt copied. Paste it into the Agent chat (Ctrl+V)";

        bool editable = await Task.Run(FocusedIsEditable);
        if (!editable) return $"Prompt copied. Click {app.Name}'s message box and press Ctrl+V";

        if (app.Kind != AiKind.Claude) // Claude already has it from the link
        {
            Native.SendKeys(0x11 /* Ctrl */, 0x56 /* V */);
            await Task.Delay(200);
        }
        if (autoSend) Native.SendKeys(0x0D /* Enter */);
        return null;
    }

    private static bool Belongs(AiApp app, AppWindow window)
    {
        if (string.Equals(window.Aumid, app.Aumid, StringComparison.OrdinalIgnoreCase)) return true;
        string exe = Path.GetFileNameWithoutExtension(window.Exe ?? "");
        return ProcessHints[app.Kind].Any(h => exe.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> WaitForForegroundAsync(AiApp app, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var hwnd = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            var (exe, aumid) = Native.ProcessIdentity(pid);
            var window = new AppWindow(hwnd, "", exe, ShellApps.WindowAumid(hwnd) ?? aumid);
            if (Belongs(app, window)) return true;
            await Task.Delay(250);
        }
        return false;
    }

    private static bool FocusedIsEditable()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            var type = focused?.Current.ControlType;
            return type == ControlType.Edit || type == ControlType.Document;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyToClipboard(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch
            {
                Thread.Sleep(50); // clipboard briefly locked by another app
            }
        }
    }
}
