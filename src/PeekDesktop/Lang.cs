namespace PeekDesktop;

/// <summary>
/// Provides localized UI strings. Detects Chinese system language at startup;
/// falls back to English for all other locales.
/// </summary>
internal static class Lang
{
    private static readonly bool IsChinese = NativeMethods.IsSystemChinese();

    // --- Tray Menu Items ---

    public static string Tray_Enabled => IsChinese ? "已启用" : "Enabled";
    public static string Tray_StartWithWindows => IsChinese ? "开机自启动" : "Start with Windows";
    public static string Tray_RequireDoubleClick => IsChinese ? "需要双击" : "Require Double-Click";
    public static string Tray_PeekOnDesktopClick => IsChinese ? "点击桌面时预览" : "Peek on Desktop Click";
    public static string Tray_PeekOnTaskbarClick => IsChinese ? "点击任务栏时预览" : "Peek on Taskbar Click";
    public static string Tray_RestoreOnAppSwitch => IsChinese ? "切换应用时恢复所有窗口" : "Restore All Windows on App Switch";
    public static string Tray_PauseWhileGaming => IsChinese ? "游戏/全屏时暂停" : "Pause While Gaming / Full-Screen";
    public static string Tray_ShowDesktop => IsChinese ? "显示桌面 (资源管理器)" : "Show Desktop (Explorer)";
    public static string Tray_FlyAway => IsChinese ? "窗口飞走 (实验性)" : "Fly Away (Experimental)";
    public static string Tray_About => IsChinese ? "关于 PeekDesktop" : "About PeekDesktop";
    public static string Tray_CheckForUpdates => IsChinese ? "检查更新" : "Check for Updates";
    public static string Tray_AutoCheckForUpdates => IsChinese ? "自动检查更新" : "Auto-Check for Updates";
    public static string Tray_Exit => IsChinese ? "退出" : "Exit";

    // --- Tray Tooltip ---

    public static string TrayTooltip_Initial => "PeekDesktop";

    public static string TrayTooltip_Mode(PeekMode mode) =>
        $"PeekDesktop - {PeekModeDisplayName(mode)}";

    public static string PeekModeDisplayName(PeekMode mode) => mode switch
    {
        PeekMode.Minimize => IsChinese ? "经典最小化" : "Classic Minimize",
        PeekMode.FlyAway => IsChinese ? "窗口飞走" : "Fly Away",
        PeekMode.NativeShowDesktop => IsChinese ? "原生显示桌面" : "Native Show Desktop",
        _ => IsChinese ? "预览" : "Peek"
    };

    public static string Version_DevBuild => IsChinese ? "开发版" : "dev build";
    public static string Version_Unknown => IsChinese ? "未知" : "unknown";

    // --- About Dialog ---

    public static string About_Title => IsChinese ? "关于 PeekDesktop" : "About PeekDesktop";

    public static string About_Body(string version) => IsChinese
        ? $"PeekDesktop v{version}\n\n" +
          "点击桌面壁纸即可预览桌面，\n" +
          "如同 macOS Sonoma 一样。\n\n" +
          "点击任意窗口或任务栏即可恢复。\n" +
          "预览模式可在资源管理器显示桌面\n" +
          "和窗口飞走模式之间切换。\n\n" +
          "更新来自 GitHub Releases。\n\n" +
          "github.com/shanselman/PeekDesktop"
        : $"PeekDesktop v{version}\n\n" +
          "Click your desktop wallpaper to peek at your desktop,\n" +
          "just like macOS Sonoma.\n\n" +
          "Click any window or the taskbar to restore.\n" +
          "Peek Style lets you switch between Explorer show desktop\n" +
          "and fly-away mode.\n\n" +
          "Updates come from GitHub Releases.\n\n" +
          "github.com/shanselman/PeekDesktop";

    // --- Balloon Notification ---

    public static string Balloon_UpdateTitle => IsChinese ? "PeekDesktop 有可用更新" : "PeekDesktop Update Available";
    public static string Balloon_UpdateBody(string version) => IsChinese
        ? $"版本 {version} 已可用。点击此处下载并安装。"
        : $"Version {version} is available. Click here to download and install.";

    // --- Program.cs Fatal Error ---

    public static string Fatal_Title => IsChinese ? "PeekDesktop 无法启动" : "PeekDesktop failed to start";

    // --- AppUpdater Dialogs ---

    public static string Update_AlreadyChecking => IsChinese
        ? "PeekDesktop 正在检查更新。" : "PeekDesktop is already checking for updates.";
    public static string Update_AlreadyChecking_Title => IsChinese
        ? "PeekDesktop 更新" : "PeekDesktop Update";

    public static string Update_UpToDate(string version) => IsChinese
        ? $"你已经在使用最新版本的 PeekDesktop ({version})。"
        : $"You're already on the latest version of PeekDesktop ({version}).";
    public static string Update_UpToDate_Title => IsChinese
        ? "PeekDesktop 更新" : "PeekDesktop Update";

    public static string Update_CheckFailed => IsChinese
        ? "PeekDesktop 无法检查更新。" : "PeekDesktop couldn't check for updates.";
    public static string Update_CheckFailed_Title => IsChinese
        ? "更新错误" : "Update Error";

    public static string Update_AvailableBody(string version) => IsChinese
        ? $"PeekDesktop {version} 已可用。\n\n是否立即下载并安装？PeekDesktop 将自动重启。"
        : $"PeekDesktop {version} is available.\n\nDownload and install it now? PeekDesktop will restart automatically.";
    public static string Update_Available_Title => IsChinese
        ? "有可用更新" : "Update Available";

    public static string Update_RestartFailed => IsChinese
        ? "更新已安装，但 PeekDesktop 无法自动重启。\n\n请手动启动 PeekDesktop。"
        : "The update was installed but PeekDesktop could not restart automatically.\n\n" +
          "Please start PeekDesktop manually.";
    public static string Update_RestartFailed_Title => IsChinese
        ? "更新已安装" : "Update Installed";

    public static string Update_InstallFailed => IsChinese
        ? "PeekDesktop 无法安装更新。" : "PeekDesktop couldn't install the update.";
    public static string Update_InstallFailed_Title => IsChinese
        ? "更新错误" : "Update Error";

    // --- VirtualDesktopService ---

    public static string VirtualDesktop_Name => IsChinese
        ? "PeekDesktop (实验性)" : "PeekDesktop (Experimental)";
}
