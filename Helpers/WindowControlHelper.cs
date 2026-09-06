using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace ScreenTimeTracker.Helpers
{
    /// <summary>
    /// Helper class for controlling window behavior and appearance
    /// </summary>
    public class WindowControlHelper
    {
        private const int WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const int IMAGE_ICON = 1;
        private const int LR_LOADFROMFILE = 0x0010;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, int uType, int cxDesired, int cyDesired, uint fuLoad);

        private readonly Window _window;
        private readonly AppWindow _appWindow;
        private readonly OverlappedPresenter? _presenter;
        private readonly IntPtr _windowHandle;
        private bool _isMaximized;

        public WindowControlHelper(Window window)
        {
            _window = window;
            _windowHandle = WindowNative.GetWindowHandle(window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _presenter = _appWindow.Presenter as OverlappedPresenter;
        }

        /// <summary>
        /// Sets up a comfortable activity-dashboard size. The requested dimensions are
        /// logical DIPs, then capped to the current monitor work area so high-DPI or
        /// smaller displays never open the window partly off-screen.
        /// </summary>
        public void SetUpWindow(double initialWidth = 1040, double initialHeight = 780, string title = "Screeny")
        {
            try
            {
                _appWindow.Title = title;
                _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

                SetWindowIcon();

                if (_presenter != null)
                {
                    _presenter.IsResizable = true;
                    _presenter.IsMaximizable = true;
                    _presenter.IsMinimizable = true;
                }

                try
                {
                    var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
                    var workArea = display.WorkArea;
                    var scale = Math.Max(1.0, GetScaleAdjustment());

                    int desiredWidth = (int)Math.Round(initialWidth * scale);
                    int desiredHeight = (int)Math.Round(initialHeight * scale);

                    // Leave a little room around the window for the taskbar and desktop.
                    int maxWidth = Math.Max(1, (int)Math.Round(workArea.Width * 0.94));
                    int maxHeight = Math.Max(1, (int)Math.Round(workArea.Height * 0.94));
                    int width = Math.Min(desiredWidth, maxWidth);
                    int height = Math.Min(desiredHeight, maxHeight);

                    int x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
                    int y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);

                    _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
                }
                catch (Exception ex)
                {
                    // Fall back to a taller fixed size. AppWindow.Resize consumes pixels.
                    var scale = Math.Max(1.0, GetScaleAdjustment());
                    _appWindow.Resize(new SizeInt32
                    {
                        Width = (int)Math.Round(initialWidth * scale),
                        Height = (int)Math.Round(initialHeight * scale)
                    });
                    System.Diagnostics.Debug.WriteLine($"Display-aware window sizing fell back: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting up window: {ex.Message}");
            }
        }

        private void SetWindowIcon()
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "screeny icon.ico");
            System.Diagnostics.Debug.WriteLine($"Attempting to load window icon from: {iconPath}");
            if (!File.Exists(iconPath)) return;

            try
            {
                IntPtr smallIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
                if (smallIcon != IntPtr.Zero)
                    SendMessage(_windowHandle, WM_SETICON, ICON_SMALL, smallIcon);
                else
                    System.Diagnostics.Debug.WriteLine($"LoadImage failed for ICON_SMALL. Error: {Marshal.GetLastWin32Error()}");

                IntPtr bigIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
                if (bigIcon != IntPtr.Zero)
                    SendMessage(_windowHandle, WM_SETICON, ICON_BIG, bigIcon);
                else
                    System.Diagnostics.Debug.WriteLine($"LoadImage failed for ICON_BIG. Error: {Marshal.GetLastWin32Error()}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to set window icon: {ex.Message}");
            }
        }

        public void MinimizeWindow()
        {
            _presenter?.Minimize();
        }

        public void MaximizeOrRestoreWindow()
        {
            if (_presenter == null) return;

            if (_isMaximized)
            {
                _presenter.Restore();
                _isMaximized = false;
            }
            else
            {
                _presenter.Maximize();
                _isMaximized = true;
            }
        }

        public void CloseWindow()
        {
            _window.Close();
        }

        private double GetScaleAdjustment()
        {
            try
            {
                return _window.Content?.XamlRoot?.RasterizationScale ?? 1.0;
            }
            catch
            {
                return 1.0;
            }
        }
    }
}
