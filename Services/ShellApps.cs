using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynaIsland.Services;

/// <summary>
/// Shell helpers: app names and icons (from an AppUserModelID or any file path), the list of
/// installed apps (shell:AppsFolder), window / shortcut AppUserModelIDs and shortcut targets.
/// Call from the UI (STA) thread.
/// </summary>
public static class ShellApps
{
    private static readonly Dictionary<string, (string Name, BitmapSource? Icon)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BitmapSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Display name and icon for an AppUserModelID.</summary>
    public static (string Name, BitmapSource? Icon) Lookup(string aumid)
    {
        if (Cache.TryGetValue(aumid, out var hit)) return hit;
        string name = Fallback(aumid);
        var item = CreateItem(@"shell:AppsFolder\" + aumid);
        if (item is not null)
        {
            if (item.GetDisplayName(0, out var display) == 0 && !string.IsNullOrWhiteSpace(display)) name = display;
            Marshal.ReleaseComObject(item);
        }
        return Cache[aumid] = (name, Icon(@"shell:AppsFolder\" + aumid));
    }

    /// <summary>Icon for a file, shortcut, folder or shell:AppsFolder\&lt;AUMID&gt; path.</summary>
    public static BitmapSource? Icon(string parsingName, int size = 64)
    {
        string key = $"{size}|{parsingName}";
        if (IconCache.TryGetValue(key, out var cached)) return cached;
        BitmapSource? icon = null;
        var item = CreateItem(parsingName);
        if (item is not null)
        {
            try
            {
                if (item is IShellItemImageFactory factory &&
                    factory.GetImage(new SIZE { cx = size, cy = size }, 0x1 /* BIGGERSIZEOK */, out var hbitmap) == 0)
                {
                    try { icon = FromHBitmap(hbitmap); }
                    finally { DeleteObject(hbitmap); }
                }
            }
            catch (Exception ex)
            {
                Log.Error("icon", ex);
            }
            Marshal.ReleaseComObject(item);
        }
        return IconCache[key] = icon;
    }

    /// <summary>Installed apps from Start (name, AppUserModelID).</summary>
    public static List<(string Name, string Aumid)> InstalledApps()
    {
        var result = new List<(string, string)>();
        try
        {
            var folderId = FOLDERID_AppsFolder;
            var iid = typeof(IShellItem).GUID;
            if (SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref iid, out var folder) != 0 || folder is null) return result;
            var bhid = BHID_EnumItems;
            var enumIid = typeof(IEnumShellItems).GUID;
            if (folder.BindToHandler(IntPtr.Zero, ref bhid, ref enumIid, out var ptr) == 0 && ptr != IntPtr.Zero)
            {
                var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(ptr);
                Marshal.Release(ptr);
                while (items.Next(1, out var child, out uint fetched) == 0 && fetched == 1)
                {
                    if (child.GetDisplayName(0, out var name) == 0 &&
                        child.GetDisplayName(0x80018001 /* PARENTRELATIVEPARSING */, out var id) == 0)
                        result.Add((name, id));
                    Marshal.ReleaseComObject(child);
                }
                Marshal.ReleaseComObject(items);
            }
            Marshal.ReleaseComObject(folder);
        }
        catch (Exception ex)
        {
            Log.Error("installed apps", ex);
        }
        return result;
    }

    /// <summary>AppUserModelID a window declares (UWP frames, Chromium/Electron apps, ...), if any.</summary>
    public static string? WindowAumid(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        return SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) == 0 ? ReadAumid(store) : null;
    }

    /// <summary>Target path and AppUserModelID of a .lnk shortcut.</summary>
    public static (string? Target, string? Aumid) Shortcut(string lnkPath)
    {
        string? target = null, aumid = null;
        try
        {
            var link = (IShellLinkW)new ShellLinkComObject();
            ((IPersistFile)link).Load(lnkPath, 0);
            var buffer = new StringBuilder(1024);
            if (link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0) == 0 && buffer.Length > 0) target = buffer.ToString();
            Marshal.ReleaseComObject(link);

            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, 0, ref iid, out var store) == 0) aumid = ReadAumid(store);
        }
        catch (Exception ex)
        {
            Log.Error("shortcut", ex);
        }
        return (target, aumid);
    }

    /// <summary>Opens an app by AppUserModelID.</summary>
    public static void Launch(string aumid) => Start("explorer.exe", $"shell:AppsFolder\\{aumid}");

    public static void Start(string file, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments ?? "") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("launch", ex);
        }
    }

    private static string? ReadAumid(IPropertyStore? store)
    {
        if (store is null) return null;
        try
        {
            var key = PKEY_AppUserModel_ID;
            if (store.GetValue(ref key, out var value) != 0) return null;
            try { return value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.p) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static IShellItem? CreateItem(string parsingName)
    {
        var iid = typeof(IShellItem).GUID;
        return SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var item) == 0 ? item : null;
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

    // ---- Interop ----------------------------------------------------------------------------

    private static readonly Guid FOLDERID_AppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt, r1, r2, r3;
        public IntPtr p, p2;
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

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem item, out uint fetched);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkComObject;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderItem(ref Guid folderId, uint flags, IntPtr token, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr pbc, int flags, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT value);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
