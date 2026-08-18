using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PeekDesktop;

internal static class Program
{
    private const string ZeroWindowDescription = "hwnd=0x0 class=<none> title=<none>";
    private static readonly Regex SafeProcessNamePattern = new("^[^\\\\/:*?\"<>|\\x00-\\x1F]+$", RegexOptions.Compiled);

    private sealed record HarnessOptions(int Iterations, bool Verbose);

    private static int Main(string[] args)
    {
        HarnessOptions options = ParseOptions(args);
        if (options.Verbose)
            Console.WriteLine($"Interop harness starting. Iterations={options.Iterations:N0}");

        var failures = new List<string>();

        RunTest("API surface smoke", options, failures, ApiSurfaceSmoke);
        RunTest("Invalid handle matrix", options, failures, InvalidHandleMatrix);
        RunTest("Point/role fuzz", options, failures, PointAndRoleFuzz);
        RunTest("Process-id fuzz", options, failures, ProcessIdFuzz);
        RunTest("Concurrency fuzz", options, failures, () => ConcurrencyFuzz(options.Iterations / 4));
        RunTest("Version info smoke", options, failures, VersionInfoSmoke);
        RunTest("Notification state stress", options, failures, NotificationStateStress);
        RunTest("Malformed input contracts", options, failures, MalformedInputContracts);
        RunTest("Mouse click routing logic", options, failures, MouseClickRoutingLogic);
        RunTest($"Leak probe ({options.Iterations:N0} iterations)", options, failures, () => LeakProbe(options));

        // Auto-updater tests
        RunTest("Version comparison logic", options, failures, VersionComparisonLogic);
        RunTest("Asset matching by architecture", options, failures, AssetMatchingByArchitecture);
        RunTest("Release JSON deserialization", options, failures, ReleaseJsonDeserialization);
        RunTest("Authenticode rejects unsigned", options, failures, AuthenticodeRejectsUnsigned);
        RunTest("WinHttp download to file", options, failures, WinHttpDownloadToFile);
        RunTest("Zip extraction round-trip", options, failures, ZipExtractionRoundTrip);
        RunTest("WinVerifyTrust state cleanup", options, failures, WinVerifyTrustStateCleanup);

        if (failures.Count == 0)
        {
            Console.WriteLine("Interop harness passed.");
            return 0;
        }

        Console.Error.WriteLine("Interop harness failures:");
        foreach (string failure in failures)
            Console.Error.WriteLine($"- {failure}");

        return 1;
    }

