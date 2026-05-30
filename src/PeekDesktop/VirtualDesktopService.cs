using System;
using System.Runtime.InteropServices;

namespace PeekDesktop;

internal sealed class VirtualDesktopService : IDisposable
{
    private const int AdjacentDesktopLeft = 3;
    private const int AdjacentDesktopRight = 4;

    private DesktopManagerAdapter? _managerInternal;
    private Guid? _peekDesktopId;
    private bool _peekDesktopCreated;
    private bool _disposed;

    public bool TryEnterPeekDesktop(out Guid originalDesktopId)
    {
        originalDesktopId = Guid.Empty;

        if (!EnsureInitialized())
            return false;

        IVirtualDesktop? currentDesktop = null;
        IVirtualDesktop? peekDesktop = null;

        try
        {
            currentDesktop = _managerInternal!.GetCurrentDesktop();
            originalDesktopId = currentDesktop.GetId();

            peekDesktop = GetOrCreatePeekDesktop(originalDesktopId);
            if (peekDesktop == null)
                return false;

            Guid peekDesktopId = peekDesktop.GetId();
            if (peekDesktopId == originalDesktopId)
            {
                AppDiagnostics.Log("Virtual desktop peek desktop unexpectedly matched the current desktop");
                return false;
            }

            if (!SwitchToDesktop(peekDesktop))
                return false;

            AppDiagnostics.Log($"Switched to virtual peek desktop {peekDesktopId}");
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Virtual desktop enter failed: {ex.Message}");
            return false;
        }
        finally
        {
            ReleaseComObject(peekDesktop);
            ReleaseComObject(currentDesktop);
        }
    }

    public bool TryRestoreDesktop(Guid originalDesktopId)
    {
        if (originalDesktopId == Guid.Empty)
        {
            AppDiagnostics.Log("Virtual desktop restore skipped because no original desktop was recorded");
            return false;
        }

        if (!EnsureInitialized())
            return false;

        IVirtualDesktop? originalDesktop = null;
        try
        {
            originalDesktop = _managerInternal!.FindDesktop(ref originalDesktopId);
            if (originalDesktop == null)
            {
                AppDiagnostics.Log($"Original virtual desktop {originalDesktopId} was not found");
                return false;
            }

            bool switched = SwitchToDesktop(originalDesktop);
            if (switched)
                AppDiagnostics.Log($"Restored original virtual desktop {originalDesktopId}");

            return switched;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Virtual desktop restore failed: {ex.Message}");
            return false;
        }
        finally
        {
            ReleaseComObject(originalDesktop);
        }
    }

    private bool EnsureInitialized()
    {
        if (_managerInternal != null)
            return true;

        IServiceProvider10? shell = null;
        try
        {
            int buildNumber = Environment.OSVersion.Version.Build;
            Type? shellType = Type.GetTypeFromCLSID(VirtualDesktopComGuids.CLSID_ImmersiveShell);
            if (shellType == null)
            {
                AppDiagnostics.Log("Virtual desktop shell COM class was not found");
                return false;
            }

            shell = (IServiceProvider10)Activator.CreateInstance(shellType)!;
            Guid serviceGuid = VirtualDesktopComGuids.CLSID_VirtualDesktopManagerInternal;
            Guid interfaceGuid = VirtualDesktopComGuids.IID_VirtualDesktopManagerInternal;
            object managerRaw = shell.QueryService(ref serviceGuid, ref interfaceGuid);

            _managerInternal = DesktopManagerAdapter.Create(managerRaw, buildNumber);
            AppDiagnostics.Log($"Virtual desktop shell initialized for Windows build {buildNumber}");
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Virtual desktop shell initialization failed: {ex.Message}");
            _managerInternal?.Dispose();
            _managerInternal = null;
            return false;
        }
        finally
        {
            ReleaseComObject(shell);
        }
    }

