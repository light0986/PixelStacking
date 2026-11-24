using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 沒有系統標題列（WindowStyle=None）的視窗最大化時，Windows 會把它放大成整個螢幕（連工作列一起蓋住）。
    /// 這個掛鉤讓「最大化」剛好等於視窗所在那個螢幕的工作區（實體像素，配合 PerMonitorV2），工作列不會被蓋住：
    ///
    /// 1. WM_GETMINMAXINFO：最小尺寸 = MinWidth / MinHeight 乘上該螢幕目前的 DPI 縮放。
    /// 2. WM_WINDOWPOSCHANGING：視窗處於最大化時，直接把要套用的位置與大小改成工作區。
    ///    不用 ptMaxSize 來指定最大化大小，是因為多螢幕時 Windows 會把它當成「主螢幕的最大化大小」，
    ///    再加上「目標螢幕與主螢幕的尺寸差」才套用（實測：副螢幕 1920×1080、主螢幕 1366×768 時多出 554×312），
    ///    而且結果小於最小尺寸時還會再被夾住，很難反推；直接改最終的位置與大小最可靠。
    /// </summary>
    internal static class MaximizeHook
    {
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int MONITOR_DEFAULTTONEAREST = 2;
        private const int GWL_STYLE = -16;
        private const int WS_MAXIMIZE = 0x01000000;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOMOVE = 0x0002;

        public static void Attach(Window window)
        {
            window.SourceInitialized += (s, e) =>
            {
                HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
                if (source == null) { return; }

                source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == WM_GETMINMAXINFO) { FixMinMax(window, lParam); }
                    else if (msg == WM_WINDOWPOSCHANGING) { FixMaximizedPlacement(hwnd, lParam); }
                    return IntPtr.Zero;   // handled 維持 false：WPF 照常處理其他部分
                });
            };
        }

        /// <summary>最小追蹤尺寸：MinWidth / MinHeight 是裝置無關單位，要換成這個螢幕的實體像素。</summary>
        private static void FixMinMax(Window window, IntPtr lParam)
        {
            MINMAXINFO mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));

            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            if (window.MinWidth > 0) { mmi.ptMinTrackSize.x = (int)Math.Ceiling(window.MinWidth * dpi.DpiScaleX); }
            if (window.MinHeight > 0) { mmi.ptMinTrackSize.y = (int)Math.Ceiling(window.MinHeight * dpi.DpiScaleY); }

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        /// <summary>視窗正在最大化（或已最大化又被重新擺放）時，把位置與大小改成所在螢幕的工作區。</summary>
        private static void FixMaximizedPlacement(IntPtr hwnd, IntPtr lParam)
        {
            if ((GetWindowLong(hwnd, GWL_STYLE) & WS_MAXIMIZE) == 0) { return; }

            WINDOWPOS pos = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
            if ((pos.flags & SWP_NOSIZE) != 0 && (pos.flags & SWP_NOMOVE) != 0) { return; }   // 只是改 Z 順序之類，沒有要動位置

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            MONITORINFO info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) { return; }

            if ((pos.flags & SWP_NOMOVE) == 0)
            {
                pos.x = info.rcWork.Left;
                pos.y = info.rcWork.Top;
            }
            if ((pos.flags & SWP_NOSIZE) == 0)
            {
                pos.cx = info.rcWork.Right - info.rcWork.Left;
                pos.cy = info.rcWork.Bottom - info.rcWork.Top;
            }

            Marshal.StructureToPtr(pos, lParam, false);
        }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd, hwndInsertAfter;
            public int x, y, cx, cy;
            public int flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public int dwFlags;
        }
    }
}
