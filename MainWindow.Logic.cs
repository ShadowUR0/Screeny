using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using System.Threading.Tasks;
using ScreenTimeTracker.Models;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using ScreenTimeTracker.Services;
using ScreenTimeTracker.Helpers;

namespace ScreenTimeTracker
{
    public sealed partial class MainWindow
    {
        private void RegisterPowerNotifications()
        {
            if (_hWnd == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("Cannot register power notifications: HWND is zero.");
                return;
            }

            try
            {
                Guid consoleGuid = GuidConsoleDisplayState;
                _hConsoleDisplayState = RegisterPowerSettingNotification(_hWnd, ref consoleGuid, DEVICE_NOTIFY_WINDOW_HANDLE);
                if (_hConsoleDisplayState == IntPtr.Zero)
                    System.Diagnostics.Debug.WriteLine($"Failed to register for GuidConsoleDisplayState. Error: {Marshal.GetLastWin32Error()}");

                Guid awayGuid = GuidSystemAwayMode;
                _hSystemAwayMode = RegisterPowerSettingNotification(_hWnd, ref awayGuid, DEVICE_NOTIFY_WINDOW_HANDLE);
                if (_hSystemAwayMode == IntPtr.Zero)
                    System.Diagnostics.Debug.WriteLine($"Failed to register for GuidSystemAwayMode. Error: {Marshal.GetLastWin32Error()}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error registering power notifications: {ex.Message}");
            }
        }

