using System;
#if !UNIT_TEST
using Microsoft.UI.Xaml.Media.Imaging;
#endif
using ScreenTimeTracker.Helpers;

namespace ScreenTimeTracker.Models
{
    public class AppUsageRecord : ScreenyObservableObject
    {
        public int Id { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public string WindowTitle { get; set; } = string.Empty;
        public IntPtr WindowHandle { get; set; }
        public bool IsFocused { get; set; }
        public string ApplicationName { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public DateTime? LastUpdated { get; set; }

#if !UNIT_TEST
        private BitmapImage? _appIcon;
        public BitmapImage? AppIcon
        {
            get => _appIcon;
            private set
            {
                if (_appIcon != value)
                {
                    _appIcon = value;
                    NotifyPropertyChanged();
                }
            }
        }
#endif

        private DateTime _startTime;
        public DateTime StartTime
        {
            get => _startTime;
            set
            {
                if (_startTime == value) return;
                _startTime = value;
                NotifyPropertyChanged();
                NotifyDurationChanged(forceFormatted: true);
            }
        }

        private DateTime? _endTime;
        public DateTime? EndTime
        {
            get => _endTime;
            set
            {
                if (_endTime == value) return;
                _endTime = value;
                NotifyPropertyChanged();
                NotifyDurationChanged(forceFormatted: true);
            }
        }

        internal TimeSpan _accumulatedDuration = TimeSpan.Zero;
        private DateTime _lastFocusTime;
        private bool _durationIsSnapshot;
        private long _lastDisplayedMinute = long.MinValue;

        public TimeSpan Duration
        {
            get
            {
                var duration = _accumulatedDuration;
                if (IsFocused && !_durationIsSnapshot)
                {
                    var liveDelta = DateTime.Now - _lastFocusTime;
                    if (liveDelta > TimeSpan.Zero)
                        duration += liveDelta;
                }

                return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
            }
        }

        // The activity list is a wellbeing summary, not a stopwatch. Showing seconds made
        // every active row repaint continuously while adding no useful information.
        public string FormattedDuration
        {
            get
            {
                var duration = Duration;
                if (duration.TotalMinutes < 1)
                    return "<1m";

                return TimeFormatter.FormatTimeSpan(duration);
            }
        }

        public string FormattedStartTime => StartTime.ToString("HH:mm");

        public bool IsFromDate(DateTime date) => StartTime.Date == date.Date;

        public void SetFocus(bool isFocused)
        {
            _durationIsSnapshot = false;
            if (IsFocused == isFocused) return;

            if (isFocused)
            {
                _lastFocusTime = DateTime.Now;
            }
            else
            {
                var focusedDuration = DateTime.Now - _lastFocusTime;
                if (focusedDuration > TimeSpan.Zero)
                    _accumulatedDuration += focusedDuration;
            }

            IsFocused = isFocused;
            NotifyPropertyChanged(nameof(IsFocused));
            NotifyDurationChanged(forceFormatted: true);
        }

        internal void SetIdleAnchor(DateTime timestamp)
        {
            _lastFocusTime = timestamp;
            NotifyDurationChanged(forceFormatted: true);
        }

        internal AppUsageRecord CreateSnapshot()
        {
            return new AppUsageRecord
            {
                Id = Id,
                ProcessName = ProcessName,
                ProcessId = ProcessId,
                WindowTitle = WindowTitle,
                WindowHandle = WindowHandle,
                IsFocused = IsFocused,
                ApplicationName = ApplicationName,
                ExecutablePath = ExecutablePath,
                Date = Date,
                LastUpdated = LastUpdated,
                StartTime = StartTime,
                EndTime = EndTime,
                _accumulatedDuration = Duration,
                _lastFocusTime = DateTime.Now,
                _durationIsSnapshot = true
            };
        }

        public void MergeWith(AppUsageRecord other)
        {
            if (!other.EndTime.HasValue) return;

            var otherDuration = other.EndTime.Value - other.StartTime;
            if (otherDuration > TimeSpan.Zero)
                _accumulatedDuration += otherDuration;

            if (!EndTime.HasValue)
            {
                StartTime = other.EndTime.Value;
                _lastFocusTime = StartTime;
            }

            NotifyDurationChanged(forceFormatted: true);
        }

        public static AppUsageRecord CreateAggregated(string processName, DateTime date)
        {
            // Aggregates feed charts and summaries. Icon I/O here caused every background
            // aggregation to fan out into unnecessary process/disk lookups.
            return new AppUsageRecord
            {
                ProcessName = processName,
                ApplicationName = processName,
                Date = date,
                StartTime = new DateTime(date.Year, date.Month, date.Day, 12, 0, 0),
                _accumulatedDuration = TimeSpan.Zero
            };
        }

#if !UNIT_TEST
        public async void LoadAppIconIfNeeded()
        {
            if (AppIcon != null || _loadingIcon) return;

            _loadingIcon = true;
            try
            {
                var icon = await ScreenTimeTracker.Services.IconLoader.Instance.GetIconAsync(this);
                if (icon != null)
                {
                    if (!DispatcherHelper.EnqueueOnUIThread(() => AppIcon = icon))
                        AppIcon = icon;
                }
            }
            catch
            {
                // Icon failures are cosmetic; IconLoader also caches failures to prevent hot retries.
            }
            finally
            {
                _loadingIcon = false;
            }
        }

        private bool _loadingIcon;

        public void ClearIcon() => AppIcon = null;
#else
        public void LoadAppIconIfNeeded()
        {
        }

        public void ClearIcon()
        {
        }
#endif

        public void RaiseDurationChanged()
        {
            NotifyDurationChanged(forceFormatted: false);
        }

        private void NotifyDurationChanged(bool forceFormatted)
        {
            NotifyPropertyChanged(nameof(Duration));

            long displayedMinute = Math.Max(0L, (long)Math.Floor(Duration.TotalMinutes));
            if (!forceFormatted && displayedMinute == _lastDisplayedMinute)
                return;

            _lastDisplayedMinute = displayedMinute;
            NotifyPropertyChanged(nameof(FormattedDuration));
        }
    }
}
