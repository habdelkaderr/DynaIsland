using System.Runtime.InteropServices;

namespace DynaIsland.Services;

/// <summary>
/// Built-in display brightness through WMI (the same mechanism Windows' slider uses on laptops),
/// via late-bound COM so no extra library is needed. External monitors aren't controllable this way;
/// <see cref="Get"/> returns null when brightness isn't supported.
/// </summary>
public static class BrightnessControl
{
    private const string Namespace = @"winmgmts:{impersonationLevel=impersonate}!\\.\root\wmi";

    public static int? Get()
    {
        try
        {
            dynamic wmi = BindToMoniker(Namespace);
            foreach (dynamic item in wmi.ExecQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE"))
                return Convert.ToInt32(item.CurrentBrightness);
        }
        catch
        {
            // no internal panel, or WMI unavailable
        }
        return null;
    }

    private static int _pending = -1;
    private static int _worker;

    /// <summary>Sets brightness off the UI thread; while dragging, only the latest value is applied.</summary>
    public static void Request(int percent)
    {
        Volatile.Write(ref _pending, percent);
        if (Interlocked.Exchange(ref _worker, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                int value;
                while ((value = Interlocked.Exchange(ref _pending, -1)) >= 0) Set(value);
            }
            finally
            {
                Volatile.Write(ref _worker, 0);
                if (Volatile.Read(ref _pending) >= 0) Request(Volatile.Read(ref _pending));
            }
        });
    }

    public static bool Set(int percent)
    {
        try
        {
            dynamic wmi = BindToMoniker(Namespace);
            foreach (dynamic item in wmi.ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE"))
            {
                // Provider methods need their parameters passed as a WMI in-parameters object.
                dynamic inParams = item.Methods_.Item("WmiSetBrightness").InParameters.SpawnInstance_();
                inParams.Properties_.Item("Timeout").Value = 1;
                inParams.Properties_.Item("Brightness").Value = (byte)Math.Clamp(percent, 0, 100);
                item.ExecMethod_("WmiSetBrightness", inParams);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("brightness", ex);
        }
        return false;
    }

    private static object BindToMoniker(string name)
    {
        Marshal.ThrowExceptionForHR(CreateBindCtx(0, out var ctx));
        Marshal.ThrowExceptionForHR(MkParseDisplayName(ctx, name, out _, out var moniker));
        var iid = new Guid("00020400-0000-0000-C000-000000000046"); // IDispatch
        moniker.BindToObject(ctx, null, ref iid, out var result);
        return result;
    }

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out System.Runtime.InteropServices.ComTypes.IBindCtx ctx);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int MkParseDisplayName(System.Runtime.InteropServices.ComTypes.IBindCtx ctx, string name,
        out int eaten, out System.Runtime.InteropServices.ComTypes.IMoniker moniker);
}