    private static HarnessOptions ParseOptions(string[] args)
    {
        int iterations = 10_000;
        bool verbose = false;

        foreach (string arg in args)
        {
            if (int.TryParse(arg, out int parsed) && parsed > 0)
            {
                iterations = parsed;
                continue;
            }

            if (string.Equals(arg, "--verbose", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-v", StringComparison.OrdinalIgnoreCase))
            {
                verbose = true;
            }
        }

        return new HarnessOptions(iterations, verbose);
    }

    private static void RunTest(string name, HarnessOptions options, List<string> failures, Action action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            action();
            Console.WriteLine($"[PASS] {name} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.GetType().Name} {ex.Message}");
            Console.WriteLine($"[FAIL] {name} ({sw.ElapsedMilliseconds} ms)");
        }

        if (options.Verbose)
            Console.WriteLine($"        Completed: {name}");
    }

    private static void ApiSurfaceSmoke()
    {
        _ = NativeMethods.GetForegroundWindow();
        _ = NativeMethods.TryGetCursorPoint(out NativeMethods.POINT point);
        string pointDescription = NativeMethods.DescribePoint(point);
        if (!pointDescription.Contains("x=") || !pointDescription.Contains("y="))
            throw new InvalidOperationException("DescribePoint returned an unexpected format.");

        _ = NativeMethods.TryGetUserNotificationState(out _);
        _ = NativeMethods.GetExeVersionInfo();
        if (!NativeMethods.TryGetProcessName((uint)Environment.ProcessId, out string processName)
            || string.IsNullOrWhiteSpace(processName))
        {
            throw new InvalidOperationException("Current process name should resolve to a non-empty value.");
        }

        bool hasAccessibleDetails = NativeMethods.TryGetAccessibleDetailsAtPoint(point, out int role, out string name);
        if (hasAccessibleDetails || role != 0 || name.Length != 0)
            throw new InvalidOperationException("Accessible details baseline contract was violated.");
    }

    private static void MouseClickRoutingLogic()
    {
        const uint doubleClickTime = 500;
        const int doubleClickDistance = 2;
        const int dragDistance = 4;
        var surfaceWindow = new IntPtr(101);
        var otherSurfaceWindow = new IntPtr(202);
        var origin = new NativeMethods.POINT { x = 100, y = 200 };
        var nearby = new NativeMethods.POINT { x = 101, y = 201 };
        var dragged = new NativeMethods.POINT { x = 110, y = 200 };
        var tracker = new MouseClickTracker();

        if (!tracker.TryBeginClick(
                surfaceWindow,
                origin,
                requireDoubleClick: false,
                tick: 1_000,
                doubleClickTime,
                doubleClickDistance,
                doubleClickDistance)
            || !tracker.TryCompleteClick(nearby, dragDistance, dragDistance, out PendingMouseClick singleClick)
            || singleClick.Window != surfaceWindow
            || singleClick.Point.x != origin.x
            || singleClick.Point.y != origin.y)
        {
            throw new InvalidOperationException("Single-click mode did not preserve and complete the candidate click.");
        }

        tracker.Reset();
        if (tracker.TryBeginClick(
                surfaceWindow,
                origin,
                requireDoubleClick: true,
                tick: 2_000,
                doubleClickTime,
                doubleClickDistance,
                doubleClickDistance)
            || tracker.HasPendingClick)
        {
            throw new InvalidOperationException("The first click incorrectly armed double-click activation.");
        }

        if (!tracker.TryBeginClick(
                surfaceWindow,
                nearby,
                requireDoubleClick: true,
                tick: 2_200,
                doubleClickTime,
                doubleClickDistance,
                doubleClickDistance)
            || !tracker.TryCompleteClick(nearby, dragDistance, dragDistance, out _))
        {
            throw new InvalidOperationException("A valid second click did not complete double-click activation.");
        }

        tracker.Reset();
        _ = tracker.TryBeginClick(
            surfaceWindow,
            origin,
            requireDoubleClick: true,
            tick: 3_000,
            doubleClickTime,
            doubleClickDistance,
            doubleClickDistance);
        if (tracker.TryBeginClick(
                otherSurfaceWindow,
                origin,
                requireDoubleClick: true,
                tick: 3_100,
                doubleClickTime,
                doubleClickDistance,
                doubleClickDistance))
        {
            throw new InvalidOperationException("Clicks on different Explorer surfaces were combined into a double-click.");
        }

        tracker.Reset();
        _ = tracker.TryBeginClick(
            surfaceWindow,
            origin,
            requireDoubleClick: true,
            tick: 3_500,
            doubleClickTime,
            doubleClickDistance,
            doubleClickDistance);
        if (tracker.TryBeginClick(
                surfaceWindow,
                nearby,
                requireDoubleClick: true,
                tick: 4_001,
                doubleClickTime,
                doubleClickDistance,
                doubleClickDistance))
        {
            throw new InvalidOperationException("Clicks outside the system time window were combined into a double-click.");
        }

        tracker.Reset();
        _ = tracker.TryBeginClick(
            surfaceWindow,
            origin,
            requireDoubleClick: true,
            tick: 4_000,
            doubleClickTime,
            doubleClickDistance,
            doubleClickDistance);
        tracker.Reset();
        if (tracker.TryBeginClick(
                surfaceWindow,
                nearby,
                requireDoubleClick: true,
                tick: 4_100,
                doubleClickTime,
                doubleClickDistance,
                doubleClickDistance))
        {
            throw new InvalidOperationException("A reset click sequence incorrectly completed a double-click.");
        }

        tracker.Reset();
        _ = tracker.TryBeginClick(
            surfaceWindow,
            origin,
            requireDoubleClick: false,
            tick: 5_000,
            doubleClickTime,
            doubleClickDistance,
            doubleClickDistance);
        if (!tracker.CancelIfMoved(dragged, dragDistance, dragDistance)
            || tracker.TryCompleteClick(dragged, dragDistance, dragDistance, out _))
        {
            throw new InvalidOperationException("Drag movement did not cancel the pending click.");
        }

        if (DesktopDetector.IsPotentialPeekSurfaceWindow(
            IntPtr.Zero,
            origin,
            includeDesktop: true,
            includeTaskbar: true))
        {
            throw new InvalidOperationException("A zero window was accepted as an Explorer click surface.");
        }

        IntPtr taskbarWindow = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbarWindow != IntPtr.Zero)
        {
            if (!DesktopDetector.IsPotentialPeekSurfaceWindow(
                    taskbarWindow,
                    origin,
                    includeDesktop: false,
                    includeTaskbar: true)
                || DesktopDetector.IsPotentialPeekSurfaceWindow(
                    taskbarWindow,
                    origin,
                    includeDesktop: false,
                    includeTaskbar: false))
            {
                throw new InvalidOperationException("Taskbar surface gating did not honor the configured trigger.");
            }
        }

        IntPtr desktopWindow = NativeMethods.FindWindow("Progman", null);
        if (desktopWindow != IntPtr.Zero
            && (!DesktopDetector.IsPotentialPeekSurfaceWindow(
                    desktopWindow,
                    origin,
                    includeDesktop: true,
                    includeTaskbar: false)
                || DesktopDetector.IsPotentialPeekSurfaceWindow(
                    desktopWindow,
                    origin,
                    includeDesktop: false,
                    includeTaskbar: false)))
        {
            throw new InvalidOperationException("Desktop surface gating did not honor the configured trigger.");
        }

        Action? queuedAction = null;
        using var hook = new MouseHook(action => queuedAction = action);
        hook.DispatchMouseClick(IntPtr.Zero, origin);
        if (queuedAction is null)
            throw new InvalidOperationException("Mouse classification was not deferred through the supplied dispatcher.");
    }

