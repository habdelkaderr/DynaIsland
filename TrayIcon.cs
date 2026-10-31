using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DynaIsland;

/// <summary>An on/off option shown in both the tray menu and the island's right-click menu.</summary>
internal sealed record MenuToggle(string Header, Func<bool> Get, Action<bool> Set, bool SeparatorBefore = false);

/// <summary>Notification-area icon with a menu to control the island.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _image;

    public TrayIcon(IReadOnlyList<MenuToggle> toggles, Action<MenuToggle, bool> apply,
                    Func<bool> isIslandVisible, Action<bool> setIslandVisible, Action quit)
    {
        _image = CreateIcon();

        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new Drawing.Font("Segoe UI", 9.5f),
        };

        var title = new Forms.ToolStripLabel("DynaIsland") { Font = new Drawing.Font("Segoe UI Semibold", 9.5f) };
        menu.Items.Add(title);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var showIsland = new Forms.ToolStripMenuItem("Show island") { CheckOnClick = true };
        showIsland.Click += (_, _) => setIslandVisible(showIsland.Checked);
        menu.Items.Add(showIsland);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var items = new List<(Forms.ToolStripMenuItem Item, MenuToggle Toggle)>();
        foreach (var toggle in toggles)
        {
            if (toggle.SeparatorBefore) menu.Items.Add(new Forms.ToolStripSeparator());
            var item = new Forms.ToolStripMenuItem(toggle.Header) { CheckOnClick = true };
            item.Click += (_, _) =>
            {
                apply(toggle, item.Checked);
                item.Checked = toggle.Get();
            };
            menu.Items.Add(item);
            items.Add((item, toggle));
        }

        menu.Items.Add(new Forms.ToolStripSeparator());
        var quitItem = new Forms.ToolStripMenuItem("Quit DynaIsland");
        quitItem.Click += (_, _) => quit();
        menu.Items.Add(quitItem);

        // Keep the ticks in sync with changes made from the island's own menu.
        menu.Opening += (_, _) =>
        {
            showIsland.Checked = isIslandVisible();
            foreach (var (item, toggle) in items) item.Checked = toggle.Get();
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "DynaIsland",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) setIslandVisible(!isIslandVisible());
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _image.Dispose();
    }

    /// <summary>Draws the tray icon: a black island pill with a light outline and "now playing" bars.</summary>
    private static Drawing.Icon CreateIcon()
    {
        int size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
        using var bmp = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            float w = size * 0.94f, h = size * 0.5f;
            float x = (size - w) / 2, y = (size - h) / 2;
            using var pill = new Drawing.Drawing2D.GraphicsPath();
            pill.AddArc(x, y, h, h, 90, 180);
            pill.AddArc(x + w - h, y, h, h, 270, 180);
            pill.CloseFigure();

            using var fill = new Drawing.SolidBrush(Drawing.Color.Black);
            using var outline = new Drawing.Pen(Drawing.Color.FromArgb(200, 255, 255, 255), Math.Max(1f, size / 20f));
            g.FillPath(fill, pill);
            g.DrawPath(outline, pill);

            // Three small bars on the right, like the compact now-playing view.
            using var bar = new Drawing.SolidBrush(Drawing.Color.White);
            float bw = Math.Max(1.5f, size / 14f), gap = bw * 0.9f, cy = size / 2f;
            float[] heights = [0.45f, 0.8f, 0.6f];
            float bx = x + w - h * 0.5f - (bw * 3 + gap * 2);
            for (int i = 0; i < heights.Length; i++)
            {
                float bh = h * 0.62f * heights[i];
                g.FillRectangle(bar, bx + i * (bw + gap), cy - bh / 2, bw, bh);
            }
        }

        IntPtr handle = bmp.GetHicon();
        try
        {
            return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private sealed class DarkRenderer() : Forms.ToolStripProfessionalRenderer(new DarkColors())
    {
        private static readonly Drawing.Color Text = Drawing.Color.FromArgb(240, 240, 242);
        private static readonly Drawing.Color DisabledText = Drawing.Color.FromArgb(120, 120, 126);

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : DisabledText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Drawing.Pen(Text, Math.Max(1.5f, r.Height / 9f));
            e.Graphics.DrawLines(pen,
            [
                new Drawing.PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
                new Drawing.PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                new Drawing.PointF(r.Left + r.Width * 0.80f, r.Top + r.Height * 0.30f),
            ]);
        }
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Background = Drawing.Color.FromArgb(32, 32, 34);
        private static readonly Drawing.Color Highlight = Drawing.Color.FromArgb(56, 56, 60);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(62, 62, 66);

        public override Drawing.Color ToolStripDropDownBackground => Background;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Highlight;
        public override Drawing.Color MenuItemSelected => Highlight;
        public override Drawing.Color MenuItemSelectedGradientBegin => Highlight;
        public override Drawing.Color MenuItemSelectedGradientEnd => Highlight;
        public override Drawing.Color ImageMarginGradientBegin => Background;
        public override Drawing.Color ImageMarginGradientMiddle => Background;
        public override Drawing.Color ImageMarginGradientEnd => Background;
        public override Drawing.Color SeparatorDark => Border;
        public override Drawing.Color SeparatorLight => Background;
        public override Drawing.Color CheckBackground => Background;
        public override Drawing.Color CheckSelectedBackground => Highlight;
        public override Drawing.Color CheckPressedBackground => Highlight;
    }
}
