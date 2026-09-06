// Modified in the ShadowUR0 Screeny fork in 2026.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenTimeTracker.Services;

/// <summary>
/// Keeps one Screeny process per interactive Windows session. A second launch
/// brings the existing dashboard forward instead of creating another tracker.
/// Also exposes a lightweight cross-process signal used by the installer before
/// replacing application files during an upgrade.
/// </summary>
internal sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\Screeny.SingleInstance";
    private const string UpdateShutdownEventName = @"Local\Screeny.UpdateShutdown";
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _updateShutdownEvent;
    private readonly bool _ownsMutex;

    public bool IsPrimaryInstance => _ownsMutex;

    public SingleInstanceService()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _ownsMutex);
        _updateShutdownEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            UpdateShutdownEventName);
    }

    public void ActivateExistingInstance()
    {
        int currentPid = Environment.ProcessId;
        string processName = Process.GetCurrentProcess().ProcessName;

        // The first instance may still be creating its WinUI window. Give it a short
        // grace period so a rapid double-click still resolves to one visible window.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (process.Id == currentPid) continue;

                    IntPtr hWnd = process.MainWindowHandle;
                    if (hWnd == IntPtr.Zero) continue;

                    ShowWindowAsync(hWnd, IsIconic(hWnd) ? SW_RESTORE : SW_SHOW);
                    SetForegroundWindow(hWnd);
                    return;
                }
                catch
                {
                    // The candidate can exit while we inspect it; try another one.
                }
                finally
                {
                    process.Dispose();
                }
            }

            Thread.Sleep(100);
        }
    }

    public void WaitForUpdateShutdownRequest()
    {
        _updateShutdownEvent.WaitOne();
    }

    public bool RequestUpdateShutdownAndWaitForExit(TimeSpan timeout)
    {
        _updateShutdownEvent.Set();

        try
        {
            if (!_mutex.WaitOne(timeout))
                return false;

            // The primary process released its mutex, so the update can replace files.
            _mutex.ReleaseMutex();
            return true;
        }
        catch (AbandonedMutexException)
        {
            // A terminated primary also means its executable is no longer locked.
            return true;
        }
    }

    public void Dispose()
    {
        _updateShutdownEvent.Dispose();

        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }

        _mutex.Dispose();
    }
}
