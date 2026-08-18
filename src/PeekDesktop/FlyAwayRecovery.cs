using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PeekDesktop;

internal readonly record struct FlyAwayRecoveryWindow(
    long Handle,
    uint ProcessId,
    NativeMethods.WINDOWPLACEMENT Placement,
    NativeMethods.RECT Bounds);

internal readonly record struct FlyAwayRecoveryResult(int Recovered, int Skipped);

internal static class FlyAwayRecovery
{
    private const int SchemaVersion = 1;
    private static readonly string RecoveryDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeekDesktop");
    private static readonly string RecoveryPath = Path.Combine(RecoveryDirectory, "flyaway-recovery.json");

    internal static bool TryWriteSnapshot(IReadOnlyList<FlyAwayRecoveryWindow> windows)
    {
        if (windows.Count == 0)
            return false;

        string tempPath = RecoveryPath + ".tmp";
        try
        {
            Directory.CreateDirectory(RecoveryDirectory);
            byte[] json = Serialize(windows);
            using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, RecoveryPath, overwrite: true);
            AppDiagnostics.Log($"Fly Away recovery snapshot saved for {windows.Count} window(s)");
            return true;
        }
        catch (IOException ex)
        {
            AppDiagnostics.Log($"Failed to save Fly Away recovery snapshot: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            AppDiagnostics.Log($"Failed to save Fly Away recovery snapshot: {ex.Message}");
            return false;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    internal static void RecoverIfNeeded()
    {
        if (!File.Exists(RecoveryPath))
            return;

        List<FlyAwayRecoveryWindow> windows;
        try
        {
            windows = Deserialize(File.ReadAllBytes(RecoveryPath));
        }
        catch (JsonException ex)
        {
            AppDiagnostics.Log($"Discarding malformed Fly Away recovery snapshot: {ex.Message}");
            ClearSnapshot();
            return;
        }
        catch (FormatException ex)
        {
            AppDiagnostics.Log($"Discarding malformed Fly Away recovery snapshot: {ex.Message}");
            ClearSnapshot();
            return;
        }
        catch (InvalidOperationException ex)
        {
            AppDiagnostics.Log($"Discarding malformed Fly Away recovery snapshot: {ex.Message}");
            ClearSnapshot();
            return;
        }
        catch (OverflowException ex)
        {
            AppDiagnostics.Log($"Discarding malformed Fly Away recovery snapshot: {ex.Message}");
            ClearSnapshot();
            return;
        }
        catch (IOException ex)
        {
            AppDiagnostics.Log($"Could not read Fly Away recovery snapshot: {ex.Message}");
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            AppDiagnostics.Log($"Could not read Fly Away recovery snapshot: {ex.Message}");
            return;
        }

        FlyAwayRecoveryResult result = RecoverWindows(windows);
        AppDiagnostics.Log(
            $"Fly Away startup recovery complete: recovered={result.Recovered} skipped={result.Skipped}");
        ClearSnapshot();
    }

    internal static FlyAwayRecoveryResult RecoverWindows(IReadOnlyList<FlyAwayRecoveryWindow> windows)
    {
        int recovered = 0;
        int skipped = 0;

        foreach (FlyAwayRecoveryWindow window in windows)
        {
            IntPtr hwnd = new(window.Handle);
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                skipped++;
                continue;
            }

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint currentProcessId);
            if (currentProcessId == 0 || currentProcessId != window.ProcessId)
            {
                skipped++;
                continue;
            }

            NativeMethods.RECT currentBounds;
            if (NativeMethods.IsIconic(hwnd))
            {
                var currentPlacement = new NativeMethods.WINDOWPLACEMENT
                {
                    length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>()
                };
                if (!NativeMethods.GetWindowPlacement(hwnd, ref currentPlacement))
                {
                    skipped++;
                    continue;
                }

                currentBounds = currentPlacement.rcNormalPosition;
            }
            else if (!NativeMethods.GetWindowRect(hwnd, out currentBounds))
            {
                skipped++;
                continue;
            }

            NativeMethods.RECT monitorProbe = currentBounds;
            if (NativeMethods.MonitorFromRect(ref monitorProbe, NativeMethods.MONITOR_DEFAULTTONULL) != IntPtr.Zero)
            {
                skipped++;
                continue;
            }

            NativeMethods.WINDOWPLACEMENT placement = window.Placement;
            placement.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
            if (NativeMethods.SetWindowPlacement(hwnd, ref placement))
                recovered++;
            else
                skipped++;
        }

        return new FlyAwayRecoveryResult(recovered, skipped);
    }

    internal static void ClearSnapshot()
    {
        if (TryDelete(RecoveryPath))
            AppDiagnostics.Log("Fly Away recovery snapshot cleared");
    }

