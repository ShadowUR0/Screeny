using System;
using System.Collections.Generic;
using System.Linq;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using ScreenTimeTracker.Models;
using SkiaSharp;

namespace ScreenTimeTracker.Helpers
{
    public static class ChartHelper
    {
        public static TimeSpan UpdateUsageChart(
            LiveChartsCore.SkiaSharpView.WinUI.CartesianChart chart,
            ICollection<AppUsageRecord> usageRecords,
            ChartViewMode viewMode,
            TimePeriod timePeriod,
            DateTime selectedDate,
            DateTime? selectedEndDate = null,
            AppUsageRecord? liveFocusedRecord = null)
        {
            if (chart == null)
                return TimeSpan.Zero;

            TimeSpan totalTime = TimeUtil.CalculateUniqueTotalTime(usageRecords);
            SKColor seriesColor = GetSeriesColor();
            SKColor axisColor = Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? SKColors.White
                : SKColors.Black;

            if (viewMode == ChartViewMode.Hourly)
            {
                UpdateHourlyChart(chart, usageRecords, selectedDate, selectedEndDate, seriesColor, axisColor);
            }
            else
            {
                UpdateDailyChart(chart, usageRecords, timePeriod, selectedDate, selectedEndDate, seriesColor, axisColor);
            }

            chart.LegendPosition = LiveChartsCore.Measure.LegendPosition.Hidden;
            chart.AnimationsSpeed = TimeSpan.Zero;
            return totalTime;
        }

        private static SKColor GetSeriesColor()
        {
            try
            {
                if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object accentColorObject) &&
                    accentColorObject is Windows.UI.Color accentColor)
                {
                    return new SKColor(accentColor.R, accentColor.G, accentColor.B);
                }
            }
            catch
            {
            }

