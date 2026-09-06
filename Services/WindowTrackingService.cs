using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using ScreenTimeTracker.Helpers;
using ScreenTimeTracker.Models;
using Windows.Media.Control;

namespace ScreenTimeTracker.Services
{
    public class WindowTrackingService : IDisposable
    {
        private const int IdleThresholdSeconds = 300;
        private static readonly TimeSpan MediaStateCacheDuration = TimeSpan.FromSeconds(5);

        private readonly System.Timers.Timer _timer;
        private readonly object _lockObject = new object();
        private AppUsageRecord? _currentRecord;
        private AppUsageRecord? _idleRecord;
        private bool _disposed;
        private bool _isIdle;
        private DateTime _lastMediaStateCheckUtc = DateTime.MinValue;
        private bool _lastMediaPlaying;
        private GlobalSystemMediaTransportControlsSessionManager? _mediaSessionManager;

#if !UNIT_TEST
        private IntPtr _focusHook = IntPtr.Zero;
        private WinEventDelegate? _winEventDelegate;

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
#endif

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        public event EventHandler<AppUsageRecord>? UsageRecordUpdated;
        public event EventHandler? WindowChanged;
        public event EventHandler<UsageSlice>? UsageSliceFinalized;

        public bool IsTracking { get; private set; }
        public AppUsageRecord? CurrentRecord => _currentRecord;

        public WindowTrackingService()
        {
            // Foreground changes are event-driven. One tick per second is enough for
            // live duration display and idle detection, and halves periodic wake-ups.
            _timer = new System.Timers.Timer(1000);
            _timer.Elapsed += Timer_Elapsed;
            _timer.AutoReset = true;

            Debug.WriteLine("WindowTrackingService initialized.");

#if !UNIT_TEST
            _winEventDelegate = OnWinEvent;
            _focusHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);
#endif
        }

        public void StartTracking()
        {
            lock (_lockObject)
            {
                ThrowIfDisposed();
                if (IsTracking) return;

                IsTracking = true;
                _timer.Start();
            }

            CheckActiveWindow();
        }

        public void StopTracking()
        {
            lock (_lockObject)
            {
                ThrowIfDisposed();
                if (!IsTracking) return;

                _timer.Stop();
                IsTracking = false;
                FinalizeOpenRecords(DateTime.Now);
            }
        }

        public void PauseTrackingForSuspend()
        {
            lock (_lockObject)
            {
                ThrowIfDisposed();
                if (!IsTracking) return;

                _timer.Stop();
                IsTracking = false;
                FinalizeOpenRecords(DateTime.Now);
            }
        }

        public void ResumeTrackingAfterSuspend()
        {
            lock (_lockObject)
            {
                ThrowIfDisposed();
                if (IsTracking) return;

                IsTracking = true;
                _timer.Start();
            }

            CheckActiveWindow();
        }

        private static int GetIdleSeconds()
        {
            var li = new LASTINPUTINFO
            {
                cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
            };

            if (!GetLastInputInfo(ref li))
            {
                return 0;
            }

            // LASTINPUTINFO uses the same wrapping 32-bit tick counter as
            // Environment.TickCount. Unsigned subtraction handles rollover safely.
            uint elapsedMilliseconds = unchecked((uint)Environment.TickCount - li.dwTime);
            return (int)(elapsedMilliseconds / 1000U);
        }

        private bool IsAnyMediaPlaying(DateTime utcNow)
        {
            if (utcNow - _lastMediaStateCheckUtc < MediaStateCacheDuration)
            {
                return _lastMediaPlaying;
            }

            bool mediaPlaying = false;
            try
            {
                _mediaSessionManager ??=
                    GlobalSystemMediaTransportControlsSessionManager.RequestAsync().GetAwaiter().GetResult();

                foreach (var session in _mediaSessionManager.GetSessions())
                {
                    var info = session.GetPlaybackInfo();
                    if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        mediaPlaying = true;
                        break;
                    }
                }
            }
            catch
            {
                // Re-acquire on a later check if the media-session service was reset.
                _mediaSessionManager = null;
            }