    internal static byte[] Serialize(IReadOnlyList<FlyAwayRecoveryWindow> windows)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });

        (string? productVersion, Version? fileVersion) = NativeMethods.GetExeVersionInfo();
        string version = productVersion ?? fileVersion?.ToString() ?? "unknown";

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("createdUtc", DateTime.UtcNow);
        writer.WriteString("appVersion", version);
        writer.WriteStartArray("windows");

        foreach (FlyAwayRecoveryWindow window in windows)
        {
            NativeMethods.WINDOWPLACEMENT placement = window.Placement;
            writer.WriteStartObject();
            writer.WriteNumber("handle", window.Handle);
            writer.WriteNumber("processId", window.ProcessId);
            writer.WriteNumber("flags", placement.flags);
            writer.WriteNumber("showCommand", placement.showCmd);
            writer.WriteNumber("minX", placement.ptMinPosition.x);
            writer.WriteNumber("minY", placement.ptMinPosition.y);
            writer.WriteNumber("maxX", placement.ptMaxPosition.x);
            writer.WriteNumber("maxY", placement.ptMaxPosition.y);
            WriteRect(writer, "normal", placement.rcNormalPosition);
            WriteRect(writer, "bounds", window.Bounds);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    internal static List<FlyAwayRecoveryWindow> Deserialize(ReadOnlySpan<byte> json)
    {
        var windows = new List<FlyAwayRecoveryWindow>();
        var reader = new Utf8JsonReader(json);
        int schemaVersion = 0;

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Recovery snapshot root must be an object.");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Unexpected token in recovery snapshot.");

            if (reader.ValueTextEquals("schemaVersion"u8))
            {
                reader.Read();
                schemaVersion = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("windows"u8))
            {
                ReadWindows(ref reader, windows);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (schemaVersion != SchemaVersion)
            throw new JsonException($"Unsupported Fly Away recovery schema version: {schemaVersion}.");

        return windows;
    }

    private static void ReadWindows(
        ref Utf8JsonReader reader,
        List<FlyAwayRecoveryWindow> windows)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Recovery windows must be an array.");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Recovery window entry must be an object.");

            windows.Add(ReadWindow(ref reader));
        }
    }

    private static FlyAwayRecoveryWindow ReadWindow(ref Utf8JsonReader reader)
    {
        long handle = 0;
        uint processId = 0;
        var placement = new NativeMethods.WINDOWPLACEMENT();
        var bounds = new NativeMethods.RECT();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Unexpected token in recovery window entry.");

            if (reader.ValueTextEquals("handle"u8))
            {
                reader.Read();
                handle = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("processId"u8))
            {
                reader.Read();
                processId = reader.GetUInt32();
            }
            else if (reader.ValueTextEquals("flags"u8))
            {
                reader.Read();
                placement.flags = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("showCommand"u8))
            {
                reader.Read();
                placement.showCmd = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("minX"u8))
            {
                reader.Read();
                placement.ptMinPosition.x = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("minY"u8))
            {
                reader.Read();
                placement.ptMinPosition.y = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("maxX"u8))
            {
                reader.Read();
                placement.ptMaxPosition.x = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("maxY"u8))
            {
                reader.Read();
                placement.ptMaxPosition.y = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("normal"u8))
            {
                placement.rcNormalPosition = ReadRect(ref reader);
            }
            else if (reader.ValueTextEquals("bounds"u8))
            {
                bounds = ReadRect(ref reader);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        placement.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
        return new FlyAwayRecoveryWindow(handle, processId, placement, bounds);
    }

    private static void WriteRect(
        Utf8JsonWriter writer,
        string name,
        NativeMethods.RECT rect)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("left", rect.Left);
        writer.WriteNumber("top", rect.Top);
        writer.WriteNumber("right", rect.Right);
        writer.WriteNumber("bottom", rect.Bottom);
        writer.WriteEndObject();
    }

    private static NativeMethods.RECT ReadRect(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Recovery rectangle must be an object.");

        var rect = new NativeMethods.RECT();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Unexpected token in recovery rectangle.");

            bool isLeft = reader.ValueTextEquals("left"u8);
            bool isTop = reader.ValueTextEquals("top"u8);
            bool isRight = reader.ValueTextEquals("right"u8);
            bool isBottom = reader.ValueTextEquals("bottom"u8);
            reader.Read();

            if (isLeft)
                rect.Left = reader.GetInt32();
            else if (isTop)
                rect.Top = reader.GetInt32();
            else if (isRight)
                rect.Right = reader.GetInt32();
            else if (isBottom)
                rect.Bottom = reader.GetInt32();
            else
                reader.Skip();
        }

        return rect;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            File.Delete(path);
            return true;
        }
        catch (IOException ex)
        {
            AppDiagnostics.Log($"Failed to delete Fly Away recovery file {path}: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            AppDiagnostics.Log($"Failed to delete Fly Away recovery file {path}: {ex.Message}");
            return false;
        }
    }
}
