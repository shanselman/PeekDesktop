using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PeekDesktop;

internal static class AppDiagnostics
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern void OutputDebugString(string lpOutputString);

    [Conditional("TRACE")]
    public static void Metric(string message)
    {
        WriteLine("BENCH", message);
    }

    [Conditional("TRACE")]
    public static void Log(string message)
    {
        WriteLine(null, message);
    }

    [Conditional("TRACE")]
    public static void LogWindow(string prefix, IntPtr hwnd)
    {
        Log($"{prefix}: {NativeMethods.DescribeWindow(hwnd)}");
    }

    private static void WriteLine(string? category, string message)
    {
        string prefix = category is null ? "PeekDesktop" : $"PeekDesktop {category}";
        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string line = $"[{prefix} {timestamp}] {message}";
        Trace.WriteLine(line);
        OutputDebugString(line);
    }
}
