using System;
using System.Diagnostics;

namespace ScreenTimeTracker
{
    public sealed partial class MainWindow
    {
        /// <summary>
        /// Allows the installer to replace the running executable without fighting the
        /// normal close-to-tray behavior. Tracking is stopped first so the current slice
        /// is finalized before the process exits.
        /// </summary>
        internal void ShutdownForUpdate()
        {
            try
            {
                if (_appWindow != null)
                    _appWindow.Closing -= AppWindow_Closing;

                _updateTimer?.Stop();
                if (_trackingService?.IsTracking == true)
                    _trackingService.StopTracking();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error preparing Screeny for update shutdown: {ex.Message}");
            }

            Close();
        }
    }
}
