using System.Runtime.InteropServices;
using FileNodeClient.Ipc;

namespace FileNodeClient.Windows;

/// <summary>
/// Registers COM class factories so Explorer can activate our handlers
/// (e.g. IStorageProviderUriSource) in-process via the running Service.exe.
/// </summary>
public static class ComServerHost
{
    private static uint _cookie;
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

        var clsid = new Guid(UriSourceHandler.Clsid);
        var factory = new UriSourceClassFactory();

        var hr = CoRegisterClassObject(
            ref clsid,
            factory,
            CLSCTX_LOCAL_SERVER,
            REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED,
            out _cookie);

        if (hr != 0)
        {
            Log.Error($"CoRegisterClassObject failed: 0x{hr:X8}");
            return;
        }

        hr = CoResumeClassObjects();
        if (hr != 0)
        {
            Log.Error($"CoResumeClassObjects failed: 0x{hr:X8}");
            return;
        }

        _registered = true;
        Log.Info("COM server registered for UriSourceHandler");
    }

    public static void Revoke()
    {
        if (!_registered) return;
        CoRevokeClassObject(_cookie);
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
