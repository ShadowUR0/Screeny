using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenTimeTracker.Services;

/// <summary>
/// Keeps one Screeny process per interactive Windows session. A second launch
/// brings the existing dashboard forward instead of creating another tracker.
/// </summary>
internal sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\Screeny.SingleInstance";
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
    private readonly bool _ownsMutex;

    public bool IsPrimaryInstance => _ownsMutex;

    public SingleInstanceService()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _ownsMutex);
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

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }

        _mutex.Dispose();
    }
}
