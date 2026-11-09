using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DynaIsland.Services;

namespace DynaIsland;

// Home card (calendar + AI shortcuts) and the AI prompt box.
public partial class MainWindow
{
    private AiApp? _chatApp;
    private DateTime _suppressClipboardUntil;

    // ---- App shortcuts -----------------------------------------------------------------------

    /// <summary>
    /// Builds the island's app shortcuts from settings (your apps, your order). The first time, they're
    /// seeded with the AI apps found on this PC.
    /// </summary>
    private void LoadAiApps()
    {
        if (_settings.IslandShortcuts is null)
        {
            try
            {
                _settings.IslandShortcuts = AiHandoff.Discover().Select(a => new IslandShortcut(a.Aumid, Ask: true)).ToList();
                _settings.Save();
            }
            catch (Exception ex)
            {
                Log.Error("ai discover", ex);
                _settings.IslandShortcuts = [];
            }
        }

        AiPanel.Children.Clear();
        MediaAiPanel.Children.Clear();
        foreach (var shortcut in _settings.IslandShortcuts)
        {
            var (name, icon) = ShellApps.Lookup(shortcut.Id);
            string? exe = _settings.DockEnabled || _settings.LaunchpadEnabled ? AppCatalog.Find(shortcut.Id)?.Exe : null;
            var app = new AiApp(AiApp.KindFor(name), name, shortcut.Id, icon, exe);
            AiPanel.Children.Add(ShortcutButton(app, shortcut.Ask));
            MediaAiPanel.Children.Add(ShortcutButton(app, shortcut.Ask));
        }
        AiPanel.Visibility = _settings.IslandShortcuts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Small icon-only button (the app's own icon); a soft circle appears on hover.</summary>
    private Button ShortcutButton(AiApp app, bool ask)
    {
        var icon = new Image { Source = app.Icon, Width = 18, Height = 18 };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var button = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = icon,
            Width = 30,
            Height = 30,
            Margin = new Thickness(1, 0, 1, 0),
            ToolTip = ask ? $"Ask {app.Name}" : $"Open {app.Name}",
        };
        button.Click += (_, _) =>
        {
            if (ask) OpenChat(app);
            else ShellApps.Launch(app.Aumid);
        };
        button.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowShortcutMenu(app);
        };
        return button;
    }

    private void ShowShortcutMenu(AiApp app)
    {
        var list = _settings.IslandShortcuts ?? [];
        int index = list.FindIndex(s => s.Id == app.Aumid);
        var entries = new List<Ui.GlassMenu.Entry>
        {
            Ui.GlassMenu.Entry.Header(app.Name),
            new("Ask (prompt box)", () => SetShortcut(index, ask: true), Checked: index >= 0 && list[index].Ask),
            new("Open the app", () => SetShortcut(index, ask: false), Checked: index >= 0 && !list[index].Ask),
            Ui.GlassMenu.Entry.Separator,
            new("Move Left", () => MoveShortcut(index, -1), Enabled: index > 0),
            new("Move Right", () => MoveShortcut(index, 1), Enabled: index >= 0 && index < list.Count - 1),
            new("Remove from Island", () => { list.RemoveAt(index); ShortcutsChanged(); }, Enabled: index >= 0),
            Ui.GlassMenu.Entry.Separator,
            new("Edit Shortcuts…", () => OpenSettings("Dynamic Island")),
        };
        var cursor = System.Windows.Forms.Cursor.Position;
        new Ui.GlassMenu(entries, 200) { Blur = _settings.GlassBlur }.ShowAt(cursor.X, cursor.Y + 8, HorizontalAlignment.Center);
    }

    private void SetShortcut(int index, bool ask)
    {
        if (_settings.IslandShortcuts is not { } list || index < 0) return;
        list[index] = list[index] with { Ask = ask };
        ShortcutsChanged();
    }

    private void MoveShortcut(int index, int delta)
    {
        if (_settings.IslandShortcuts is not { } list || index < 0) return;
        int to = Math.Clamp(index + delta, 0, list.Count - 1);
        (list[index], list[to]) = (list[to], list[index]);
        ShortcutsChanged();
    }

