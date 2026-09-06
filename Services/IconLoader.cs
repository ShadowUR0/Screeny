using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using ScreenTimeTracker.Helpers;
using ScreenTimeTracker.Models;

namespace ScreenTimeTracker.Services
{
    public interface IIconLoader
    {
        Task<BitmapImage?> GetIconAsync(AppUsageRecord record, CancellationToken ct = default);
    }

    public sealed class IconLoader : IIconLoader
    {
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint WindowMessageTimeoutMilliseconds = 100;
        private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromMinutes(5);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeout,
            out IntPtr result);

        public static IconLoader Instance { get; } = new IconLoader();

        private readonly ConcurrentDictionary<string, BitmapImage> _iconCache = new();
        private readonly ConcurrentDictionary<string, DateTime> _failedUntilUtc = new();
        private readonly ConcurrentDictionary<string, Task<BitmapImage?>> _inflightLoads = new();
        private readonly SemaphoreSlim _loadGate = new(initialCount: 3, maxCount: 3);

        private IconLoader()
        {
        }

        public async Task<BitmapImage?> GetIconAsync(AppUsageRecord record, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ct.ThrowIfCancellationRequested();

            string cacheKey = AppIconIdentity.CreateProcessCacheKey(record);
            if (string.IsNullOrWhiteSpace(cacheKey))
                return null;

            if (_iconCache.TryGetValue(cacheKey, out var cached))
                return cached;

            if (_failedUntilUtc.TryGetValue(cacheKey, out var failedUntil))
            {
                if (failedUntil > DateTime.UtcNow)
                    return null;

                _failedUntilUtc.TryRemove(cacheKey, out _);
            }

            var loadTask = _inflightLoads.GetOrAdd(cacheKey, _ => StartSharedLoad(cacheKey, record));
            return await loadTask.WaitAsync(ct);
        }

        private Task<BitmapImage?> StartSharedLoad(string cacheKey, AppUsageRecord record)
        {
            var task = ResolveAndCacheAsync(cacheKey, record);
            _ = task.ContinueWith(
                completedTask =>
                {
                    _inflightLoads.TryRemove(cacheKey, out _);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }

        private async Task<BitmapImage?> ResolveAndCacheAsync(string cacheKey, AppUsageRecord record)
        {
            await _loadGate.WaitAsync();
            try
            {
                BitmapImage? resolved = null;

                string? exePath = ResolveExecutablePath(record);
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    record.ExecutablePath = exePath;
                    resolved = await TryLoadIconWithSHGetFileInfoAsync(exePath);
                }

                if (resolved == null && record.WindowHandle != IntPtr.Zero)
                    resolved = await TryLoadIconFromWindowHandleAsync(record.WindowHandle);

                if (resolved != null)
                {
                    _iconCache[cacheKey] = resolved;
                    _failedUntilUtc.TryRemove(cacheKey, out _);
                }
                else
                {
                    _failedUntilUtc[cacheKey] = DateTime.UtcNow.Add(FailureCacheDuration);
                }

                return resolved;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IconLoader failed for {record.ProcessName}: {ex.Message}");
                _failedUntilUtc[cacheKey] = DateTime.UtcNow.Add(FailureCacheDuration);
                return null;
            }
            finally
            {
                _loadGate.Release();
            }
        }

        private static IntPtr TryGetWindowIcon(IntPtr windowHandle, int iconSize)
        {
            if (SendMessageTimeout(
                    windowHandle,
                    (uint)Win32Interop.WM_GETICON,
                    (IntPtr)iconSize,
                    IntPtr.Zero,
                    SmtoAbortIfHung,
                    WindowMessageTimeoutMilliseconds,
                    out var iconHandle) == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            return iconHandle;
        }

        private static async Task<BitmapImage?> TryLoadIconFromWindowHandleAsync(IntPtr windowHandle)
        {
            try
            {
                IntPtr iconHandle = TryGetWindowIcon(windowHandle, Win32Interop.ICON_BIG);
                if (iconHandle == IntPtr.Zero)
                    iconHandle = TryGetWindowIcon(windowHandle, Win32Interop.ICON_SMALL);
                if (iconHandle == IntPtr.Zero)
                    iconHandle = Win32Interop.GetClassLongPtrSafe(windowHandle, Win32Interop.GCL_HICON);
                if (iconHandle == IntPtr.Zero)
                    iconHandle = Win32Interop.GetClassLongPtrSafe(windowHandle, Win32Interop.GCL_HICONSM);

                if (iconHandle == IntPtr.Zero)
                    return null;

                using var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(iconHandle).Clone();
                using var bitmap = icon.ToBitmap();
                return await ConvertBitmapToBitmapImageAsync(bitmap);
            }
            catch
            {
                return null;
            }
        }

        private static string? ResolveExecutablePath(AppUsageRecord record)
        {
            if (!string.IsNullOrWhiteSpace(record.ExecutablePath) && File.Exists(record.ExecutablePath))
                return record.ExecutablePath;

            string? byId = ResolveExecutablePathByProcessId(record.ProcessId);
            if (!string.IsNullOrWhiteSpace(byId)) return byId;

            string processName = Path.GetFileNameWithoutExtension(record.ProcessName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(processName)) return null;

            try
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        string? path = ResolveExecutablePathByProcessId(process.Id);
                        if (!string.IsNullOrWhiteSpace(path)) return path;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string? ResolveExecutablePathByProcessId(int processId)
        {
            if (processId <= 0) return null;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                string? path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
            }
            catch { }

            try
            {
                IntPtr processHandle = Win32Interop.OpenProcess(
                    Win32Interop.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
                if (processHandle == IntPtr.Zero) return null;
                try
                {
                    var path = new StringBuilder(1024);
                    int size = path.Capacity;
                    return Win32Interop.QueryFullProcessImageName(processHandle, 0, path, ref size)
                        && File.Exists(path.ToString()) ? path.ToString() : null;
                }
                finally { Win32Interop.CloseHandle(processHandle); }
            }
            catch { return null; }
        }

        private static async Task<BitmapImage?> TryLoadIconWithSHGetFileInfoAsync(string path)
        {
            if (!File.Exists(path))
                return null;

            var info = new Win32Interop.SHFILEINFO();
            uint flags = Win32Interop.SHGFI_ICON | Win32Interop.SHGFI_LARGEICON;

            if (Win32Interop.SHGetFileInfo(
                    path,
                    0,
                    ref info,
                    (uint)Marshal.SizeOf(info),
                    flags) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                using var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(info.hIcon).Clone();
                using var bitmap = icon.ToBitmap();
                return await ConvertBitmapToBitmapImageAsync(bitmap);
            }
            catch
            {
                return null;
            }
            finally
            {
                Win32Interop.DestroyIcon(info.hIcon);
            }
        }

        private static async Task<BitmapImage?> ConvertBitmapToBitmapImageAsync(System.Drawing.Bitmap bitmap)
        {
            try
            {
                using var pngStream = new MemoryStream();
                bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
                byte[] pngBytes = pngStream.ToArray();

                var image = new BitmapImage();
                using var randomAccessStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(randomAccessStream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(pngBytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                }

                randomAccessStream.Seek(0);
                await image.SetSourceAsync(randomAccessStream);
                return image;
            }
            catch
            {
                return null;
            }
        }
    }
}