    private IVirtualDesktop? GetOrCreatePeekDesktop(Guid currentDesktopId)
    {
        if (_peekDesktopId.HasValue && _peekDesktopId.Value != currentDesktopId)
        {
            Guid existingId = _peekDesktopId.Value;
            try
            {
                IVirtualDesktop existingDesktop = _managerInternal!.FindDesktop(ref existingId);
                AppDiagnostics.Log($"Reusing virtual peek desktop {existingId}");
                return existingDesktop;
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log($"Stored virtual peek desktop could not be reused: {ex.Message}");
                _peekDesktopId = null;
                _peekDesktopCreated = false;
            }
        }

        try
        {
            IVirtualDesktop desktop = _managerInternal!.CreateDesktop();
            Guid desktopId = desktop.GetId();
            _peekDesktopId = desktopId;
            _peekDesktopCreated = true;
            TrySetDesktopName(desktop, Lang.VirtualDesktop_Name);
            AppDiagnostics.Log($"Created virtual peek desktop {desktopId}");
            return desktop;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Creating virtual peek desktop failed: {ex.Message}");
            return null;
        }
    }

    private bool SwitchToDesktop(IVirtualDesktop desktop)
    {
        try
        {
            IntPtr taskbarWindow = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbarWindow != IntPtr.Zero)
            {
                IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
                uint taskbarThreadId = NativeMethods.GetWindowThreadProcessId(taskbarWindow, out _);
                uint foregroundThreadId = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
                uint currentThreadId = NativeMethods.GetCurrentThreadId();

                if (taskbarThreadId != 0 && foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                {
                    NativeMethods.AttachThreadInput(taskbarThreadId, currentThreadId, true);
                    NativeMethods.AttachThreadInput(foregroundThreadId, currentThreadId, true);
                    NativeMethods.SetForegroundWindow(taskbarWindow);
                    NativeMethods.AttachThreadInput(foregroundThreadId, currentThreadId, false);
                    NativeMethods.AttachThreadInput(taskbarThreadId, currentThreadId, false);
                }
            }

            _managerInternal!.SwitchDesktopWithAnimation(desktop);
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Virtual desktop switch failed: {ex.Message}");
            return false;
        }
    }

    private void TrySetDesktopName(IVirtualDesktop desktop, string name)
    {
        IntPtr hstring = IntPtr.Zero;
        try
        {
            if (NativeMethods.WindowsCreateString(name, name.Length, out hstring) != 0)
            {
                AppDiagnostics.Log("Virtual desktop name allocation failed");
                return;
            }

            _managerInternal!.SetDesktopName(desktop, hstring);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Setting virtual desktop name failed: {ex.Message}");
        }
        finally
        {
            if (hstring != IntPtr.Zero)
                NativeMethods.WindowsDeleteString(hstring);
        }
    }

    private void RemoveCreatedPeekDesktop()
    {
        if (!_peekDesktopCreated || !_peekDesktopId.HasValue || !EnsureInitialized())
            return;

        Guid peekDesktopId = _peekDesktopId.Value;
        IVirtualDesktop? peekDesktop = null;
        IVirtualDesktop? currentDesktop = null;
        IVirtualDesktop? fallbackDesktop = null;

        try
        {
            peekDesktop = _managerInternal!.FindDesktop(ref peekDesktopId);
            if (peekDesktop == null)
                return;

            currentDesktop = _managerInternal.GetCurrentDesktop();
            fallbackDesktop = currentDesktop;

            if (currentDesktop.GetId() == peekDesktopId)
            {
                int hr = _managerInternal.GetAdjacentDesktop(peekDesktop, AdjacentDesktopLeft, out fallbackDesktop!);
                if (hr != 0)
                    hr = _managerInternal.GetAdjacentDesktop(peekDesktop, AdjacentDesktopRight, out fallbackDesktop!);

                if (hr != 0 || fallbackDesktop == null)
                {
                    AppDiagnostics.Log("Could not find a fallback desktop to remove the temporary peek desktop");
                    return;
                }
            }

            _managerInternal.RemoveDesktop(peekDesktop, fallbackDesktop);
            AppDiagnostics.Log($"Removed temporary virtual peek desktop {peekDesktopId}");
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Removing temporary virtual peek desktop failed: {ex.Message}");
        }
        finally
        {
            if (!ReferenceEquals(fallbackDesktop, currentDesktop))
                ReleaseComObject(fallbackDesktop);

            ReleaseComObject(currentDesktop);
            ReleaseComObject(peekDesktop);
        }
    }