    /// <summary>Saves and rebuilds the shortcuts (also called by the Settings window).</summary>
    private void ShortcutsChanged()
    {
        _settings.Save();
        LoadAiApps();
        _settingsWindow?.Refresh();
    }

    private void OpenChat(AiApp app)
    {
        _chatApp = app;
        ChatIcon.Source = app.Icon;
        ChatPlaceholder.Text = $"Ask {app.Name}…";
        ChatInput.Text = "";
        ChatPlaceholder.Visibility = Visibility.Visible;
        _transient = null;

        // The island normally never takes focus; it needs it to receive typing.
        SetActivatable(true);
        Native.SetForegroundWindow(_hwnd);
        Activate();
        UpdateState();
        Dispatcher.InvokeAsync(() => { ChatInput.Focus(); Keyboard.Focus(ChatInput); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseChat()
    {
        if (_chatApp is null) return;
        _chatApp = null;
        SetActivatable(false);
        UpdateState();
    }

    private void SetActivatable(bool activatable)
    {
        if (_hwnd == IntPtr.Zero) return;
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex = activatable ? ex & ~Native.WS_EX_NOACTIVATE : ex | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    private void OnChatTextChanged(object sender, TextChangedEventArgs e) =>
        ChatPlaceholder.Visibility = ChatInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnChatKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseChat();
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendChat();
        }
    }

    private void OnChatSend(object sender, RoutedEventArgs e) => SendChat();

    private async void SendChat()
    {
        if (_chatApp is not { } app) return;
        string prompt = ChatInput.Text.Trim();
        if (prompt.Length == 0) return;

        CloseChat();
        _suppressClipboardUntil = DateTime.Now.AddSeconds(3); // our own copy isn't a "Copied" moment
        ShowNotice("", app.Icon, app.Name, $"Opening {app.Name}…", prompt, ColorUtil.Neutral, 2500, null);

        try
        {
            string? help = await AiHandoff.SendAsync(app, prompt, _settings.AiAutoSend);
            if (help is not null) ShowNotice("", app.Icon, app.Name, "Almost there", help, ColorUtil.Neutral, 6000, null);
        }
        catch (Exception ex)
        {
            Log.Error("ai handoff", ex);
        }
    }

    // ---- Calendar on the Home card -----------------------------------------------------------

    private void UpdateCalendarUi()
    {
        CalendarPanel.Children.Clear();
        if (!_settings.ShowCalendar)
        {
            CalendarPanel.Visibility = Visibility.Collapsed;
            return;
        }
        CalendarPanel.Visibility = Visibility.Visible;

        if (!_calendar.OutlookRunning)
        {
            CalendarPanel.Children.Add(Faint("Calendar appears here while classic Outlook is open"));
            return;
        }

        var events = _calendar.Events.Take(2).ToList();
        if (events.Count == 0)
        {
            CalendarPanel.Children.Add(Faint("No more events in the next 24 hours"));
            return;
        }

        foreach (var meeting in events)
        {
            var now = DateTime.Now;
            string when = meeting.IsNow ? "Now" : meeting.Start.Date == now.Date ? meeting.Start.ToString("t") : $"Tomorrow {meeting.Start:t}";
            int minutes = (int)Math.Ceiling((meeting.Start - now).TotalMinutes);
            string soon = !meeting.IsNow && minutes is > 0 and <= 60 ? $"  ·  in {minutes} min" : "";

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Rectangle
            {
                Width = 3,
                Height = 16,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(meeting.IsNow ? Green : Red),
                Margin = new Thickness(0, 0, 9, 0),
            });
            var time = new TextBlock { Text = when, FontSize = 12, Foreground = (Brush)FindResource("SubtleText"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(time, 1);
            row.Children.Add(time);
            var subject = new TextBlock
            {
                Text = (string.IsNullOrWhiteSpace(meeting.Subject) ? "(No title)" : meeting.Subject) + soon,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(subject, 2);
            row.Children.Add(subject);
            CalendarPanel.Children.Add(row);
        }
    }

    private TextBlock Faint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = (Brush)FindResource("FaintText"),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
}