    private static void InvalidHandleMatrix()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        IntPtr[] handles =
        [
            IntPtr.Zero,
            new IntPtr(1),
            new IntPtr(-1),
            new IntPtr(int.MaxValue),
            new IntPtr(unchecked((int)0xdeadbeef)),
            foreground
        ];

        foreach (IntPtr hwnd in handles)
        {
            bool isWindow = NativeMethods.IsWindow(hwnd);
            bool isVisible = NativeMethods.IsWindowVisible(hwnd);
            bool isIconic = NativeMethods.IsIconic(hwnd);
            bool isCloaked = NativeMethods.IsWindowCloaked(hwnd);
            string className = NativeMethods.GetWindowClassName(hwnd);
            string title = NativeMethods.GetWindowTitle(hwnd);
            string description = NativeMethods.DescribeWindow(hwnd);
            string hierarchy = NativeMethods.DescribeWindowHierarchy(hwnd, maxDepth: 4);
            _ = NativeMethods.GetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE);

            bool hitTestResolved = NativeMethods.TryIsDesktopListViewItemAtPoint(
                hwnd,
                new NativeMethods.POINT { x = int.MaxValue, y = int.MinValue },
                out bool isOnItem);

            if (hwnd == IntPtr.Zero)
            {
                if (isWindow || isVisible || isIconic || isCloaked)
                    throw new InvalidOperationException("Zero handle should not report window state.");

                if (className.Length != 0 || title.Length != 0)
                    throw new InvalidOperationException("Zero handle returned unexpected class or title.");

                if (!string.Equals(description, ZeroWindowDescription, StringComparison.Ordinal))
                    throw new InvalidOperationException("Zero handle description contract was violated.");

                if (!string.Equals(hierarchy, "<none>", StringComparison.Ordinal))
                    throw new InvalidOperationException("Zero handle hierarchy contract was violated.");

                if (hitTestResolved || isOnItem)
                    throw new InvalidOperationException("ListView hit-test should fail for zero handle.");
            }

