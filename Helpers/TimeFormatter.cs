using System;

namespace ScreenTimeTracker.Helpers
{
    public static class TimeFormatter
    {
        public static string FormatTimeSpan(TimeSpan time)
        {
            const int MaxReasonableDays = 365;
            if (time.TotalDays > MaxReasonableDays)
            {
                time = TimeSpan.FromDays(MaxReasonableDays);
            }

            if (time < TimeSpan.Zero)
            {
                time = TimeSpan.Zero;
            }

            int days = (int)time.TotalDays;
            int hours = time.Hours;
            int minutes = time.Minutes;

            if (days > 0)
                return $"{days}d {hours}h {minutes}m";
            if (time.TotalHours >= 1)
                return $"{(int)time.TotalHours}h {minutes}m";
            if (time.TotalMinutes >= 1)
                return $"{(int)time.TotalMinutes}m";

            return $"{Math.Max(0, time.Seconds)}s";
        }
    }
}
