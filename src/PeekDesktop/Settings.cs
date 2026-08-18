using System;
using System.Buffers;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace PeekDesktop;

/// <summary>
/// Persists user settings (enabled state, autostart) as JSON in %APPDATA%
/// and manages the Windows Run registry key for auto-start.
/// </summary>
public sealed class Settings
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PeekDesktop");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public bool Enabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool RequireDoubleClick { get; set; } = false;
    public bool PauseWhileFullscreenAppActive { get; set; } = true;
    public bool PeekOnDesktopClick { get; set; } = true;
    public bool PeekOnTaskbarClick { get; set; } = false;
    public bool RestoreHiddenWindowsOnAppOpen { get; set; } = true;
    public bool AutoCheckForUpdates { get; set; } = true;
    public PeekMode PeekMode { get; set; } = PeekMode.NativeShowDesktop;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                byte[] jsonBytes = File.ReadAllBytes(SettingsPath);
                bool missingTaskbarClickSetting = !JsonContainsProperty(jsonBytes, "PeekOnTaskbarClick"u8);
                bool shouldSave = !JsonContainsProperty(jsonBytes, "RestoreHiddenWindowsOnAppOpen"u8)
                    || !JsonContainsProperty(jsonBytes, "AutoCheckForUpdates"u8)
                    || !JsonContainsProperty(jsonBytes, "PeekOnDesktopClick"u8)
                    || missingTaskbarClickSetting;
                Settings settings = DeserializeUtf8(jsonBytes);
                if (missingTaskbarClickSetting)
                    settings.PeekOnTaskbarClick = true;

                PeekMode normalizedMode = NormalizePeekMode(settings.PeekMode);
                if (settings.PeekMode != normalizedMode)
                {
                    AppDiagnostics.Log($"Unsupported peek mode '{settings.PeekMode}' migrated to {normalizedMode}.");
                    settings.PeekMode = normalizedMode;
                    shouldSave = true;
                }

                if (shouldSave)
                    settings.Save();

                return settings;
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Failed to load settings from {SettingsPath}: {ex.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            byte[] jsonBytes = SerializeUtf8();
            File.WriteAllBytes(SettingsPath, jsonBytes);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Failed to save settings to {SettingsPath}: {ex.Message}");
        }
    }

    private static PeekMode NormalizePeekMode(PeekMode peekMode)
    {
        return peekMode switch
        {
            PeekMode.FlyAway => PeekMode.FlyAway,
            PeekMode.NativeShowDesktop => PeekMode.NativeShowDesktop,
            _ => PeekMode.NativeShowDesktop // migrate Minimize, legacy Cloak, VirtualDesktop, etc.
        };
    }

    private static Settings DeserializeUtf8(ReadOnlySpan<byte> utf8Json)
    {
        var settings = new Settings();
        var reader = new Utf8JsonReader(utf8Json);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return settings;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.ValueTextEquals("Enabled"u8))
            {
                reader.Read();
                settings.Enabled = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("StartWithWindows"u8))
            {
                reader.Read();
                settings.StartWithWindows = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("RequireDoubleClick"u8))
            {
                reader.Read();
                settings.RequireDoubleClick = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("PauseWhileFullscreenAppActive"u8))
            {
                reader.Read();
                settings.PauseWhileFullscreenAppActive = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("PeekOnTaskbarClick"u8))
            {
                reader.Read();
                settings.PeekOnTaskbarClick = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("PeekOnDesktopClick"u8))
            {
                reader.Read();
                settings.PeekOnDesktopClick = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("RestoreHiddenWindowsOnAppOpen"u8))
            {
                reader.Read();
                settings.RestoreHiddenWindowsOnAppOpen = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("AutoCheckForUpdates"u8))
            {
                reader.Read();
                settings.AutoCheckForUpdates = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("PeekMode"u8))
            {
                reader.Read();
                settings.PeekMode = (PeekMode)reader.GetInt32();
            }
            else
            {
                reader.Skip();
            }
        }

        return settings;
    }

    private static bool JsonContainsProperty(ReadOnlySpan<byte> utf8Json, ReadOnlySpan<byte> propertyName)
    {
        var reader = new Utf8JsonReader(utf8Json);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return false;

            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.ValueTextEquals(propertyName))
                return true;

            reader.Skip();
        }

        return false;
    }

    private byte[] SerializeUtf8()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteBoolean("Enabled"u8, Enabled);
        writer.WriteBoolean("StartWithWindows"u8, StartWithWindows);
        writer.WriteBoolean("RequireDoubleClick"u8, RequireDoubleClick);
        writer.WriteBoolean("PauseWhileFullscreenAppActive"u8, PauseWhileFullscreenAppActive);
        writer.WriteBoolean("PeekOnDesktopClick"u8, PeekOnDesktopClick);
        writer.WriteBoolean("PeekOnTaskbarClick"u8, PeekOnTaskbarClick);
        writer.WriteBoolean("RestoreHiddenWindowsOnAppOpen"u8, RestoreHiddenWindowsOnAppOpen);
        writer.WriteBoolean("AutoCheckForUpdates"u8, AutoCheckForUpdates);
        writer.WriteNumber("PeekMode"u8, (int)PeekMode);
        writer.WriteEndObject();

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Adds or removes a registry entry under HKCU\...\Run to launch PeekDesktop at login.
    /// No admin rights required.
    /// </summary>
    public static void SetAutoStart(bool enabled)
    {
        try
        {
            const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            const string valueName = "PeekDesktop";

            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key == null)
                return;

            if (enabled)
            {
                string exePath = Environment.ProcessPath ?? "";
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    AppDiagnostics.Log("Auto-start registry update skipped: process path is unavailable.");
                    return;
                }

                string startupCommand = $"\"{exePath}\"";
                string? currentValue = key.GetValue(valueName) as string;
                if (!string.Equals(currentValue, startupCommand, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(valueName, startupCommand);
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Failed to update auto-start registry entry: {ex.Message}");
        }
    }
}
