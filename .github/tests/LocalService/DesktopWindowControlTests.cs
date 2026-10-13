using AllLive.SignService;
using System.Runtime.InteropServices;

internal static class DesktopWindowControlTests
{
    internal static void Run()
    {
        var processId = Environment.ProcessId;
        Reject(0, processId, "null handle");
        Reject(-1, processId, "invalid handle");
        Reject(long.MaxValue, processId, "unknown handle");
        Reject(1, 0, "missing owner");
        if (!OperatingSystem.IsWindows()) return;

        // This hidden test-owned window never becomes visible or receives a command.
        var window = CreateWindowEx(0, "STATIC", "AllLive window validation test", 0,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new Exception("Cannot create hidden validation window.");
        try
        {
            Reject(window.ToInt64(), processId, "same-process non-CoreWindow");
            Reject(window.ToInt64(), processId + 1, "foreign process window");
        }
        finally { DestroyWindow(window); }
        Reject(window.ToInt64(), processId, "destroyed window");
        Console.WriteLine("PASS: desktop control rejects invalid, foreign and non-CoreWindow targets.");
    }

    private static void Reject(long window, int processId, string scenario)
    {
        if (DesktopWindowControl.TryExecute(window, processId, false, out _)
            || DesktopWindowControl.TryExecute(window, processId, true, out _))
            throw new Exception("Unexpected window access: " + scenario);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
}
