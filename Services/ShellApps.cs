using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynaIsland.Services;

/// <summary>Resolves an app's display name and icon from its AppUserModelID (via shell:AppsFolder).</summary>
public static class ShellApps
{
    private static readonly Dictionary<string, (string Name, BitmapSource? Icon)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static (string Name, BitmapSource? Icon) Lookup(string aumid)
    {
        if (Cache.TryGetValue(aumid, out var hit)) return hit;

        string name = Fallback(aumid);
        BitmapSource? icon = null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + aumid, IntPtr.Zero, ref iid, out var item) == 0 && item is not null)
            {
                if (item.GetDisplayName(0, out var display) == 0 && !string.IsNullOrWhiteSpace(display)) name = display;
                if (item is IShellItemImageFactory factory &&
                    factory.GetImage(new SIZE { cx = 64, cy = 64 }, 0x1 /* BIGGERSIZEOK */, out var hbitmap) == 0)
                {
                    try { icon = FromHBitmap(hbitmap); }
                    finally { DeleteObject(hbitmap); }
                }
                Marshal.ReleaseComObject(item);
            }
        }
        catch (Exception ex)
        {
            Log.Error("app lookup", ex);
        }

        return Cache[aumid] = (name, icon);
    }

    /// <summary>Opens the app (the closest thing to clicking the original notification).</summary>
    public static void Launch(string aumid)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{aumid}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("launch app", ex);
        }
    }

    private static string Fallback(string aumid)
    {
        string name = aumid;
        int bang = name.IndexOf('!');
        if (bang > 0) name = name[..bang];
        int underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        int slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name;
    }

    /// <summary>Converts a 32-bit premultiplied-alpha HBITMAP to a frozen BitmapSource, keeping transparency.</summary>
    private static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmBitsPixel != 32) return null;
        int w = bm.bmWidth, h = bm.bmHeight;
        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[w * h * 4];
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbitmap, 0, (uint)h, pixels, ref info, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        source.Freeze();
        return source;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