    private static void ReleaseComObject<T>(T? comObject) where T : class
    {
        if (comObject != null && Marshal.IsComObject(comObject))
            Marshal.ReleaseComObject(comObject);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        RemoveCreatedPeekDesktop();
        _managerInternal?.Dispose();
        _managerInternal = null;
        _disposed = true;
    }
}

internal abstract class DesktopManagerAdapter : IDisposable
{
    public abstract int GetCount();
    public abstract IVirtualDesktop GetCurrentDesktop();
    public abstract IVirtualDesktop CreateDesktop();
    public abstract int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
    public abstract void SwitchDesktopWithAnimation(IVirtualDesktop desktop);
    public abstract void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    public abstract IVirtualDesktop FindDesktop(ref Guid desktopId);
    public abstract void SetDesktopName(IVirtualDesktop desktop, IntPtr nameHString);
    public abstract void Dispose();

    public static DesktopManagerAdapter Create(object comObject, int buildNumber)
    {
        DesktopManagerAdapter? primary = buildNumber >= 26100
            ? TryCreate24H2(comObject)
            : TryCreatePre24H2(comObject);

        if (primary != null && primary.SmokeTest())
            return primary;

        primary?.Dispose();

        DesktopManagerAdapter? fallback = buildNumber >= 26100
            ? TryCreatePre24H2(comObject)
            : TryCreate24H2(comObject);

        if (fallback != null && fallback.SmokeTest())
            return fallback;

        fallback?.Dispose();
        throw new InvalidOperationException("No compatible virtual desktop COM adapter was found.");
    }

    private static DesktopManagerAdapter? TryCreate24H2(object comObject)
    {
        try
        {
            return new Adapter24H2((IVirtualDesktopManagerInternal24H2)comObject);
        }
        catch
        {
            return null;
        }
    }

    private static DesktopManagerAdapter? TryCreatePre24H2(object comObject)
    {
        try
        {
            return new AdapterPre24H2((IVirtualDesktopManagerInternalPre24H2)comObject);
        }
        catch
        {
            return null;
        }
    }

    private bool SmokeTest()
    {
        IVirtualDesktop? current = null;
        IVirtualDesktop? found = null;
        try
        {
            int count = GetCount();
            if (count < 1 || count > 200)
                return false;

            current = GetCurrentDesktop();
            Guid id = current.GetId();
            found = FindDesktop(ref id);
            return found != null;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (found != null && !ReferenceEquals(found, current) && Marshal.IsComObject(found))
                Marshal.ReleaseComObject(found);

            if (current != null && Marshal.IsComObject(current))
                Marshal.ReleaseComObject(current);
        }
    }

    private sealed class Adapter24H2 : DesktopManagerAdapter
    {
        private IVirtualDesktopManagerInternal24H2? _com;

        public Adapter24H2(IVirtualDesktopManagerInternal24H2 com)
        {
            _com = com;
        }

        public override int GetCount() => _com!.GetCount();
        public override IVirtualDesktop GetCurrentDesktop() => _com!.GetCurrentDesktop();
        public override IVirtualDesktop CreateDesktop() => _com!.CreateDesktop();
        public override int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop) => _com!.GetAdjacentDesktop(from, direction, out desktop);
        public override void SwitchDesktopWithAnimation(IVirtualDesktop desktop) => _com!.SwitchDesktopWithAnimation(desktop);
        public override void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback) => _com!.RemoveDesktop(desktop, fallback);
        public override IVirtualDesktop FindDesktop(ref Guid desktopId) => _com!.FindDesktop(ref desktopId);
        public override void SetDesktopName(IVirtualDesktop desktop, IntPtr nameHString) => _com!.SetDesktopName(desktop, nameHString);

        public override void Dispose()
        {
            if (_com != null && Marshal.IsComObject(_com))
                Marshal.ReleaseComObject(_com);

            _com = null;
        }
    }

    private sealed class AdapterPre24H2 : DesktopManagerAdapter
    {
        private IVirtualDesktopManagerInternalPre24H2? _com;

        public AdapterPre24H2(IVirtualDesktopManagerInternalPre24H2 com)
        {
            _com = com;
        }

        public override int GetCount() => _com!.GetCount();
        public override IVirtualDesktop GetCurrentDesktop() => _com!.GetCurrentDesktop();
        public override IVirtualDesktop CreateDesktop() => _com!.CreateDesktop();
        public override int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop) => _com!.GetAdjacentDesktop(from, direction, out desktop);
        public override void SwitchDesktopWithAnimation(IVirtualDesktop desktop) => _com!.SwitchDesktopWithAnimation(desktop);
        public override void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback) => _com!.RemoveDesktop(desktop, fallback);
        public override IVirtualDesktop FindDesktop(ref Guid desktopId) => _com!.FindDesktop(ref desktopId);
        public override void SetDesktopName(IVirtualDesktop desktop, IntPtr nameHString) => _com!.SetDesktopName(desktop, nameHString);

        public override void Dispose()
        {
            if (_com != null && Marshal.IsComObject(_com))
                Marshal.ReleaseComObject(_com);

            _com = null;
        }
    }
}

