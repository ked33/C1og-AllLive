using System.Runtime.InteropServices;
using System.Text;

namespace AllLive.SignService;

// Only the owning UWP process may address one of its own CoreWindows. The enclosing
// ApplicationFrameWindow can belong to ApplicationFrameHost on Windows 10.
internal static class DesktopWindowControl
{
    internal static bool TryExecute(long handle, int parentProcessId, bool toggle, out bool maximized)
    {
        maximized = false;
        if (!OperatingSystem.IsWindows() || handle <= 0 || parentProcessId <= 0
            || (IntPtr.Size == 4 && handle > int.MaxValue)) return false;
        var core = new IntPtr(handle);
        GetWindowThreadProcessId(core, out var processId);
        if (processId != (uint)parentProcessId || ClassName(core) != "Windows.UI.Core.CoreWindow") return false;
        var frame = GetAncestor(core, 2); // GA_ROOT, never the foreground window or a title match.
        if (frame == IntPtr.Zero || (frame != core && ClassName(frame) != "ApplicationFrameWindow")
            || !IsWindowVisible(frame) || IsIconic(frame)
            || (GetWindowLong(frame, -16) & 0x00010000) == 0) return false; // WS_MAXIMIZEBOX

        maximized = IsZoomed(frame);
        if (!toggle) return true;
        // Use the same system command as the caption button; preserve restore bounds,
        // taskbar and title bar. Bound the wait if the target window is closing/hung.
        var command = maximized ? 0xF120 : 0xF030; // SC_RESTORE / SC_MAXIMIZE
        if (SendMessageTimeout(frame, 0x0112, new IntPtr(command), IntPtr.Zero,
            0x0023, 500, out _) == IntPtr.Zero) return false;
        maximized = IsZoomed(frame);
        return true;
    }

    private static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(256);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int maxCount);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
}

internal sealed class WindowControlRequest
{
    public long Handle { get; set; }
}
