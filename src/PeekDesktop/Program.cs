using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PeekDesktop;

public static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    public static void Main()
    {
        _mutex = new Mutex(true, @"Local\PeekDesktop_SingleInstance", out bool isNewInstance);
        if (!isNewInstance)
            return;

        try
        {
            ConfigureTraceLogging();
            AppDiagnostics.Log("Program starting");

            using var messageLoop = new Win32MessageLoop();
            AppDiagnostics.Log("Message loop created");

            // Defer initialization until the message loop is pumping so hooks
            // and SynchronizationContext-like callbacks work correctly.
            messageLoop.PostDeferredAction(1, () =>
            {
                try
                {
                    AppDiagnostics.Log("Deferred initialization starting");
                    Initialize(messageLoop);
                    AppDiagnostics.Log("Deferred initialization complete");
                }
                catch (Exception ex)
                {
                    HandleFatalStartupError("Deferred initialization failed", ex);
                    messageLoop.Quit();
                }
            });

            messageLoop.Run();
        }
        catch (Exception ex)
        {
            HandleFatalStartupError("Program startup failed", ex);
        }
        finally
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    private static DesktopPeek? _desktopPeek;
    private static TrayIcon? _trayIcon;
    private static AppUpdater? _appUpdater;

    private static void Initialize(Win32MessageLoop messageLoop)
    {
        var settings = Settings.Load();
        Settings.SetAutoStart(settings.StartWithWindows);
        _desktopPeek = new DesktopPeek(settings);
        _appUpdater = new AppUpdater(messageLoop);
        _trayIcon = new TrayIcon(messageLoop, _desktopPeek, _appUpdater, settings, () => messageLoop.Quit());

        if (settings.Enabled)
            _desktopPeek.Start();

        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);

            if (_appUpdater is not null)
                await _appUpdater.CheckForUpdatesAsync(interactive: false);
        });
    }

    private static void ConfigureTraceLogging()
    {
        string logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PeekDesktop");

        Directory.CreateDirectory(logDir);

        string logPath = Path.Combine(logDir, "PeekDesktop.log");
        Trace.Listeners.Clear();
        Trace.Listeners.Add(new TextWriterTraceListener(logPath));
        Trace.AutoFlush = true;
    }

    private static void HandleFatalStartupError(string context, Exception ex)
    {
        try
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PeekDesktop");
            Directory.CreateDirectory(logDir);

            string fatalPath = Path.Combine(logDir, "startup-error.log");
            File.AppendAllText(
                fatalPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Last-chance logging only.
        }

        AppDiagnostics.Log($"{context}: {ex}");
        NativeMethods.MessageBoxW(
            IntPtr.Zero,
            $"{context}\n\n{ex.Message}",
            "PeekDesktop failed to start",
            NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
    }
}
