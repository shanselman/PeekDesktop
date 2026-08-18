using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PeekDesktop.Resources;

namespace PeekDesktop;

public static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0
            && args[0].Equals("--selftest-localization", StringComparison.OrdinalIgnoreCase))
        {
            return RunLocalizationSelfTest();
        }

        bool isRestarting = args.Length > 0
            && args[0].Equals("--restarting", StringComparison.OrdinalIgnoreCase);

        // Acquire single-instance mutex. If restarting after an update,
        // retry for a few seconds while the old process exits.
        _mutex = new Mutex(true, @"Local\PeekDesktop_SingleInstance", out bool isNewInstance);
        if (!isNewInstance)
        {
            if (isRestarting)
            {
                for (int i = 0; i < 20 && !isNewInstance; i++)
                {
                    Thread.Sleep(250);
                    try
                    {
                        isNewInstance = _mutex.WaitOne(0);
                    }
                    catch (AbandonedMutexException)
                    {
                        isNewInstance = true;
                    }
                }
            }

            if (!isNewInstance)
            {
                _mutex.Dispose();
                return 0;
            }
        }

        // Cleanup after mutex so we don't race with an in-flight update
        AppUpdater.CleanupPreviousUpdate();

        try
        {
            ConfigureTraceLogging();
            AppDiagnostics.Log("Program starting");

            using var messageLoop = new Win32MessageLoop();
            AppDiagnostics.Log("Message loop created");

            // Defer initialization until the message loop is pumping so hooks
            // and posted callbacks work correctly.
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
                    HandleFatalStartupError(Strings.StartupDeferredInitializationFailed, ex);
                    messageLoop.Quit();
                }
            });

            messageLoop.Run();
        }
        catch (Exception ex)
        {
            HandleFatalStartupError(Strings.StartupProgramFailed, ex);
        }
        finally
        {
            if (_mutex is not null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Dispose();
            }
        }

        return 0;
    }

    private static DesktopPeek? _desktopPeek;
    private static TrayIcon? _trayIcon;
    private static AppUpdater? _appUpdater;

    private static void Initialize(Win32MessageLoop messageLoop)
    {
        var settings = Settings.Load();
        Settings.SetAutoStart(settings.StartWithWindows);
        _desktopPeek = new DesktopPeek(settings, messageLoop.BeginInvoke);
        _desktopPeek.SetRestoreHiddenWindowsOnAppOpen(settings.RestoreHiddenWindowsOnAppOpen);
        _appUpdater = new AppUpdater(messageLoop);

        // Let the updater release the mutex before relaunching
        AppUpdater.ReleaseMutex = () =>
        {
            try
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
                _mutex = null;
            }
            catch { /* best effort */ }
        };

        _trayIcon = new TrayIcon(messageLoop, _desktopPeek, _appUpdater, settings, () => messageLoop.Quit());

        if (settings.Enabled)
            _desktopPeek.Start();

        if (settings.AutoCheckForUpdates)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);

                if (_appUpdater is not null)
                    await _appUpdater.CheckForUpdatesAsync(interactive: false);
            });
        }
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
            string timestamp = DateTime.Now.ToString(
                "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture);
            File.AppendAllText(
                fatalPath,
                $"[{timestamp}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Last-chance logging only.
        }

        AppDiagnostics.Log($"{context}: {ex}");
        NativeMethods.MessageBoxW(
            IntPtr.Zero,
            Strings.StartupErrorMessage(context, ex.Message),
            Strings.StartupErrorCaption,
            NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
    }

    private static int RunLocalizationSelfTest()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo englishFallback = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentCulture = englishFallback;
            CultureInfo.CurrentUICulture = englishFallback;
            if (!string.Equals(Strings.Enabled, "Enabled", StringComparison.Ordinal))
                return 1;

            CultureInfo simplifiedChinese = CultureInfo.GetCultureInfo("zh-Hans");
            CultureInfo.CurrentCulture = simplifiedChinese;
            CultureInfo.CurrentUICulture = simplifiedChinese;
            return string.Equals(Strings.Enabled, "启用", StringComparison.Ordinal) ? 0 : 2;
        }
        catch (CultureNotFoundException)
        {
            return 3;
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
