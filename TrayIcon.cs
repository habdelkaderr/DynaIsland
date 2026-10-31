using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DynaIsland;

/// <summary>Notification-area icon. Left-click shows/hides the island; right-click opens the shared menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _image;

    public TrayIcon(Forms.ContextMenuStrip menu, Action onLeftClick)
    {
        _image = CreateIcon();
        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "DynaIsland",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) onLeftClick();
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;
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

    /// <summary>Dark menu styling, used for every menu in the app via ToolStripManager.Renderer.</summary>
    internal sealed class DarkRenderer() : Forms.ToolStripProfessionalRenderer(new DarkColors())
    {
        private static readonly Drawing.Color Text = Drawing.Color.FromArgb(240, 240, 242);
        private static readonly Drawing.Color DisabledText = Drawing.Color.FromArgb(120, 120, 126);

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item is Forms.ToolStripLabel ? DisabledText : e.Item.Enabled ? Text : DisabledText;
            base.OnRenderItemText(e);
        }

        // Drawn by hand: the stock renderer switches to the light system highlight for keyboard navigation.
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !e.Item.Pressed) return;
            var bounds = new Drawing.Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using var brush = new Drawing.SolidBrush(DarkColors.HighlightColor);
            e.Graphics.FillRectangle(brush, bounds);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
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
        public static Drawing.Color HighlightColor => Highlight;
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(62, 62, 66);

        public override Drawing.Color ToolStripDropDownBackground => Background;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Highlight;
        public override Drawing.Color MenuItemSelected => Highlight;
        public override Drawing.Color MenuItemSelectedGradientBegin => Highlight;
        public override Drawing.Color MenuItemSelectedGradientEnd => Highlight;
        public override Drawing.Color MenuItemPressedGradientBegin => Highlight;
        public override Drawing.Color MenuItemPressedGradientEnd => Highlight;
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