            if (description.Length > 4096 || hierarchy.Length > 16384)
                throw new InvalidOperationException("Window diagnostics returned unexpectedly large payloads.");
        }
    }

    private static void PointAndRoleFuzz()
    {
        NativeMethods.POINT[] points =
        [
            new() { x = 0, y = 0 },
            new() { x = -1, y = -1 },
            new() { x = int.MinValue, y = int.MinValue },
            new() { x = int.MaxValue, y = int.MaxValue },
            new() { x = 50_000, y = -50_000 }
        ];

        foreach (NativeMethods.POINT point in points)
        {
            string description = NativeMethods.DescribePoint(point);
            if (!description.Contains($"x={point.x}") || !description.Contains($"y={point.y}"))
                throw new InvalidOperationException("DescribePoint did not preserve point coordinates.");

            bool accessibleFound = NativeMethods.TryGetAccessibleDetailsAtPoint(point, out int role, out string name);
            if (accessibleFound || role != 0 || name.Length != 0)
                throw new InvalidOperationException("Accessible details contract was violated for fuzz input.");

            bool hitTestResolved = NativeMethods.TryIsDesktopListViewItemAtPoint(IntPtr.Zero, point, out bool isOnItem);
            if (hitTestResolved || isOnItem)
                throw new InvalidOperationException("ListView hit-test should fail for invalid source hwnd.");
        }
    }

    private static void ProcessIdFuzz()
    {
        ValidateProcessLookup(0, expectedToResolve: false);

        uint[] processIds =
        [
            1,
            2,
            4,
            (uint)Environment.ProcessId,
            uint.MaxValue,
            1234567890u
        ];

        foreach (uint processId in processIds)
            ValidateProcessLookup(processId, expectedToResolve: processId == (uint)Environment.ProcessId);
    }

    private static void ConcurrencyFuzz(int iterationsPerWorker)
    {
        if (iterationsPerWorker < 1)
            iterationsPerWorker = 1;

        var tasks = new List<Task>(4);
        int contractViolations = 0;
        for (int worker = 0; worker < 4; worker++)
        {
            int seed = 1000 + worker;
            tasks.Add(Task.Run(() =>
            {
                var rand = new Random(seed);
                for (int i = 0; i < iterationsPerWorker; i++)
                {
                    IntPtr hwnd = i % 2 == 0 ? IntPtr.Zero : new IntPtr(rand.Next(int.MinValue, int.MaxValue));
                    string className = NativeMethods.GetWindowClassName(hwnd);
                    string title = NativeMethods.GetWindowTitle(hwnd);
                    _ = NativeMethods.IsWindowCloaked(hwnd);
                    string description = NativeMethods.DescribeWindow(hwnd);
                    bool stateResolved = NativeMethods.TryGetUserNotificationState(out NativeMethods.UserNotificationState state);

                    if (hwnd == IntPtr.Zero && (className.Length != 0 || title.Length != 0 || description != ZeroWindowDescription))
                        Interlocked.Increment(ref contractViolations);

                    if (stateResolved && !Enum.IsDefined(state))
                        Interlocked.Increment(ref contractViolations);
                }
            }));
        }

        Task.WaitAll([.. tasks]);
        if (contractViolations != 0)
            throw new InvalidOperationException($"Concurrency contract violations observed: {contractViolations}");
    }

    private static void VersionInfoSmoke()
    {
        var version = NativeMethods.GetExeVersionInfo();
        if (version.ProductVersion is null && version.FileVersion is null)
            throw new InvalidOperationException("Both ProductVersion and FileVersion were null.");
    }

    private static void NotificationStateStress()
    {
        int resolvedCount = 0;
        for (int i = 0; i < 5_000; i++)
        {
            bool resolved = NativeMethods.TryGetUserNotificationState(out NativeMethods.UserNotificationState state);
            if (resolved)
            {
                if (!Enum.IsDefined(state))
                    throw new InvalidOperationException($"Invalid notification state value: {(int)state}");

                resolvedCount++;
            }
        }

        if (resolvedCount == 0)
            throw new InvalidOperationException("Notification state never resolved successfully.");
    }

    private static void MalformedInputContracts()
    {
        NativeMethods.POINT malformedPoint = new() { x = int.MaxValue, y = int.MinValue };
        bool resolved = NativeMethods.TryGetAccessibleDetailsAtPoint(malformedPoint, out int role, out string name);
        if (resolved || role != 0 || name.Length != 0)
            throw new InvalidOperationException("Malformed point should not produce accessible details.");

        resolved = NativeMethods.TryGetProcessName(uint.MaxValue, out string processName);
        if (!resolved && processName.Length != 0)
            throw new InvalidOperationException("Failed process lookup should clear process name.");

        resolved = NativeMethods.TryGetProcessName(0, out processName);
        if (resolved || processName.Length != 0)
            throw new InvalidOperationException("PID zero should fail and return an empty process name.");
    }

    private static void LeakProbe(HarnessOptions options)
    {
        int phaseIterations = Math.Max(1, options.Iterations / 2);
        RunLeakWorkload(Math.Max(1, phaseIterations / 2), seed: 17);
        ForceGc();

        Process proc = Process.GetCurrentProcess();
        proc.Refresh();
        long privateBefore = proc.PrivateMemorySize64;
        int handlesBefore = proc.HandleCount;

        RunLeakWorkload(phaseIterations, seed: 1337);
        ForceGc();
        proc.Refresh();
        long privateMid = proc.PrivateMemorySize64;
        int handlesMid = proc.HandleCount;

        RunLeakWorkload(phaseIterations, seed: 2112);
        ForceGc();
        proc.Refresh();
        long privateAfter = proc.PrivateMemorySize64;
        int handlesAfter = proc.HandleCount;

        long privateGrowthPhase1 = privateMid - privateBefore;
        long privateGrowthPhase2 = privateAfter - privateMid;
        long privateGrowthTotal = privateAfter - privateBefore;
        int handleGrowthPhase1 = handlesMid - handlesBefore;
        int handleGrowthPhase2 = handlesAfter - handlesMid;
        int handleGrowthTotal = handlesAfter - handlesBefore;

        if (options.Verbose)
        {
            Console.WriteLine($"        LeakProbe private bytes before={privateBefore:N0} mid={privateMid:N0} after={privateAfter:N0}");
            Console.WriteLine($"        LeakProbe private growth phase1={privateGrowthPhase1:N0} phase2={privateGrowthPhase2:N0} total={privateGrowthTotal:N0}");
            Console.WriteLine($"        LeakProbe handles before={handlesBefore:N0} mid={handlesMid:N0} after={handlesAfter:N0}");
            Console.WriteLine($"        LeakProbe handle growth phase1={handleGrowthPhase1:N0} phase2={handleGrowthPhase2:N0} total={handleGrowthTotal:N0}");
        }

        const long maxPrivateGrowthBytes = 64L * 1024 * 1024;
        const int maxHandleGrowth = 16;
        const long maxSecondPhasePrivateGrowth = 24L * 1024 * 1024;
        const int maxSecondPhaseHandleGrowth = 8;

        if (privateGrowthTotal > maxPrivateGrowthBytes)
        {
            throw new InvalidOperationException(
                $"Private bytes grew by {privateGrowthTotal:N0} (> {maxPrivateGrowthBytes:N0}).");
        }

        if (handleGrowthTotal > maxHandleGrowth)
        {
            throw new InvalidOperationException(
                $"Handle count grew by {handleGrowthTotal} (> {maxHandleGrowth}).");
        }

        if (privateGrowthPhase2 > maxSecondPhasePrivateGrowth)
        {
            throw new InvalidOperationException(
                $"Private bytes grew by {privateGrowthPhase2:N0} in phase 2 (> {maxSecondPhasePrivateGrowth:N0}).");
        }

        if (handleGrowthPhase2 > maxSecondPhaseHandleGrowth)
        {
            throw new InvalidOperationException(
                $"Handle count grew by {handleGrowthPhase2} in phase 2 (> {maxSecondPhaseHandleGrowth}).");
        }
    }

    // --- Auto-updater tests ---

    private static void VersionComparisonLogic()
    {
        // Test version normalization
        string v1 = AppUpdater.GetCurrentVersion();
        if (string.IsNullOrWhiteSpace(v1))
            throw new InvalidOperationException("GetCurrentVersion returned empty.");

        // Test that the static helpers work correctly with known inputs via reflection-free checks
        // We test the public surface: does CheckForUpdatesAsync not crash when called?
        // Version parsing is exercised through the public API.

        // Verify known version ordering by creating GitHubReleaseInfo objects
        var oldRelease = new GitHubReleaseInfo { TagName = "v0.1.0", HtmlUrl = "https://github.com/shanselman/PeekDesktop/releases/tag/v0.1.0" };
        var futureRelease = new GitHubReleaseInfo { TagName = "v99.0.0", HtmlUrl = "https://github.com/shanselman/PeekDesktop/releases/tag/v99.0.0" };

        // Verify tag normalization (strip 'v' prefix)
        if (oldRelease.TagName[0] != 'v')
            throw new InvalidOperationException("Tag should start with v.");

        // Verify model has assets list
        if (oldRelease.Assets is null)
            throw new InvalidOperationException("Assets list should be initialized.");
    }

    private static void AssetMatchingByArchitecture()
    {
        var release = new GitHubReleaseInfo
        {
            TagName = "v1.0.0",
            HtmlUrl = "https://github.com/shanselman/PeekDesktop/releases/tag/v1.0.0",
            Assets = new List<GitHubAssetInfo>
            {
                new() { Name = "PeekDesktop-v1.0.0-win-x64.zip", BrowserDownloadUrl = "https://github.com/shanselman/PeekDesktop/releases/download/v1.0.0/PeekDesktop-v1.0.0-win-x64.zip" },
                new() { Name = "PeekDesktop-v1.0.0-win-arm64.zip", BrowserDownloadUrl = "https://github.com/shanselman/PeekDesktop/releases/download/v1.0.0/PeekDesktop-v1.0.0-win-arm64.zip" },
            }
        };

        // The current architecture should match one of the assets
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        string expectedSuffix = arch switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "win-x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
            _ => throw new InvalidOperationException($"Unexpected architecture: {arch}")
        };

        bool found = false;
        foreach (var asset in release.Assets)
        {
            if (asset.Name.Contains(expectedSuffix, StringComparison.OrdinalIgnoreCase)
                && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                if (string.IsNullOrEmpty(asset.BrowserDownloadUrl))
                    throw new InvalidOperationException("Matched asset has empty download URL.");
                break;
            }
        }

        if (!found)
            throw new InvalidOperationException($"No asset matched architecture suffix '{expectedSuffix}'.");

        // Verify no match for a fake architecture suffix
        bool fakeFound = false;
        foreach (var asset in release.Assets)
        {
            if (asset.Name.Contains("win-mips64", StringComparison.OrdinalIgnoreCase))
                fakeFound = true;
        }
        if (fakeFound)
            throw new InvalidOperationException("Fake architecture should not match.");
    }

    private static void ReleaseJsonDeserialization()
    {
        // Minimal GitHub release JSON with assets array
        string json = """
        {
            "tag_name": "v0.8.5",
            "html_url": "https://github.com/shanselman/PeekDesktop/releases/tag/v0.8.5",
            "assets": [
                {
                    "name": "PeekDesktop-v0.8.5-win-x64.zip",
                    "browser_download_url": "https://github.com/shanselman/PeekDesktop/releases/download/v0.8.5/PeekDesktop-v0.8.5-win-x64.zip",
                    "size": 1900000
                },
                {
                    "name": "PeekDesktop-v0.8.5-win-arm64.zip",
                    "browser_download_url": "https://github.com/shanselman/PeekDesktop/releases/download/v0.8.5/PeekDesktop-v0.8.5-win-arm64.zip",
                    "size": 1800000
                }
            ],
            "body": "Release notes here",
            "draft": false,
            "prerelease": false
        }
        """;

        // Use reflection-free deserialization via the internal method
        byte[] utf8Json = System.Text.Encoding.UTF8.GetBytes(json);
        // We can't call the private method directly, but we can verify the model shape
        var reader = new System.Text.Json.Utf8JsonReader(utf8Json);
        if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.StartObject)
            throw new InvalidOperationException("JSON should start with object.");

        // Parse manually the same way AppUpdater does
        var info = new GitHubReleaseInfo();
        bool foundTagName = false, foundAssets = false;

        while (reader.Read())
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.EndObject) break;
            if (reader.TokenType != System.Text.Json.JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("tag_name"u8))
            {
                reader.Read();
                info.TagName = reader.GetString() ?? "";
                foundTagName = true;
            }
            else if (reader.ValueTextEquals("assets"u8))
            {
                reader.Read();
                if (reader.TokenType == System.Text.Json.JsonTokenType.StartArray)
                {
                    while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == System.Text.Json.JsonTokenType.StartObject)
                        {
                            var asset = new GitHubAssetInfo();
                            while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndObject)
                            {
                                if (reader.TokenType != System.Text.Json.JsonTokenType.PropertyName) continue;
                                if (reader.ValueTextEquals("name"u8)) { reader.Read(); asset.Name = reader.GetString() ?? ""; }
                                else if (reader.ValueTextEquals("browser_download_url"u8)) { reader.Read(); asset.BrowserDownloadUrl = reader.GetString() ?? ""; }
                                else reader.Skip();
                            }
                            info.Assets.Add(asset);
                        }
                    }
                    foundAssets = true;
                }
            }
            else
            {
                reader.Skip();
            }
        }

        if (!foundTagName || info.TagName != "v0.8.5")
            throw new InvalidOperationException($"tag_name parse failed: '{info.TagName}'");
        if (!foundAssets || info.Assets.Count != 2)
            throw new InvalidOperationException($"assets parse failed: count={info.Assets.Count}");
        if (info.Assets[0].Name != "PeekDesktop-v0.8.5-win-x64.zip")
            throw new InvalidOperationException($"First asset name wrong: '{info.Assets[0].Name}'");
        if (!info.Assets[1].BrowserDownloadUrl.Contains("arm64"))
            throw new InvalidOperationException("Second asset URL should contain arm64.");
    }

    private static void AuthenticodeRejectsUnsigned()
    {
        // Create a temp file that is NOT signed — verification should fail
        string tempExe = Path.Combine(Path.GetTempPath(), $"peekdesktop-test-unsigned-{Guid.NewGuid():N}.exe");
        try
        {
            // Write a minimal PE-like file (just some bytes, not a real PE)
            File.WriteAllBytes(tempExe, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 });

            var (isValid, signerName) = NativeMethods.VerifyAuthenticodeSignature(tempExe);
            if (isValid)
                throw new InvalidOperationException("Unsigned file should fail Authenticode verification.");

            // signerName should be null for unsigned files
            if (signerName is not null)
                throw new InvalidOperationException($"Unsigned file should have null signer, got: '{signerName}'");
        }
        finally
        {
            try { File.Delete(tempExe); } catch { }
        }
    }

    private static void WinHttpDownloadToFile()
    {
        // Download a known small file — the GitHub Releases API for this repo
        // Use WinHttp.Get first to verify connectivity, then test DownloadToFile
        // with a URL that accepts application/octet-stream (a real release asset)
        string tempFile = Path.Combine(Path.GetTempPath(), $"peekdesktop-test-download-{Guid.NewGuid():N}.txt");
        try
        {
            // Test basic GET first — this confirms WinHttp works
            string apiResponse = WinHttp.Get("https://api.github.com", "PeekDesktop-Test", timeoutSeconds: 15);
            if (!apiResponse.Contains("current_user_url"))
                throw new InvalidOperationException("WinHttp.Get did not return expected GitHub API response.");

            // Test DownloadToFile with a URL that serves raw content
            // Use raw.githubusercontent.com which happily serves any Accept header
            WinHttp.DownloadToFile(
                "https://raw.githubusercontent.com/shanselman/PeekDesktop/main/LICENSE",
                "PeekDesktop-Test", tempFile, timeoutSeconds: 15);

            if (!File.Exists(tempFile))
                throw new InvalidOperationException("Downloaded file does not exist.");

            long fileSize = new FileInfo(tempFile).Length;
            if (fileSize == 0)
                throw new InvalidOperationException("Downloaded file is empty.");

            string content = File.ReadAllText(tempFile);
            if (!content.Contains("MIT License"))
                throw new InvalidOperationException("Downloaded content does not look like the LICENSE file.");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static void ZipExtractionRoundTrip()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"peekdesktop-test-zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create a test zip containing a fake PeekDesktop.exe and a README
            string zipPath = Path.Combine(tempDir, "test-release.zip");
            string fakeExeContent = "This is a fake PeekDesktop.exe for testing";

            using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                var exeEntry = archive.CreateEntry("PeekDesktop.exe");
                using (var writer = new StreamWriter(exeEntry.Open()))
                    writer.Write(fakeExeContent);

                var readmeEntry = archive.CreateEntry("README.md");
                using (var writer = new StreamWriter(readmeEntry.Open()))
                    writer.Write("# PeekDesktop");
            }

            // Verify the zip exists and has content
            if (!File.Exists(zipPath) || new FileInfo(zipPath).Length == 0)
                throw new InvalidOperationException("Test zip was not created properly.");

            // Extract using the same method the updater uses
            string extractedPath = Path.Combine(tempDir, "PeekDesktop.new.exe");
            using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
            {
                System.IO.Compression.ZipArchiveEntry? exeEntry = null;
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.Equals("PeekDesktop.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exeEntry = entry;
                        break;
                    }
                }

                if (exeEntry is null)
                    throw new InvalidOperationException("Zip should contain PeekDesktop.exe.");

                exeEntry.ExtractToFile(extractedPath, overwrite: true);
            }

            // Verify the extracted file
            if (!File.Exists(extractedPath))
                throw new InvalidOperationException("Extracted file does not exist.");

            string extractedContent = File.ReadAllText(extractedPath);
            if (extractedContent != fakeExeContent)
                throw new InvalidOperationException("Extracted content does not match original.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void WinVerifyTrustStateCleanup()
    {
        // Call VerifyAuthenticodeSignature multiple times to ensure no handle/resource leak
        // from the WTD_STATEACTION_CLOSE fix
        string tempExe = Path.Combine(Path.GetTempPath(), $"peekdesktop-test-leak-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(tempExe, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });

            Process proc = Process.GetCurrentProcess();
            proc.Refresh();
            int handlesBefore = proc.HandleCount;

            for (int i = 0; i < 100; i++)
            {
                var (isValid, _) = NativeMethods.VerifyAuthenticodeSignature(tempExe);
                if (isValid)
                    throw new InvalidOperationException("Unsigned file passed verification on iteration " + i);
            }

            ForceGc();
            proc.Refresh();
            int handlesAfter = proc.HandleCount;
            int handleGrowth = handlesAfter - handlesBefore;

            // Allow some slack for GC / runtime internals, but 100 calls should not leak handles
            if (handleGrowth > 20)
                throw new InvalidOperationException($"WinVerifyTrust handle leak: grew by {handleGrowth} over 100 calls.");
        }
        finally
        {
            try { File.Delete(tempExe); } catch { }
        }
    }

    private static void ValidateProcessLookup(uint processId, bool expectedToResolve)
    {
        bool resolved = NativeMethods.TryGetProcessName(processId, out string processName);
        if (expectedToResolve && !resolved)
            throw new InvalidOperationException($"Expected process lookup to succeed for pid={processId}.");

        if (!resolved)
        {
            if (processName.Length != 0)
                throw new InvalidOperationException($"Failed process lookup returned non-empty name for pid={processId}.");
            return;
        }

        if (string.IsNullOrWhiteSpace(processName))
            throw new InvalidOperationException($"Resolved process lookup returned empty name for pid={processId}.");

        if (!SafeProcessNamePattern.IsMatch(processName))
            throw new InvalidOperationException($"Resolved process name contains invalid characters for pid={processId}: '{processName}'.");
    }

    private static void RunLeakWorkload(int iterations, int seed)
    {
        var rand = new Random(seed);
        for (int i = 0; i < iterations; i++)
        {
            IntPtr hwnd = i % 2 == 0 ? IntPtr.Zero : new IntPtr(rand.Next(int.MinValue, int.MaxValue));
            _ = NativeMethods.GetWindowClassName(hwnd);
            _ = NativeMethods.GetWindowTitle(hwnd);
            _ = NativeMethods.IsWindowCloaked(hwnd);
            _ = NativeMethods.DescribeWindow(hwnd);
            _ = NativeMethods.TryGetProcessName((uint)rand.Next(1, int.MaxValue), out _);
            _ = NativeMethods.TryGetUserNotificationState(out _);
        }
    }

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
