using System.Runtime.InteropServices;
using FileNodeClient.Ipc;

namespace FileNodeClient.Windows;

/// <summary>
/// Registers COM class factories so Explorer can activate our handlers
/// (e.g. IStorageProviderUriSource) in-process via the running Service.exe.
/// </summary>
public static class ComServerHost
{
    private static uint _uriSourceCookie;
    private static uint _thumbnailCookie;
    private static bool _registered;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        [In] ref Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext,
        uint flags,
        out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);

    private const uint CLSCTX_LOCAL_SERVER = 4;
    private const uint REGCLS_MULTIPLEUSE = 1;
    private const uint REGCLS_SUSPENDED = 4;

    [DllImport("ole32.dll")]
    private static extern int CoResumeClassObjects();

    public static void Register()
    {
        if (_registered) return;

        // Register UriSourceHandler
        var uriClsid = new Guid(UriSourceHandler.Clsid);
        var uriFactory = new UriSourceClassFactory();
        var hr = CoRegisterClassObject(
            ref uriClsid,
            uriFactory,
            CLSCTX_LOCAL_SERVER,
            REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED,
            out _uriSourceCookie);
        if (hr != 0)
        {
            Log.Error($"CoRegisterClassObject (UriSource) failed: 0x{hr:X8}");
            return;
        }

        // Register ThumbnailHandler
        var thumbClsid = new Guid(ThumbnailHandler.Clsid);
        var thumbFactory = new ThumbnailClassFactory();
        hr = CoRegisterClassObject(
            ref thumbClsid,
            thumbFactory,
            CLSCTX_LOCAL_SERVER,
            REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED,
            out _thumbnailCookie);
        if (hr != 0)
        {
            Log.Error($"CoRegisterClassObject (Thumbnail) failed: 0x{hr:X8}");
            return;
        }

        hr = CoResumeClassObjects();
        if (hr != 0)
        {
            Log.Error($"CoResumeClassObjects failed: 0x{hr:X8}");
            return;
        }

        _registered = true;
        Log.Info("COM server registered for UriSourceHandler and ThumbnailHandler");
    }

    public static void Revoke()
    {
        if (!_registered) return;
        CoRevokeClassObject(_uriSourceCookie);
        CoRevokeClassObject(_thumbnailCookie);
        _registered = false;
        Log.Info("COM server revoked");
    }

    [ComVisible(true)]
    [Guid("A7B3E2D1-9F4C-4A8B-B5E6-1D3C7F8A2E9C")]
    private sealed class UriSourceClassFactory : IClassFactory
    {
        public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
        {
            ppvObject = IntPtr.Zero;
            if (pUnkOuter != IntPtr.Zero)
                return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION

            var handler = new UriSourceHandler();
            ppvObject = Marshal.GetComInterfaceForObject(handler, typeof(global::Windows.Storage.Provider.IStorageProviderUriSource));
            return 0; // S_OK
        }

        public int LockServer(bool fLock) => 0; // S_OK
    }

    [ComVisible(true)]
    [Guid("B8C4F3E2-1A5D-4B9C-C6F7-2E4D8A9B3F1D")]
    private sealed class ThumbnailClassFactory : IClassFactory
    {
        public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
        {
            ppvObject = IntPtr.Zero;
            if (pUnkOuter != IntPtr.Zero)
                return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION

            var handler = new ThumbnailHandler();
            ppvObject = Marshal.GetIUnknownForObject(handler);
            var iid = riid;
            var hr = Marshal.QueryInterface(ppvObject, ref iid, out var ppvResult);
            Marshal.Release(ppvObject);
            ppvObject = ppvResult;
            return hr;
        }

        public int LockServer(bool fLock) => 0; // S_OK
    }

    [ComVisible(true)]
    [Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);
        [PreserveSig]
        int LockServer(bool fLock);
    }
}