        private void UnregisterPowerNotifications()
        {
            try
            {
                if (_hConsoleDisplayState != IntPtr.Zero)
                {
                    UnregisterPowerSettingNotification(_hConsoleDisplayState);
                    _hConsoleDisplayState = IntPtr.Zero;
                }

                if (_hSystemAwayMode != IntPtr.Zero)
                {
                    UnregisterPowerSettingNotification(_hSystemAwayMode);
                    _hSystemAwayMode = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error unregistering power notifications: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _trayIconHelper?.Dispose();
            UnregisterPowerNotifications();
            RestoreWindowProc();

            if (_appWindow != null)
                _appWindow.Closing -= AppWindow_Closing;

            _trackingService?.StopTracking();
            _updateTimer?.Stop();
            _usageRecords?.Clear();

            _trackingService?.Dispose();
            _databaseService?.Dispose();

            if (_updateTimer != null) _updateTimer.Tick -= UpdateTimer_Tick;
            if (_trackingService != null)
            {
                _trackingService.WindowChanged -= TrackingService_WindowChanged;
                _trackingService.UsageRecordUpdated -= TrackingService_UsageRecordUpdated;
                _trackingService.UsageSliceFinalized -= TrackingService_UsageSliceFinalized;
            }

            if (Content is FrameworkElement root) root.Loaded -= MainWindow_Loaded;
            if (_trayIconHelper != null)
            {
                _trayIconHelper.ShowClicked -= TrayIcon_ShowClicked;
                _trayIconHelper.ExitClicked -= TrayIcon_ExitClicked;
            }

            _disposed = true;
        }

        public void PrepareForSuspend()
        {
            try
            {
                if (_viewModel.SelectedDate > DateTime.Today)
                    _viewModel.SelectedDate = DateTime.Today;

                if (_trackingService != null && _trackingService.IsTracking)
                    _trackingService.StopTracking();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ERROR in PrepareForSuspend: {ex.Message}");
            }
        }

        private void DoAutoSave()
        {
            try
            {
                CleanupSystemProcesses();

                // Finalized slices are persisted immediately by TrackingService_UsageSliceFinalized.
                // The old five-minute path rebuilt the entire list from SQLite even though there
                // was nothing left to "save", causing avoidable DB, icon and layout work.
                _isChartDirty = true;
                UpdateSummaryTab(_usageRecords.ToList());

                // Full integrity/maintenance work is intentionally infrequent. With a five-minute
                // caller cadence, 72 cycles is roughly six hours instead of every hour.
                _autoSaveCycleCount++;
                if (_autoSaveCycleCount >= 72 && _databaseService != null)
                {
                    _autoSaveCycleCount = 0;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            _databaseService.PerformDatabaseMaintenance();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error during database maintenance: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in periodic maintenance: {ex}");
            }
        }

        private void StartTracking()
        {
            ThrowIfDisposed();
            try
            {
                _trackingService?.StartTracking();
                _updateTimer.Start();
                _isChartDirty = true;

                UpdateUsageChart();
                UpdateSummaryTab(_usageRecords.ToList());
                _viewModel.IsTracking = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error starting tracking: {ex.Message}");
                if (!_disposed && Content?.XamlRoot != null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Error",
                        Content = $"Failed to start tracking: {ex.Message}",
                        CloseButtonText = "OK",
                        XamlRoot = Content.XamlRoot
                    };
                    _ = dialog.ShowAsync();
                }
            }
        }

        private void StopTracking()
        {
            ThrowIfDisposed();
            _trackingService?.StopTracking();
            _viewModel.IsTracking = false;

            foreach (var record in _usageRecords)
                record.SetFocus(false);

            _updateTimer.Stop();
            CleanupSystemProcesses();
            UpdateSummaryTab(_usageRecords.ToList());
            UpdateUsageChart();
        }

        private void LoadRecordsForDate(DateTime date)
        {
            if (date > DateTime.Today) date = DateTime.Today;

            _viewModel.SelectedDate = date;
            _viewModel.SelectedEndDate = null;
            _viewModel.IsDateRangeSelected = false;
            _usageRecords.Clear();

            if (DateDisplay != null)
            {
                var today = DateTime.Today;
                var yesterday = today.AddDays(-1);
                if (date == today) DateDisplay.Text = "Today";
                else if (date == yesterday) DateDisplay.Text = "Yesterday";
                else DateDisplay.Text = date.ToString("MMMM d");
            }

            try
            {
                List<AppUsageRecord> records;
                switch (_viewModel.CurrentTimePeriod)
                {
                    case TimePeriod.Weekly:
                    {
                        var startOfWeek = date.AddDays(-(int)date.DayOfWeek);
                        var endOfWeek = startOfWeek.AddDays(6);
                        records = BuildRecords(() => _databaseService!.GetRawRecordsForDateRange(startOfWeek, endOfWeek));

                        var aggregatedWeekly = _databaseService?.GetAggregatedRecordsWithLive(
                            startOfWeek, endOfWeek, _trackingService, includeLiveRecords: false) ?? new List<AppUsageRecord>();
                        _viewModel.AggregatedRecords.Clear();
                        foreach (var record in aggregatedWeekly) _viewModel.AggregatedRecords.Add(record);

                        if (DateDisplay != null) DateDisplay.Text = $"{startOfWeek:MMM d} - {endOfWeek:MMM d}";
                        SummaryTitle.Text = "Weekly Screen Time Summary";
                        AveragePanel.Visibility = Visibility.Visible;
                        break;
                    }
                    default:
                        records = BuildRecords(() => _databaseService!.GetDetailRecordsWithLive(date, _trackingService));
                        _viewModel.AggregatedRecords.Clear();
                        foreach (var record in records) _viewModel.AggregatedRecords.Add(record);
                        SummaryTitle.Text = "Daily Screen Time Summary";
                        AveragePanel.Visibility = Visibility.Collapsed;
                        break;
                }

                if (records.Count == 0 && date.Date != DateTime.Today)
                {
                    DispatcherQueue?.TryEnqueue(async () =>
                    {
                        if (Content == null) return;

                        var dlg = new ContentDialog
                        {
                            Title = "No Data Available",
                            Content = $"No usage data found for {DateDisplay?.Text ?? "the selected date"}.",
                            CloseButtonText = "OK",
                            XamlRoot = Content.XamlRoot
                        };

                        _viewModel.SelectedDate = DateTime.Today;
                        _viewModel.SelectedEndDate = null;
                        _viewModel.IsDateRangeSelected = false;
                        UpdateDatePickerButtonText();
                        LoadRecordsForDate(DateTime.Today);
                        await dlg.ShowAsync();
                    });
                }

                foreach (var record in records.OrderByDescending(record => record.Duration))
                {
                    record.LoadAppIconIfNeeded();
                    _usageRecords.Add(record);
                }

                CleanupSystemProcesses();
                _isReloading = false;
                UpdateSummaryTab(_usageRecords.ToList());
                UpdateChartViewMode();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading records: {ex.Message}");
                DispatcherQueue?.TryEnqueue(async () =>
                {
                    if (Content == null) return;
                    var dlg = new ContentDialog
                    {
                        Title = "Error Loading Data",
                        Content = $"Failed to load screen time data: {ex.Message}",
                        CloseButtonText = "OK",
                        XamlRoot = Content.XamlRoot
                    };
                    await dlg.ShowAsync();
                });
            }
        }

        private void LoadRecordsForDateRange(DateTime startDate, DateTime endDate)
        {
            if (startDate > endDate)
                (startDate, endDate) = (endDate, startDate);

            _viewModel.SelectedDate = startDate;
            _viewModel.SelectedEndDate = endDate;
            _viewModel.IsDateRangeSelected = true;
            _usageRecords.Clear();

            try
            {
                var records = BuildRecords(() => _databaseService!.GetRawRecordsForDateRange(startDate, endDate));
                foreach (var record in records.OrderByDescending(record => record.Duration))
                {
                    record.LoadAppIconIfNeeded();
                    _usageRecords.Add(record);
                }

                var aggregated = _databaseService?.GetAggregatedRecordsWithLive(
                    startDate, endDate, _trackingService, includeLiveRecords: false) ?? new List<AppUsageRecord>();

                CleanupSystemProcesses();
                UpdateAveragePanel(aggregated, startDate, endDate);
                UpdateViewModeAndChartForDateRange(startDate, endDate, aggregated);
                _isReloading = false;

                _viewModel.AggregatedRecords.Clear();
                foreach (var record in aggregated) _viewModel.AggregatedRecords.Add(record);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading records for date range: {ex.Message}");
                ShowErrorDialog($"Failed to load screen time data: {ex.Message}");
            }
        }

        private int GetDayCountForTimePeriod(TimePeriod period, DateTime date)
        {
            return period == TimePeriod.Weekly ? 7 : 1;
        }

        private TimeSpan CalculateTotalActiveTime(List<AppUsageRecord> records)
        {
            var intervals = records
                .Select(record => new { Start = record.StartTime, End = record.StartTime + record.Duration })
                .Where(interval => interval.End > interval.Start)
                .OrderBy(interval => interval.Start)
                .ToList();

            var merged = new List<(DateTime Start, DateTime End)>();
            foreach (var interval in intervals)
            {
                if (merged.Count == 0 || interval.Start > merged[^1].End)
                {
                    merged.Add((interval.Start, interval.End));
                    continue;
                }

                var last = merged[^1];
                merged[^1] = (last.Start, interval.End > last.End ? interval.End : last.End);
            }

            TimeSpan total = TimeSpan.Zero;
            foreach (var span in merged) total += span.End - span.Start;
            return total;
        }

        private List<AppUsageRecord> LoadRecordsForSpecificDay(DateTime date, bool updateUI = true)
        {
            if (_databaseService == null)
                return new List<AppUsageRecord>();

            try
            {
                var records = BuildRecords(() => _databaseService.GetDetailRecordsWithLive(date, _trackingService));
                if (!updateUI) return records;

                _viewModel.SelectedDate = date;
                _viewModel.SelectedEndDate = null;
                _viewModel.IsDateRangeSelected = false;
                _usageRecords.Clear();
                _isReloading = true;

                foreach (var record in records.OrderByDescending(record => record.Duration))
                {
                    record.LoadAppIconIfNeeded();
                    _usageRecords.Add(record);
                }

                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (_disposed) return;
                    // Keep the existing ItemsSource/binding intact so ListView recycling and
                    // virtualization are not reset for every data refresh.
                    UpdateUsageChart();
                    UpdateSummaryTab(_usageRecords.ToList());
                    _isReloading = false;
                });

                return records;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in LoadRecordsForSpecificDay: {ex.Message}");
                return new List<AppUsageRecord>();
            }
        }

        private static List<AppUsageRecord> BuildRecords(Func<List<AppUsageRecord>> query)
        {
            // The previous Task.Run(...).Result still blocked the UI caller while adding a
            // thread-pool hop and synchronization. Execute once, directly, until the data
            // loading API is made genuinely asynchronous end-to-end.
            var list = query();
            foreach (var record in list)
                ApplicationProcessingHelper.ProcessApplicationRecord(record);
            return list;
        }
    }
}
