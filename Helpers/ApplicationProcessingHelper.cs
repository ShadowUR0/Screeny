using ScreenTimeTracker.Models;
using System.Diagnostics;
using System.IO;

namespace ScreenTimeTracker.Helpers
{
    public static class ApplicationProcessingHelper
    {
        public static void ProcessApplicationRecord(AppUsageRecord record)
        {
            if (record == null) return;
            string stableProcessName = ApplicationNameNormalizer.NormalizeProcessName(record.ProcessName);
            string? executablePath = string.IsNullOrWhiteSpace(record.ExecutablePath)
                ? TryResolveExecutablePath(record.ProcessId)
                : record.ExecutablePath;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                record.ExecutablePath = executablePath;
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(executablePath);
                    if (!string.IsNullOrWhiteSpace(info.ProductName)) record.ApplicationName = info.ProductName;
                }
                catch { }
            }
            record.ProcessName = stableProcessName;
            if (string.IsNullOrWhiteSpace(record.ApplicationName)) record.ApplicationName = stableProcessName;
        }

        private static string? TryResolveExecutablePath(int processId)
        {
            if (processId <= 0) return null;
            try
            {
                using var process = Process.GetProcessById(processId);
                string? path = process.MainModule?.FileName;
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
