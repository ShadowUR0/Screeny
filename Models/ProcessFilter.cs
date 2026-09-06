using System.Collections.Generic;
using System.Diagnostics;

namespace ScreenTimeTracker.Models
{
    public static class ProcessFilter
    {
        public static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            // Screen-off / away time is not app screen time and should never appear as
            // an application row or inflate the wellbeing total.
            "Idle / Away",

            // Essential Windows system processes only
            "dwm",
            "csrss",
            "services",
            "svchost",
            "winlogon",
            "wininit",
            "lsass",
            "smss",
            "System",
            "Registry",

            // Windows security and defender
            "MsMpEng",
            "SecurityHealthService",
            "smartscreen",

            // Windows updates
            "TiWorker",
            "UsoClient",

            // Audio system
            "audiodg",

            // Windows shell and system UI
            "explorer",
            "Microsoft Windows",
            "Windows Shell Experience",

            // Self-exclusion (dynamic)
            Process.GetCurrentProcess().ProcessName
        };

        public static bool ShouldIgnoreProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return true;

            if (IgnoredProcesses.Contains(processName)) return true;

            if (processName.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase) ||
                processName.StartsWith("Windows ", StringComparison.OrdinalIgnoreCase) ||
                processName.Contains("Shell Experience", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
    }
}
