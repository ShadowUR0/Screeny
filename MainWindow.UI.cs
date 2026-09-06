using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenTimeTracker.Helpers;
using ScreenTimeTracker.Models;
using ScreenTimeTracker.Services;

namespace ScreenTimeTracker
{
    public sealed partial class MainWindow
    {
        private static readonly string CurrentProcessName = Process.GetCurrentProcess().ProcessName;
        private bool _isLiveTotalRefreshInFlight;
        private bool _isWindowHidden = App.StartedFromWindowsStartup;
        private string _lastHeroTimeText = string.Empty;

        private void SetHeroTime(TimeSpan totalTime)
        {
            if (ChartTimeValue == null) return;

            string formatted = TimeUtil.FormatTimeSpan(totalTime);
            if (string.Equals(formatted, _lastHeroTimeText, StringComparison.Ordinal))
                return;

            _lastHeroTimeText = formatted;
            ChartTimeValue.Text = formatted;
        }

        private void UpdateUsageChart(AppUsageRecord? liveFocusedRecord = null)
        {
            if (UsageChartLive == null || _usageRecords == null) return;

            // The chart and the hero total serve different purposes. Do not let chart
            // interval/layout calculations overwrite the authoritative screen-time total.
            ChartHelper.UpdateUsageChart(
                UsageChartLive,
                _usageRecords,
                _viewModel.CurrentChartViewMode,
                _viewModel.CurrentTimePeriod,
                _viewModel.SelectedDate,
                _viewModel.SelectedEndDate,
                liveFocusedRecord);
        }

        private void CleanupSystemProcesses()
        {
            if (_usageRecords == null) return;

            var toRemove = _usageRecords
                .Where(record => ProcessFilter.ShouldIgnoreProcess(record.ProcessName))
                .ToList();

            foreach (var record in toRemove)
                _usageRecords.Remove(record);
        }

        private void UpdateAveragePanel(List<AppUsageRecord> aggregatedRecords, DateTime startDate, DateTime endDate)
        {
            if (AveragePanel == null || DailyAverage == null) return;

            int activeDayCount = _usageRecords
                .Where(record => record.Duration.TotalSeconds > 0)
                .Select(record => record.StartTime.Date)
                .Distinct()
                .Count();

            if (activeDayCount <= 0) activeDayCount = 1;

            double totalSeconds = aggregatedRecords.Sum(record => record.Duration.TotalSeconds);
            DailyAverage.Text = TimeUtil.FormatTimeSpan(TimeSpan.FromSeconds(totalSeconds / activeDayCount));
            AveragePanel.Visibility = Visibility.Visible;
        }

        private void UpdateViewModeAndChartForDateRange(DateTime startDate, DateTime endDate, List<AppUsageRecord> aggregatedRecords)
        {
            _viewModel.CurrentTimePeriod = TimePeriod.Weekly;
            _viewModel.CurrentChartViewMode = ChartViewMode.Daily;
            UpdateUsageChart();
            UpdateSummaryTab(aggregatedRecords);
        }

        private void ShowNoDataDialog(DateTime startDate, DateTime endDate)
        {
            if (Content == null) return;
            _ = new ContentDialog
            {
                Title = "No Data Available",
                Content = $"No usage data found for the selected date range ({startDate:MMM d} - {endDate:MMM d}).",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            }.ShowAsync();
        }

        private void ShowErrorDialog(string message)
        {
            if (Content == null) return;
            _ = new ContentDialog
            {
                Title = "Error",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            }.ShowAsync();
        }

        private Microsoft.UI.Windowing.AppWindow GetAppWindowForCurrentWindow()
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        }

