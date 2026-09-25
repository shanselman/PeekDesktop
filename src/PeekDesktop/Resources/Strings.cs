using System.Globalization;
using System.Resources;

namespace PeekDesktop.Resources;

internal static class Strings
{
    private static readonly ResourceManager ResourceManager = new("PeekDesktop.Resources.Strings", typeof(Strings).Assembly);

    internal static string AboutCaption => GetString(nameof(AboutCaption));
    internal static string AboutMessage => GetString(nameof(AboutMessage));
    internal static string AutoCheckForUpdates => GetString(nameof(AutoCheckForUpdates));
    internal static string CheckForUpdates => GetString(nameof(CheckForUpdates));
    internal static string ClassicMinimize => GetString(nameof(ClassicMinimize));
    internal static string DevBuild => GetString(nameof(DevBuild));
    internal static string Enabled => GetString(nameof(Enabled));
    internal static string Exit => GetString(nameof(Exit));
    internal static string FlyAway => GetString(nameof(FlyAway));
    internal static string FlyAwayExperimental => GetString(nameof(FlyAwayExperimental));
    internal static string NativeShowDesktop => GetString(nameof(NativeShowDesktop));
    internal static string PauseWhileGamingFullscreen => GetString(nameof(PauseWhileGamingFullscreen));
    internal static string Peek => GetString(nameof(Peek));
    internal static string PeekOnDesktopClick => GetString(nameof(PeekOnDesktopClick));
    internal static string PeekOnTaskbarClick => GetString(nameof(PeekOnTaskbarClick));
    internal static string RequireDoubleClick => GetString(nameof(RequireDoubleClick));
    internal static string RestoreAllWindowsOnAppSwitch => GetString(nameof(RestoreAllWindowsOnAppSwitch));
    internal static string ShowDesktopExplorer => GetString(nameof(ShowDesktopExplorer));
    internal static string StartWithWindows => GetString(nameof(StartWithWindows));
    internal static string StartupDeferredInitializationFailed => GetString(nameof(StartupDeferredInitializationFailed));
    internal static string StartupProgramFailed => GetString(nameof(StartupProgramFailed));
    internal static string StartupErrorCaption => GetString(nameof(StartupErrorCaption));
    internal static string Tooltip => GetString(nameof(Tooltip));
    internal static string UnknownVersion => GetString(nameof(UnknownVersion));
    internal static string UpdateAlreadyCheckingMessage => GetString(nameof(UpdateAlreadyCheckingMessage));
    internal static string UpdateAvailableCaption => GetString(nameof(UpdateAvailableCaption));
    internal static string UpdateAvailableBalloonTitle => GetString(nameof(UpdateAvailableBalloonTitle));
    internal static string UpdateCheckErrorCaption => GetString(nameof(UpdateCheckErrorCaption));
    internal static string UpdateInstalledCaption => GetString(nameof(UpdateInstalledCaption));
    internal static string UpdateMenuCaption => GetString(nameof(UpdateMenuCaption));
    internal static string UpdateNewVersion => GetString(nameof(UpdateNewVersion));

    internal static string AboutMessageFormat(string version) => Format(nameof(AboutMessage), version);
    internal static string StartupErrorMessage(string context, string errorMessage) => Format(nameof(StartupErrorMessage), context, errorMessage);
    internal static string TooltipFormat(string mode) => Format(nameof(Tooltip), mode);
    internal static string UpdateAlreadyLatestMessage(string currentVersion) => Format(nameof(UpdateAlreadyLatestMessage), currentVersion);
    internal static string UpdateAvailableBalloonText(string version) => Format(nameof(UpdateAvailableBalloonText), version);
    internal static string UpdateAvailablePromptMessage(string version) => Format(nameof(UpdateAvailablePromptMessage), version);
    internal static string UpdateCheckFailedMessage(string errorMessage) => Format(nameof(UpdateCheckFailedMessage), errorMessage);
    internal static string UpdateInstallFailedMessage(string errorMessage) => Format(nameof(UpdateInstallFailedMessage), errorMessage);
    internal static string UpdateInstalledRestartFailedMessage() => GetString(nameof(UpdateInstalledRestartFailedMessage));

    internal static string GetString(string name)
    {
        try
        {
            string? value = ResourceManager.GetString(name, CultureInfo.CurrentUICulture);
            if (value is not null)
                return value;

            PeekDesktop.AppDiagnostics.Log($"Missing localized string resource: {name}");
        }
        catch (MissingManifestResourceException ex)
        {
            PeekDesktop.AppDiagnostics.Log($"Localization resources unavailable for {name}: {ex.Message}");
        }

        return name;
    }

    private static string Format(string name, params object[] args)
    {
        return string.Format(CultureInfo.CurrentUICulture, GetString(name), args);
    }
}