            _lastMediaPlaying = mediaPlaying;
            _lastMediaStateCheckUtc = utcNow;
            return mediaPlaying;
        }

        private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            try
            {
                DateTime now = DateTime.Now;
                int idleSeconds = GetIdleSeconds();

                // The media-session API is relatively expensive. There is no reason to
                // touch it while the user is active, so query it only after the idle
                // threshold is actually reached and then cache the result briefly.
                bool mediaPlaying = idleSeconds > IdleThresholdSeconds && IsAnyMediaPlaying(DateTime.UtcNow);
                bool currentlyIdle = idleSeconds > IdleThresholdSeconds && !mediaPlaying;
                bool shouldRefreshActiveWindow;

                lock (_lockObject)
                {
                    if (!IsTracking || _disposed) return;
                    shouldRefreshActiveWindow = ApplyIdleState(currentlyIdle, now);
                }

                if (currentlyIdle)
                {
                    return;
                }

                if (shouldRefreshActiveWindow)
                {
                    CheckActiveWindow();
                }

                lock (_lockObject)
                {
                    UpdateFocusedRecord(now);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR in Timer_Elapsed: {ex.Message}");
            }
        }

        private void CheckActiveWindow()
        {
            lock (_lockObject)
            {
                if (!IsTracking || _disposed) return;
            }

            try
            {
                var foregroundWindow = GetForegroundWindow();
                if (foregroundWindow == IntPtr.Zero || !IsWindow(foregroundWindow)) return;

                GetWindowThreadProcessId(foregroundWindow, out uint processId);
                if (processId == 0) return;

                string windowTitle = GetActiveWindowTitle(foregroundWindow);
                string processName = GetProcessName((int)processId);
                ProcessWindowChange(foregroundWindow, (int)processId, processName, windowTitle);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR in CheckActiveWindow: {ex.Message}");
            }
        }

        private static string GetActiveWindowTitle(IntPtr handle)
        {
            try
            {
                if (handle == IntPtr.Zero || !IsWindow(handle))
                {
                    return string.Empty;
                }

                const int nChars = 256;
                var buffer = new StringBuilder(nChars);
                return GetWindowText(handle, buffer, nChars) > 0 ? buffer.ToString() : string.Empty;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR in GetActiveWindowTitle: {ex.Message}");
                return string.Empty;
            }
        }

        private static string GetProcessName(int processId)
        {
            if (processId <= 0) return "Unknown";

            try
            {
                using var process = Process.GetProcessById(processId);
                return process.ProcessName;
            }
            catch (ArgumentException ex)
            {
                Debug.WriteLine($"Process has exited: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"Cannot access process: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR in GetProcessName: {ex.Message}");
            }

            return "Unknown";
        }

        public IEnumerable<AppUsageRecord> GetRecords()
        {
            lock (_lockObject)
            {
                var live = new List<AppUsageRecord>(2);
                if (_currentRecord != null) live.Add(_currentRecord.CreateSnapshot());
                if (_idleRecord != null) live.Add(_idleRecord.CreateSnapshot());
                return live;
            }
        }

        private void FinalizeRecord(AppUsageRecord record, DateTime endTime)
        {
            record.SetFocus(false);
            record.EndTime = endTime;

            if (UsageSlice.TryCreate(
                    record.ProcessName,
                    record.ApplicationName,
                    record.WindowTitle,
                    record.StartTime,
                    endTime,
                    out var slice) &&
                slice != null)
            {
                UsageSliceFinalized?.Invoke(this, slice);
            }
        }

        private void FinalizeOpenRecords(DateTime endTime)
        {
            if (_currentRecord != null)
            {
                FinalizeRecord(_currentRecord, endTime);
                _currentRecord = null;
            }

            if (_idleRecord != null)
            {
                FinalizeRecord(_idleRecord, endTime);
                _idleRecord = null;
            }

            _isIdle = false;
        }

        private void UpdateFocusedRecord(DateTime now)
        {
            if (_currentRecord == null || !_currentRecord.IsFocused)
            {
                return;
            }

            _currentRecord.RaiseDurationChanged();
            UsageRecordUpdated?.Invoke(this, _currentRecord);

            if (_currentRecord.Date < now.Date)
            {
                var endOfPreviousDay = _currentRecord.Date.AddDays(1).AddSeconds(-1);
                FinalizeRecord(_currentRecord, endOfPreviousDay);
                _currentRecord = null;
            }
        }

        private bool ApplyIdleState(bool currentlyIdle, DateTime now)
        {
            if (currentlyIdle && !_isIdle)
            {
                _isIdle = true;

                if (_currentRecord != null)
                {
                    FinalizeRecord(_currentRecord, now);
                    UsageRecordUpdated?.Invoke(this, _currentRecord);
                    _currentRecord = null;
                }

                if (_idleRecord == null)
                {
                    _idleRecord = new AppUsageRecord
                    {
                        ProcessName = "Idle / Away",
                        ApplicationName = "Idle / Away",
                        StartTime = now,
                        Date = EnsureValidDate(now.Date)
                    };
                    UsageRecordUpdated?.Invoke(this, _idleRecord);
                }

                return false;
            }

            if (!currentlyIdle && _isIdle)
            {
                _isIdle = false;

                if (_idleRecord != null)
                {
                    FinalizeRecord(_idleRecord, now);
                    UsageRecordUpdated?.Invoke(this, _idleRecord);
                    _idleRecord = null;
                }

                return true;
            }

            return false;
        }

#if UNIT_TEST
        internal void SetOpenRecordsForTest(AppUsageRecord? currentRecord, AppUsageRecord? idleRecord, bool isTracking = true)
        {
            lock (_lockObject)
            {
                _currentRecord = currentRecord;
                _idleRecord = idleRecord;
                _isIdle = idleRecord != null;
                IsTracking = isTracking;
            }
        }

        internal void UpdateFocusedRecordForTest(DateTime now)
        {
            lock (_lockObject)
            {
                UpdateFocusedRecord(now);
            }
        }

        internal void ProcessWindowChangeForTest(IntPtr foregroundWindow, int processId, string processName, string windowTitle)
        {
            ProcessWindowChange(foregroundWindow, processId, processName, windowTitle);
        }

        internal bool ApplyIdleStateForTest(bool currentlyIdle, DateTime now)
        {
            lock (_lockObject)
            {
                return ApplyIdleState(currentlyIdle, now);
            }
        }
#endif

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(WindowTrackingService));
            }
        }

        public void Dispose()
        {
            lock (_lockObject)
            {
                if (_disposed) return;

                try
                {
                    _timer.Stop();
                    _timer.Elapsed -= Timer_Elapsed;
                    _timer.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error disposing timer: {ex.Message}");
                }

                IsTracking = false;
                _currentRecord = null;
                _idleRecord = null;
                _mediaSessionManager = null;
                _disposed = true;

#if !UNIT_TEST
                if (_focusHook != IntPtr.Zero)
                {
                    try
                    {
                        UnhookWinEvent(_focusHook);
                    }
                    catch
                    {
                    }

                    _focusHook = IntPtr.Zero;
                    _winEventDelegate = null;
                }
#endif
            }
        }

        private static DateTime EnsureValidDate(DateTime date)
        {
            if (date > DateTime.Today)
            {
                Debug.WriteLine($"WARNING: Future date detected ({date:yyyy-MM-dd}), using current date instead.");
                return DateTime.Today;
            }

            return date;
        }

