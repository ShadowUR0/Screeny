// Modified in the ShadowUR0 Screeny fork in 2026.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ScreenTimeTracker.Models;
using ScreenTimeTracker.Services;
using SQLitePCL;

namespace ScreenTimeTracker;

public partial class App : Application
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_OK = 0x00000000;

    private readonly SingleInstanceService _singleInstanceService;
    private DispatcherQueue? _dispatcherQueue;
    private Window? _window;
    private WindowTrackingService? _trackingService;

    public static Window? MainWindowInstance { get; private set; }
    public static bool StartedFromWindowsStartup { get; private set; }

    public App()
    {
        bool shutdownForUpdate = HasCommandLineSwitch("--shutdown-for-update");

        // Acquire the process-wide guard before initializing SQLite or tracking.
        _singleInstanceService = new SingleInstanceService();

        if (shutdownForUpdate)
        {
            // The installer starts Screeny with this switch before replacing files.
            // If no instance is running there is nothing to stop. Otherwise signal the
            // primary process and wait until its mutex is released.
            if (_singleInstanceService.IsPrimaryInstance)
            {
                Environment.Exit(0);
                return;
            }

            bool stopped = _singleInstanceService.RequestUpdateShutdownAndWaitForExit(TimeSpan.FromSeconds(8));
            Environment.Exit(stopped ? 0 : 2);
            return;
        }

        if (!_singleInstanceService.IsPrimaryInstance)
        {
            _singleInstanceService.ActivateExistingInstance();
            Environment.Exit(0);
            return;
        }

        WriteDebugLog("Application starting...");
        StartedFromWindowsStartup = IsStartedFromWindowsStartup();
        WriteDebugLog($"Started from Windows startup: {StartedFromWindowsStartup}");

        try
        {
            SetProcessDPIAware();

            try
            {
                Batteries_V2.Init();
            }
            catch (Exception ex)
            {
                // DatabaseService will report a degraded state if SQLite is genuinely unavailable.
                WriteDebugLog($"SQLite initialization error: {ex}");
            }

            UnhandledException += App_UnhandledException;
            InitializeComponent();
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            DispatcherHelper.Initialize(_dispatcherQueue);
            StartUpdateShutdownListener();
        }
        catch (Exception ex)
        {
            ShowErrorAndExit("The application failed to initialize properly.", ex);
        }
    }

    private static bool HasCommandLineSwitch(string value)
    {
        foreach (string arg in Environment.GetCommandLineArgs())
        {
            if (arg.Equals(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void StartUpdateShutdownListener()
    {
        _ = Task.Run(() =>
        {
            try
            {
                _singleInstanceService.WaitForUpdateShutdownRequest();
                _dispatcherQueue?.TryEnqueue(() =>
                {
                    if (MainWindowInstance is MainWindow mainWindow)
                        mainWindow.ShutdownForUpdate();
                    else
                        Environment.Exit(0);
                });
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    private static bool IsStartedFromWindowsStartup()
    {
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            foreach (string arg in args)
            {
                if (arg.Contains("--startup", StringComparison.OrdinalIgnoreCase) ||
                    arg.Contains("/startup", StringComparison.OrdinalIgnoreCase) ||
                    arg.Contains("-startup", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            string startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string currentPath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (!string.IsNullOrEmpty(currentPath) &&
                currentPath.StartsWith(startupPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Keep the existing fallback for startup registrations that do not pass an argument.
            TimeSpan uptime = TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount));
            return uptime.TotalSeconds < 20;
        }
        catch (Exception ex)
        {
            WriteDebugLog($"Error detecting startup mode: {ex.Message}");
            return false;
        }
    }

    private static bool IsFirstRun()
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Screeny");
            string markerPath = Path.Combine(folder, ".firstrun");

            if (File.Exists(markerPath))
                return false;

            Directory.CreateDirectory(folder);
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
            return true;
        }
        catch (Exception ex)
        {
            WriteDebugLog($"Error checking first run: {ex.Message}");
            return false;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            _window = new MainWindow();
            MainWindowInstance = _window;

            if (_window is MainWindow mainWindow)
            {
                _trackingService = mainWindow.GetTrackingService();
                mainWindow.EnsureStartupInitialized();
            }

            bool isFirstRun = IsFirstRun();
            bool showWindow = StartupLaunchPolicy.ShouldShowWindow(StartedFromWindowsStartup, isFirstRun);

            if (showWindow)
            {
                _window.Activate();
            }
            else
            {
                WriteDebugLog("Startup launch initialized in background without activating the window.");
            }
        }
        catch (Exception ex)
        {
            ShowErrorAndExit("The application failed to start properly.", ex);
        }
    }

    private static void BuildExceptionDetails(Exception ex, StringBuilder details, int level = 0)
    {
        string indent = new(' ', level * 2);
        details.AppendLine($"{indent}Exception: {ex.GetType().FullName}");
        details.AppendLine($"{indent}Message: {ex.Message}");
        details.AppendLine($"{indent}Source: {ex.Source}");

        try
        {
            foreach (var property in ex.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.Name is "InnerException" or "StackTrace" or "Message" or "Source")
                    continue;

                try
                {
                    object? value = property.GetValue(ex);
                    if (value != null)
                        details.AppendLine($"{indent}{property.Name}: {value}");
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        details.AppendLine($"{indent}Stack trace:");
        details.AppendLine($"{indent}{ex.StackTrace}");

        if (ex.InnerException != null)
        {
            details.AppendLine($"{indent}Inner exception:");
            BuildExceptionDetails(ex.InnerException, details, level + 1);
        }
    }

    private static void ShowErrorAndExit(string message, Exception ex, bool exit = true)
    {
        LogExceptionToFile(ex);
        string detailedMessage = $"{message}\n\nError: {ex.Message}\n\nSee the Screeny error log for details.";
        MessageBox(IntPtr.Zero, detailedMessage, "Critical Application Error", MB_OK | MB_ICONERROR);

        if (exit)
            Environment.Exit(1);
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogExceptionToFile(e.Exception);
        e.Handled = true;
        ShowErrorDialog("A critical error occurred. Please check the Screeny error log.");
    }

    private static string GetLogFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenTimeTracker");
    }

    private static void LogExceptionToFile(Exception ex)
    {
        try
        {
            string logFolder = GetLogFolder();
            Directory.CreateDirectory(logFolder);
            string logPath = Path.Combine(logFolder, "Screeny_ErrorLog.txt");

            var details = new StringBuilder();
            details.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR");
            BuildExceptionDetails(ex, details);
            details.AppendLine();
            File.AppendAllText(logPath, details.ToString());
        }
        catch (Exception logEx)
        {
            // Never recursively invoke the error logger if the filesystem itself is failing.
            Debug.WriteLine($"Failed to write Screeny error log: {logEx}");
            Debug.WriteLine($"Original exception: {ex}");
        }
    }

    private static void ShowErrorDialog(string message)
    {
        MessageBox(IntPtr.Zero, message, "Application Error", MB_OK | MB_ICONERROR);
    }

    // Verbose lifecycle logging used to synchronously append to disk dozens of times on
    // every startup. Keep diagnostics in debug builds without taxing normal launches or
    // recording machine/path metadata in production.
    [Conditional("DEBUG")]
    private static void WriteDebugLog(string message)
    {
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogExceptionToFile(e.Exception);
        e.SetObserved();
    }
}