        private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            _isWindowHidden = true;
            _updateTimer?.Stop();
            args.Cancel = true;
            sender.Hide();
        }

        private void MainWindow_ActivatedForUiRefresh(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
                return;

            bool wasHidden = _isWindowHidden;
            _isWindowHidden = false;

            if (wasHidden)
            {
                // UI mutations are skipped while Screeny is in the tray. Rebuild exactly once
                // from SQLite + the current live slice when the user actually asks to see it.
                if (_viewModel.IsDateRangeSelected && _viewModel.SelectedEndDate.HasValue)
                    LoadRecordsForDateRange(_viewModel.SelectedDate, _viewModel.SelectedEndDate.Value);
                else
                    LoadRecordsForDate(_viewModel.SelectedDate);
            }

            if (_trackingService.IsTracking)
            {
                _updateTimer.Start();
                _isChartDirty = true;
                DoLiveUpdates();
            }
        }

        private void Window_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args) => Dispose();

        private void UpdateChartViewMode()
        {
            var today = DateTime.Today;
            bool isSingleRecentDay = !_viewModel.IsDateRangeSelected &&
                                     (_viewModel.SelectedDate == today || _viewModel.SelectedDate == today.AddDays(-1));

            _viewModel.CurrentChartViewMode = isSingleRecentDay ? ChartViewMode.Hourly : ChartViewMode.Daily;

            if (ViewModeLabel != null)
                ViewModeLabel.Text = _viewModel.CurrentChartViewMode == ChartViewMode.Hourly ? "Hourly View" : "Daily View";

            if (ViewModePanel != null)
                ViewModePanel.Visibility = Visibility.Collapsed;

            UpdateUsageChart();
        }

        private void ForceChartRefresh()
        {
            if (UsageChartLive == null) return;

            ChartHelper.ForceChartRefresh(
                UsageChartLive,
                _usageRecords,
                _viewModel.CurrentChartViewMode,
                _viewModel.CurrentTimePeriod,
                _viewModel.SelectedDate,
                _viewModel.SelectedEndDate);
        }

        private void UpdateDatePickerButtonText()
        {
            if (DatePickerButton == null) return;

            try
            {
                var today = DateTime.Today;
                if (!_viewModel.IsDateRangeSelected)
                {
                    DatePickerButton.Content = _viewModel.SelectedDate == today
                        ? "Today"
                        : _viewModel.SelectedDate == today.AddDays(-1)
                            ? "Yesterday"
                            : _viewModel.SelectedDate.ToString("MMM dd");
                    return;
                }

                if (_viewModel.SelectedDate == today.AddDays(-6) && _viewModel.SelectedEndDate == today)
                {
                    DatePickerButton.Content = "Last 7 days";
                    return;
                }

                if (_viewModel.SelectedDate == today.AddDays(-29) && _viewModel.SelectedEndDate == today)
                {
                    DatePickerButton.Content = "Last 30 days";
                    return;
                }

                if (_viewModel.SelectedDate == new DateTime(today.Year, today.Month, 1) && _viewModel.SelectedEndDate == today)
                {
                    DatePickerButton.Content = "This month";
                    return;
                }

                if (_viewModel.SelectedEndDate.HasValue)
                    DatePickerButton.Content = $"{_viewModel.SelectedDate:MMM dd} - {_viewModel.SelectedEndDate:MMM dd}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in UpdateDatePickerButtonText: {ex.Message}");
                DatePickerButton.Content = _viewModel.SelectedDate.ToString("MMM dd");
            }
        }

        private void LoadRecordsForLastSevenDays()
        {
            try
            {
                DateTime today = DateTime.Today;
                DateTime startDate = today.AddDays(-6);
                var weekRecords = _databaseService?.GetAggregatedRecordsWithLive(startDate, today, _trackingService)
                                  ?? new List<AppUsageRecord>();

                UpdateRecordListView(weekRecords);
                SetTimeFrameHeader($"Last 7 Days ({startDate:MMM d} - {today:MMM d}, {today.Year})");
                UpdateChartWithRecords(weekRecords);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in LoadRecordsForLastSevenDays: {ex.Message}");
            }
        }

        private void UpdateRecordListView(List<AppUsageRecord> records)
        {
            if (_usageRecords == null) return;

            _usageRecords.Clear();
            foreach (var record in records.OrderByDescending(record => record.Duration))
                _usageRecords.Add(record);
        }

        private void UpdateChartWithRecords(List<AppUsageRecord> records)
        {
            _viewModel.CurrentTimePeriod = TimePeriod.Weekly;
            _viewModel.CurrentChartViewMode = ChartViewMode.Daily;

            if (ViewModeLabel != null) ViewModeLabel.Text = "Daily View";
            if (ViewModePanel != null) ViewModePanel.Visibility = Visibility.Collapsed;

            UpdateUsageChart();
            UpdateSummaryTab(records);
        }

        private void SetTimeFrameHeader(string headerText)
        {
            if (DateDisplay != null)
                DateDisplay.Text = headerText;
        }

        private void UpdateSummaryTab()
        {
            var (start, end) = GetCurrentViewDateRange();
            var aggregated = _databaseService?.GetAggregatedRecordsWithLive(start, end, _trackingService)
                             ?? new List<AppUsageRecord>();

            UpdateSummaryTab(aggregated);
        }

        private (DateTime Start, DateTime End) GetCurrentViewDateRange()
        {
            if (_viewModel.IsDateRangeSelected && _viewModel.SelectedEndDate.HasValue)
                return (_viewModel.SelectedDate.Date, _viewModel.SelectedEndDate.Value.Date);

            return (_viewModel.SelectedDate.Date, _viewModel.SelectedDate.Date);
        }

        private void UpdateSummaryTab(List<AppUsageRecord> recordsToSummarize)
        {
            TimeSpan totalTime = recordsToSummarize.Aggregate(TimeSpan.Zero, (sum, record) => sum + record.Duration);
            int maxDays = GetDayCountForTimePeriod(_viewModel.CurrentTimePeriod, _viewModel.SelectedDate);
            TimeSpan maxDuration = TimeSpan.FromHours(24 * maxDays);
            if (totalTime > maxDuration) totalTime = maxDuration;

            _cachedTotalTime = totalTime;
            _lastFullRefresh = DateTime.Now;
            SetHeroTime(totalTime);

            if (TotalScreenTime != null)
                TotalScreenTime.Text = TimeUtil.FormatTimeSpan(totalTime);
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => _windowHelper.MinimizeWindow();
        private void MaximizeButton_Click(object sender, RoutedEventArgs e) => _windowHelper.MaximizeOrRestoreWindow();

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _trackingService.StopTracking();
            _windowHelper.CloseWindow();
        }

        private void DatePickerButton_Click(object sender, RoutedEventArgs e)
        {
            _datePickerPopup?.ShowDatePicker(
                DatePickerButton,
                _viewModel.SelectedDate,
                _viewModel.SelectedEndDate,
                _viewModel.IsDateRangeSelected);
        }

        private void SetUpUiElements()
        {
            UpdateDatePickerButtonText();
            this.Activated += MainWindow_ActivatedForUiRefresh;
        }

        private void UpdateTimer_Tick(object? sender, object e)
        {
            try
            {
                if (_disposed || _usageRecords == null || _isReloading) return;
                if (_isWindowHidden)
                {
                    _updateTimer.Stop();
                    return;
                }

                _tickCount++;
                if (_tickCount == int.MaxValue) _tickCount = 0;

                if (_trackingService.IsTracking)
                    DoLiveUpdates();

                if (_tickCount % 3 == 0 && _isChartDirty)
                    DoChartRefresh();

                if (_tickCount % 300 == 0)
                {
                    DoIconRetry();
                    DoAutoSave();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in UpdateTimer_Tick: {ex.Message}");
            }
        }

        private TimeSpan _cachedTotalTime = TimeSpan.Zero;
        private DateTime _lastFullRefresh = DateTime.MinValue;

        private bool ViewIncludesToday()
        {
            if (!_viewModel.IsDateRangeSelected)
                return _viewModel.SelectedDate.Date == DateTime.Today;

            return _viewModel.SelectedEndDate.HasValue &&
                   _viewModel.SelectedDate.Date <= DateTime.Today &&
                   _viewModel.SelectedEndDate.Value.Date >= DateTime.Today;
        }

        private void DoLiveUpdates()
        {
            if (!ViewIncludesToday()) return;

            var activeRecord = _usageRecords.FirstOrDefault(record => record.IsFocused);
            bool shouldDoFullRefresh = !_isLiveTotalRefreshInFlight &&
                                       (DateTime.Now - _lastFullRefresh).TotalSeconds >= 30;

            if (shouldDoFullRefresh)
            {
                var (start, end) = GetCurrentViewDateRange();
                _isLiveTotalRefreshInFlight = true;

                _ = Task.Run(() =>
                {
                    try
                    {
                        var aggregated = _databaseService?.GetAggregatedRecordsWithLive(start, end, _trackingService)
                                         ?? new List<AppUsageRecord>();
                        var totalTime = aggregated.Aggregate(TimeSpan.Zero, (sum, record) => sum + record.Duration);

                        DispatcherQueue?.TryEnqueue(() =>
                        {
                            _isLiveTotalRefreshInFlight = false;
                            if (_disposed || _isWindowHidden) return;
                            _cachedTotalTime = totalTime;
                            _lastFullRefresh = DateTime.Now;
                            SetHeroTime(totalTime);
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error updating live total time: {ex.Message}");
                        DispatcherQueue?.TryEnqueue(() => _isLiveTotalRefreshInFlight = false);
                    }
                });
            }
            else if (activeRecord != null && _trackingService.IsTracking)
            {
                _cachedTotalTime = _cachedTotalTime.Add(TimeSpan.FromSeconds(1));
                SetHeroTime(_cachedTotalTime);
            }
        }

        private void DoChartRefresh()
        {
            _isChartDirty = false;
            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_disposed || _isWindowHidden) return;
                try
                {
                    UpdateUsageChart();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in chart refresh: {ex.Message}");
                }
            });
        }

        private void UsageListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue || args.Item is not AppUsageRecord record) return;
            record.LoadAppIconIfNeeded();
        }

        private void DoIconRetry()
        {
            try
            {
                foreach (var record in _usageRecords)
                {
                    if (record.AppIcon == null)
                        record.LoadAppIconIfNeeded();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading missing icons: {ex.Message}");
            }
        }

        private void TrackingService_UsageRecordUpdated(object? sender, AppUsageRecord record)
        {
            if (_disposed || _isWindowHidden) return;

            DispatcherQueue?.TryEnqueue(() =>
            {
                try
                {
                    if (_isWindowHidden || !ViewIncludesToday()) return;
                    if (record.ProcessName.Equals(CurrentProcessName, StringComparison.OrdinalIgnoreCase)) return;
                    if (ProcessFilter.ShouldIgnoreProcess(record.ProcessName)) return;

                    UpdateOrAddLiveRecord(record);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in UsageRecordUpdated handler: {ex.Message}");
                }
            });
        }

        private void TrackingService_WindowChanged(object? sender, EventArgs e)
        {
            if (_disposed || _isWindowHidden) return;
            DispatcherQueue?.TryEnqueue(() => _isChartDirty = true);
        }

        private void ClearOtherFocus(AppUsageRecord? keep = null)
        {
            foreach (var item in _usageRecords)
            {
                if (!ReferenceEquals(item, keep) && item.IsFocused)
                    item.SetFocus(false);
            }
        }

        private void UpdateOrAddLiveRecord(AppUsageRecord record)
        {
            try
            {
                ApplicationProcessingHelper.ProcessApplicationRecord(record);
                var existing = _usageRecords.FirstOrDefault(item =>
                    item.ProcessName.Equals(record.ProcessName, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    if (record.IsFocused)
                        ClearOtherFocus();

                    record.LoadAppIconIfNeeded();
                    _usageRecords.Add(record);
                    _isChartDirty = true;
                    return;
                }

                if (!ReferenceEquals(existing, record) && record.Duration > existing.Duration)
                    existing._accumulatedDuration = record.Duration;

                if (record.IsFocused && !existing.IsFocused)
                {
                    ClearOtherFocus(existing);
                    existing.SetFocus(true);
                }
                else if (!record.IsFocused && existing.IsFocused)
                {
                    existing.SetFocus(false);
                }

                existing.RaiseDurationChanged();
                _isChartDirty = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in UpdateOrAddLiveRecord: {ex.Message}");
            }
        }

        private void RefreshLiveRecords()
        {
            try
            {
                var live = _databaseService?.GetDetailRecordsWithLive(DateTime.Today, _trackingService)
                           ?? new List<AppUsageRecord>();

                _usageRecords.Clear();
                foreach (var record in live.OrderByDescending(record => record.Duration))
                    _usageRecords.Add(record);

                _isChartDirty = true;
                UpdateSummaryTab(_usageRecords.ToList());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in RefreshLiveRecords: {ex.Message}");
            }
        }

        private void TrackingService_UsageSliceFinalized(object? sender, UsageSlice slice)
        {
            try
            {
                if (_databaseService == null || slice == null) return;

                var result = _databaseService.SaveSliceWithResult(slice);
                _viewModel.PersistenceHealth = result switch
                {
                    PersistenceResult.Saved => PersistenceHealthStatus.Healthy,
                    PersistenceResult.DuplicateIgnored => PersistenceHealthStatus.Healthy,
                    PersistenceResult.RetryableFailure => PersistenceHealthStatus.RetryableIssue,
                    _ => PersistenceHealthStatus.FatalIssue
                };
            }
            catch (Exception ex)
            {
                _viewModel.PersistenceHealth = PersistenceHealthStatus.FatalIssue;
                Debug.WriteLine($"Error saving usage slice from service: {ex.Message}");
            }
        }
    }
}