#if !UNIT_TEST
        private void OnWinEvent(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint thread,
            uint time)
        {
            if (eventType == EVENT_SYSTEM_FOREGROUND && hwnd != IntPtr.Zero)
            {
                Task.Run(() => CheckActiveWindow(hwnd));
            }
        }

        private void CheckActiveWindow(IntPtr foregroundWindow)
        {
            if (foregroundWindow == IntPtr.Zero) return;

            lock (_lockObject)
            {
                if (!IsTracking || _disposed) return;
            }

            try
            {
                if (!IsWindow(foregroundWindow)) return;

                GetWindowThreadProcessId(foregroundWindow, out uint processId);
                if (processId == 0) return;

                string windowTitle = GetActiveWindowTitle(foregroundWindow);
                string processName = GetProcessName((int)processId);
                ProcessWindowChange(foregroundWindow, (int)processId, processName, windowTitle);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in window tracking callback: {ex.Message}");
            }
        }
#endif

        private void ProcessWindowChange(IntPtr foregroundWindow, int processId, string processName, string windowTitle)
        {
            lock (_lockObject)
            {
                if (_currentRecord != null &&
                    _currentRecord.WindowHandle == foregroundWindow &&
                    _currentRecord.ProcessId == processId &&
                    _currentRecord.WindowTitle == windowTitle)
                {
                    if (!_currentRecord.IsFocused)
                    {
                        _currentRecord.SetFocus(true);
                        UsageRecordUpdated?.Invoke(this, _currentRecord);
                    }

                    return;
                }

                DateTime now = DateTime.Now;

                if (_currentRecord != null)
                {
                    FinalizeRecord(_currentRecord, now);
                    UsageRecordUpdated?.Invoke(this, _currentRecord);
                }

                _currentRecord = new AppUsageRecord
                {
                    ProcessName = processName,
                    WindowTitle = windowTitle,
                    StartTime = now,
                    ProcessId = processId,
                    WindowHandle = foregroundWindow,
                    Date = EnsureValidDate(now.Date),
                    ApplicationName = processName
                };

                ApplicationProcessingHelper.ProcessApplicationRecord(_currentRecord);
                _currentRecord.SetFocus(true);
                UsageRecordUpdated?.Invoke(this, _currentRecord);
                WindowChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