internal static class VirtualDesktopComGuids
{
    public static readonly Guid CLSID_ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    public static readonly Guid CLSID_VirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    public static readonly Guid IID_VirtualDesktopManagerInternal = new("53F5CA0B-158F-4124-900C-057158060B27");
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal interface IServiceProvider10
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object QueryService(ref Guid service, ref Guid riid);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal interface IVirtualDesktop
{
    bool IsViewVisible(object view);
    Guid GetId();
    IntPtr GetName();
    IntPtr GetWallpaperPath();
    bool IsRemote();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal24H2
{
    int GetCount();
    void MoveViewToDesktop(object view, IVirtualDesktop desktop);
    bool CanViewMoveDesktops(object view);
    IVirtualDesktop GetCurrentDesktop();
    void GetDesktops(out object desktops);
    [PreserveSig]
    int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
    void SwitchDesktop(IVirtualDesktop desktop);
    void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);
    IVirtualDesktop CreateDesktop();
    void MoveDesktop(IVirtualDesktop desktop, int nIndex);
    void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    IVirtualDesktop FindDesktop(ref Guid desktopId);
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop, out object includeViews, out object excludeViews);
    void SetDesktopName(IVirtualDesktop desktop, IntPtr nameHString);
    void SetDesktopWallpaper(IVirtualDesktop desktop, IntPtr pathHString);
    void UpdateWallpaperPathForAllDesktops(IntPtr pathHString);
    void CopyDesktopState(object view0, object view1);
    void CreateRemoteDesktop(IntPtr pathHString, out IVirtualDesktop desktop);
    void SwitchRemoteDesktop(IVirtualDesktop desktop, IntPtr switchType);
    void SwitchDesktopWithAnimation(IVirtualDesktop desktop);
    void GetLastActiveDesktop(out IVirtualDesktop desktop);
    void WaitForAnimationToComplete();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternalPre24H2
{
    int GetCount();
    void MoveViewToDesktop(object view, IVirtualDesktop desktop);
    bool CanViewMoveDesktops(object view);
    IVirtualDesktop GetCurrentDesktop();
    void GetDesktops(out object desktops);
    [PreserveSig]
    int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
    void SwitchDesktop(IVirtualDesktop desktop);
    IVirtualDesktop CreateDesktop();
    void MoveDesktop(IVirtualDesktop desktop, int nIndex);
    void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    IVirtualDesktop FindDesktop(ref Guid desktopId);
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop, out object includeViews, out object excludeViews);
    void SetDesktopName(IVirtualDesktop desktop, IntPtr nameHString);
    void SetDesktopWallpaper(IVirtualDesktop desktop, IntPtr pathHString);
    void UpdateWallpaperPathForAllDesktops(IntPtr pathHString);
    void CopyDesktopState(object view0, object view1);
    void CreateRemoteDesktop(IntPtr pathHString, out IVirtualDesktop desktop);
    void SwitchRemoteDesktop(IVirtualDesktop desktop, IntPtr switchType);
    void SwitchDesktopWithAnimation(IVirtualDesktop desktop);
    void GetLastActiveDesktop(out IVirtualDesktop desktop);
    void WaitForAnimationToComplete();
}
