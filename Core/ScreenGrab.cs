using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;
using WF = System.Windows.Forms;

namespace Chalkak
{
    internal class MonitorInfo
    {
        public int Index;
        public SD.Rectangle Bounds;
        public bool Primary;
        public string Label => "모니터 " + (Index + 1) + (Primary ? " (주)" : "");
        public string Resolution => Bounds.Width + " × " + Bounds.Height;
    }

    internal static class ScreenGrab
    {
        public static List<MonitorInfo> Monitors()
        {
            var list = new List<MonitorInfo>();
            var all = WF.Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
                list.Add(new MonitorInfo { Index = i, Bounds = all[i].Bounds, Primary = all[i].Primary });
            return list;
        }

        public static SD.Rectangle VirtualScreen()
        {
            var m = Monitors();
            int l = m.Min(x => x.Bounds.Left), t = m.Min(x => x.Bounds.Top);
            int r = m.Max(x => x.Bounds.Right), b = m.Max(x => x.Bounds.Bottom);
            return SD.Rectangle.FromLTRB(l, t, r, b);
        }

        public static MonitorInfo PrimaryMonitor()
        {
            var m = Monitors();
            return m.FirstOrDefault(x => x.Primary) ?? m[0];
        }

        public static MonitorInfo MonitorAt(int x, int y)
        {
            var m = Monitors();
            foreach (var mi in m) if (mi.Bounds.Contains(x, y)) return mi;
            // 경계 밖이면 가장 가까운 모니터
            return m.OrderBy(mi => Math.Abs(mi.Bounds.X + mi.Bounds.Width / 2 - x) + Math.Abs(mi.Bounds.Y + mi.Bounds.Height / 2 - y)).First();
        }

        public static SD.Point CursorPos()
        {
            Native.POINT p;
            Native.GetCursorPos(out p);
            return new SD.Point(p.X, p.Y);
        }

        public static SD.Bitmap Grab(SD.Rectangle r, bool cursor)
        {
            if (r.Width <= 0 || r.Height <= 0) throw new ArgumentException("캡처 영역이 비어 있어요.");
            var bmp = new SD.Bitmap(r.Width, r.Height, SDI.PixelFormat.Format32bppRgb);
            using (var g = SD.Graphics.FromImage(bmp))
            {
                IntPtr hdcDest = g.GetHdc();
                IntPtr hdcSrc = Native.GetDC(IntPtr.Zero);
                try
                {
                    // SRCCOPY | CAPTUREBLT (레이어드 창 포함). .NET의 CopyFromScreen은 이 조합을 거부하므로 BitBlt를 직접 호출한다.
                    if (!Native.BitBlt(hdcDest, 0, 0, r.Width, r.Height, hdcSrc, r.X, r.Y, 0x00CC0020u | 0x40000000u))
                        throw new InvalidOperationException("화면을 가져오지 못했어요.");
                }
                finally
                {
                    Native.ReleaseDC(IntPtr.Zero, hdcSrc);
                    g.ReleaseHdc(hdcDest);
                }
                if (cursor) Native.DrawCursor(g, r);
            }
            return bmp;
        }

        public static SD.Bitmap Crop(SD.Bitmap src, SD.Rectangle r)
        {
            r.Intersect(new SD.Rectangle(0, 0, src.Width, src.Height));
            var dst = new SD.Bitmap(r.Width, r.Height, SDI.PixelFormat.Format32bppRgb);
            using (var g = SD.Graphics.FromImage(dst))
                g.DrawImage(src, new SD.Rectangle(0, 0, r.Width, r.Height), r, SD.GraphicsUnit.Pixel);
            return dst;
        }
    }

    internal class WinInfo
    {
        public IntPtr Hwnd;
        public string Title;
        public string Program;
        public SD.Rectangle Bounds;

        public string DisplayName()
        {
            var mode = AppSettings.Current.NameMode;
            string first = mode == NameMode.Program ? Program : Title;
            string second = mode == NameMode.Program ? Title : Program;
            if (!string.IsNullOrWhiteSpace(first)) return first;
            if (!string.IsNullOrWhiteSpace(second)) return second;
            return "창";
        }
    }

    internal static class WindowFinder
    {
        static readonly HashSet<string> SkipClass = new HashSet<string>
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow"
        };

        public static bool IsCapturable(IntPtr h)
        {
            try
            {
                if (h == IntPtr.Zero || !Native.IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;
                if (Native.IsCloaked(h)) return false;
                if (SkipClass.Contains(Native.GetClass(h))) return false;
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (pid == (uint)Process.GetCurrentProcess().Id) return false;
                var b = Native.GetFrameBounds(h);
                return b.Width > 8 && b.Height > 8;
            }
            catch { return false; }
        }

        /// <summary>현재 활성 창. 작업 표시줄 등이 활성이면 Z순서 맨 위의 일반 창을 사용</summary>
        public static WinInfo GetActive()
        {
            IntPtr h = Native.GetForegroundWindow();
            if (!IsCapturable(h))
            {
                h = IntPtr.Zero;
                IntPtr w = Native.GetTopWindow(IntPtr.Zero);
                int guard = 0;
                while (w != IntPtr.Zero && guard++ < 400)
                {
                    if (IsCapturable(w) && Native.GetTitle(w).Length > 0 &&
                        (Native.GetWindowLong(w, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) == 0)
                    { h = w; break; }
                    w = Native.GetWindow(w, Native.GW_HWNDNEXT);
                }
            }
            return h == IntPtr.Zero ? null : Describe(h);
        }

        public static WinInfo Describe(IntPtr h)
        {
            var info = new WinInfo { Hwnd = h, Title = Native.GetTitle(h), Bounds = Native.GetFrameBounds(h) };
            try
            {
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                using (var p = Process.GetProcessById((int)pid))
                {
                    string name = null;
                    try { name = p.MainModule.FileVersionInfo.FileDescription; } catch { }
                    if (string.IsNullOrWhiteSpace(name)) name = p.ProcessName;
                    if (string.Equals(p.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) name = null;
                    info.Program = name;
                }
            }
            catch { }
            return info;
        }
    }
}