            return SKColors.DodgerBlue;
        }

        private static DateTime ResolveRecordEnd(AppUsageRecord record)
        {
            if (record.EndTime.HasValue)
                return record.EndTime.Value;

            if (record.IsFocused)
                return DateTime.Now;

            return record.StartTime + record.Duration;
        }

        private static void UpdateHourlyChart(
            LiveChartsCore.SkiaSharpView.WinUI.CartesianChart chart,
            ICollection<AppUsageRecord> usageRecords,
            DateTime selectedDate,
            DateTime? selectedEndDate,
            SKColor seriesColor,
            SKColor axisColor)
        {
            var intervalsByHour = new List<(DateTime Start, DateTime End)>[24];
            for (int hour = 0; hour < intervalsByHour.Length; hour++)
                intervalsByHour[hour] = new List<(DateTime Start, DateTime End)>();

            DateTime dayStart = selectedDate.Date;
            DateTime dayEnd = dayStart.AddDays(1);

            foreach (var record in usageRecords)
            {
                DateTime start = record.StartTime < dayStart ? dayStart : record.StartTime;
                DateTime resolvedEnd = ResolveRecordEnd(record);
                DateTime end = resolvedEnd > dayEnd ? dayEnd : resolvedEnd;
                if (end <= start)
                    continue;

                int firstHour = start.Hour;
                int lastHour = end >= dayEnd ? 23 : end.AddTicks(-1).Hour;

                for (int hour = firstHour; hour <= lastHour; hour++)
                {
                    DateTime slotStart = dayStart.AddHours(hour);
                    DateTime slotEnd = slotStart.AddHours(1);
                    DateTime overlapStart = start > slotStart ? start : slotStart;
                    DateTime overlapEnd = end < slotEnd ? end : slotEnd;
                    if (overlapEnd > overlapStart)
                        intervalsByHour[hour].Add((overlapStart, overlapEnd));
                }
            }

            var hourlyUsage = new double[24];
            var usedHours = new List<int>(24);
            for (int hour = 0; hour < 24; hour++)
            {
                var merged = TimeUtil.MergeIntervals(intervalsByHour[hour]);
                double totalHours = 0;
                foreach (var interval in merged)
                    totalHours += (interval.End - interval.Start).TotalHours;

                hourlyUsage[hour] = Math.Clamp(totalHours, 0, 1);
                if (hourlyUsage[hour] > 0.0001)
                    usedHours.Add(hour);
            }

            bool compact = !selectedEndDate.HasValue &&
                           (selectedDate.Date == DateTime.Today ||
                            selectedDate.Date == DateTime.Today.AddDays(-1));

            List<int> displayHours;
            if (usedHours.Count == 0)
            {
                int fallbackHour = selectedDate.Date == DateTime.Today ? DateTime.Now.Hour : 12;
                displayHours = new List<int> { fallbackHour };
            }
            else if (compact)
            {
                displayHours = usedHours;
            }
            else
            {
                int earliestHour = usedHours[0];
                int latestHour = usedHours[^1];
                int startHour;
                int endHour;

                if (usedHours.Count <= 3)
                {
                    int middle = (earliestHour + latestHour) / 2;
                    startHour = Math.Max(0, middle - 3);
                    endHour = Math.Min(23, middle + 3);
                }
                else
                {
                    startHour = Math.Max(0, earliestHour - 1);
                    endHour = Math.Min(23, latestHour + 1);
                }

                while (endHour - startHour < 5 && (startHour > 0 || endHour < 23))
                {
                    if (startHour > 0) startHour--;
                    else endHour++;
                }

                if (selectedDate.Date == DateTime.Today)
                    endHour = Math.Min(endHour, DateTime.Now.Hour);

                displayHours = Enumerable.Range(startHour, endHour - startHour + 1).ToList();
            }

            bool shortLabels = chart.ActualWidth < 500;
            bool mediumLabels = chart.ActualWidth < 700;
            var values = new List<double>(displayHours.Count);
            var labels = new List<string>(displayHours.Count);

            foreach (int hour in displayHours)
            {
                values.Add(hourlyUsage[hour]);
                int twelveHour = hour % 12 == 0 ? 12 : hour % 12;
                labels.Add(shortLabels
                    ? $"{twelveHour}{(hour >= 12 ? "p" : "a")}"
                    : mediumLabels
                        ? $"{twelveHour}{(hour >= 12 ? "PM" : "AM")}"
                        : $"{twelveHour} {(hour >= 12 ? "PM" : "AM")}");
            }

            UpdateSeries(chart, values, seriesColor, maxBarWidth: 40);

            chart.XAxes = new Axis[]
            {
                new()
                {
                    Labels = labels,
                    LabelsRotation = shortLabels ? 0 : 45,
                    ForceStepToMin = true,
                    MinStep = 1,
                    TextSize = 11,
                    LabelsPaint = new SolidColorPaint(axisColor),
                    SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100))
                }
            };

            chart.YAxes = new Axis[]
            {
                new()
                {
                    Name = string.Empty,
                    NamePaint = null,
                    LabelsPaint = new SolidColorPaint(axisColor),
                    TextSize = 11,
                    MinLimit = 0,
                    MaxLimit = 1,
                    ForceStepToMin = true,
                    MinStep = 0.25,
                    Labeler = TimeUtil.FormatHoursForYAxis,
                    SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100))
                }
            };
        }

        private static void UpdateDailyChart(
            LiveChartsCore.SkiaSharpView.WinUI.CartesianChart chart,
            ICollection<AppUsageRecord> usageRecords,
            TimePeriod timePeriod,
            DateTime selectedDate,
            DateTime? selectedEndDate,
            SKColor seriesColor,
            SKColor axisColor)
        {
            DateTime rangeStart;
            int daysToShow;

            if (selectedEndDate.HasValue && selectedDate.Date <= selectedEndDate.Value.Date)
            {
                rangeStart = selectedDate.Date;
                daysToShow = (selectedEndDate.Value.Date - rangeStart).Days + 1;
            }
            else
            {
                daysToShow = timePeriod == TimePeriod.Weekly ? 7 : 1;
                rangeStart = selectedDate.Date.AddDays(-(daysToShow - 1));
            }

            if (daysToShow <= 0)
                daysToShow = 1;

            DateTime rangeEndExclusive = rangeStart.AddDays(daysToShow);
            var intervalsByDay = new List<(DateTime Start, DateTime End)>[daysToShow];
            for (int i = 0; i < daysToShow; i++)
                intervalsByDay[i] = new List<(DateTime Start, DateTime End)>();

            foreach (var record in usageRecords)
            {
                DateTime start = record.StartTime < rangeStart ? rangeStart : record.StartTime;
                DateTime resolvedEnd = ResolveRecordEnd(record);
                DateTime end = resolvedEnd > rangeEndExclusive ? rangeEndExclusive : resolvedEnd;
                if (end <= start)
                    continue;

                int firstDay = Math.Max(0, (start.Date - rangeStart).Days);
                int lastDay = Math.Min(daysToShow - 1, (end.AddTicks(-1).Date - rangeStart).Days);

                for (int dayIndex = firstDay; dayIndex <= lastDay; dayIndex++)
                {
                    DateTime slotStart = rangeStart.AddDays(dayIndex);
                    DateTime slotEnd = slotStart.AddDays(1);
                    DateTime overlapStart = start > slotStart ? start : slotStart;
                    DateTime overlapEnd = end < slotEnd ? end : slotEnd;
                    if (overlapEnd > overlapStart)
                        intervalsByDay[dayIndex].Add((overlapStart, overlapEnd));
                }
            }

            var values = new List<double>(daysToShow);
            var labels = new List<string>(daysToShow);
            DateTime today = DateTime.Today;
            double maxValue = 0;

            for (int dayIndex = 0; dayIndex < daysToShow; dayIndex++)
            {
                var merged = TimeUtil.MergeIntervals(intervalsByDay[dayIndex]);
                double hours = 0;
                foreach (var interval in merged)
                    hours += (interval.End - interval.Start).TotalHours;

                hours = Math.Clamp(hours, 0, 24);
                values.Add(hours);
                maxValue = Math.Max(maxValue, hours);

                DateTime date = rangeStart.AddDays(dayIndex);
                labels.Add(date == today
                    ? "Today"
                    : date == today.AddDays(-1)
                        ? "Yesterday"
                        : date.ToString("MMM d"));
            }

            double yAxisMax;
            if (maxValue < 0.005) yAxisMax = 1;
            else if (maxValue < 0.5) yAxisMax = 1;
            else yAxisMax = Math.Min(24, Math.Ceiling(maxValue * 1.2));

            if (timePeriod == TimePeriod.Weekly)
                yAxisMax = Math.Min(24, Math.Max(2, Math.Ceiling(yAxisMax / 2) * 2));

            UpdateSeries(chart, values, seriesColor, maxBarWidth: 30);

            int labelCount = labels.Count;
            double minStep = Math.Max(1, Math.Ceiling(labelCount / 12d));
            double rotation = labelCount > 12 ? 45 : 0;

            chart.XAxes = new Axis[]
            {
                new()
                {
                    Labels = labels,
                    LabelsRotation = rotation,
                    ForceStepToMin = true,
                    MinStep = minStep,
                    TextSize = 11,
                    LabelsPaint = new SolidColorPaint(axisColor),
                    SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100))
                }
            };

            chart.YAxes = new Axis[]
            {
                new()
                {
                    Name = string.Empty,
                    NamePaint = null,
                    LabelsPaint = new SolidColorPaint(axisColor),
                    TextSize = 11,
                    MinLimit = 0,
                    MaxLimit = yAxisMax,
                    ForceStepToMin = true,
                    MinStep = timePeriod == TimePeriod.Weekly ? 2 : (yAxisMax > 4 ? 2 : 0.5),
                    Labeler = TimeUtil.FormatHoursForYAxis,
                    SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100))
                }
            };
        }

        private static void UpdateSeries(
            LiveChartsCore.SkiaSharpView.WinUI.CartesianChart chart,
            IReadOnlyCollection<double> values,
            SKColor seriesColor,
            double maxBarWidth)
        {
            if (chart.Series?.FirstOrDefault() is ColumnSeries<double> existingSeries)
            {
                existingSeries.Values = values;
                existingSeries.Fill = new SolidColorPaint(seriesColor);
                existingSeries.Stroke = null;
                existingSeries.MaxBarWidth = maxBarWidth;
                existingSeries.AnimationsSpeed = TimeSpan.Zero;
                existingSeries.EasingFunction = null;
                return;
            }

            chart.Series = new ISeries[]
            {
                new ColumnSeries<double>
                {
                    Values = values,
                    Fill = new SolidColorPaint(seriesColor),
                    Stroke = null,
                    MaxBarWidth = maxBarWidth,
                    Padding = 0,
                    Name = "Usage",
                    AnimationsSpeed = TimeSpan.Zero,
                    EasingFunction = null
                }
            };
        }

        public static TimeSpan ForceChartRefresh(
            LiveChartsCore.SkiaSharpView.WinUI.CartesianChart chart,
            ICollection<AppUsageRecord> usageRecords,
            ChartViewMode viewMode,
            TimePeriod timePeriod,
            DateTime selectedDate,
            DateTime? selectedEndDate = null)
        {
            if (chart == null)
                return TimeSpan.Zero;

            chart.Series = Array.Empty<ISeries>();
            return UpdateUsageChart(chart, usageRecords, viewMode, timePeriod, selectedDate, selectedEndDate);
        }
    }
}
