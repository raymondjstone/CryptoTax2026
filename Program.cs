using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace CryptoTax2026;

public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        // When running as an MSI (unpackaged) the Windows App Runtime COM servers are not
        // registered by an MSIX package. We must bootstrap the runtime before Application.Start()
        // by P/Invoking directly into Microsoft.WindowsAppRuntime.Bootstrap.dll — a plain native
        // DLL that requires no prior COM or WinRT initialization.
        // Packaged (MSIX) apps skip this — the package identity handles registration.
        if (!IsPackaged())
            BootstrapRuntime();

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static void BootstrapRuntime()
    {
        // MddBootstrapInitialize loads the Windows App Runtime DLLs and registers COM servers.
        // 0x00010008 = Windows App SDK major 1, minor 8.  minVersion 0 = no minimum constraint.
        var hr = MddBootstrapInitialize(0x00010008, null, 0UL);
        Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>
    /// Returns true when the process is running inside an MSIX package identity.
    /// GetCurrentPackageFullName returns ERROR_APPMODEL_NO_PACKAGE (15700) for unpackaged processes.
    /// </summary>
    private static bool IsPackaged()
    {
        int length = 0;
        return GetCurrentPackageFullName(ref length, null) != 15700;
    }

    // Bootstraps the Windows App Runtime into the current process. Ships as a plain native DLL
    // alongside the exe — safe to call before any WinRT/COM activation.
    [DllImport("Microsoft.WindowsAppRuntime.Bootstrap.dll", ExactSpelling = true)]
    private static extern int MddBootstrapInitialize(
        uint majorMinorVersion,
        [MarshalAs(UnmanagedType.LPWStr)] string? versionTag,
        ulong minVersion);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref int packageFullNameLength, char[]? packageFullName);
}
