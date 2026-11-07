using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Chalkak
{
    /// <summary>
    /// 트레이에 상주하는 프로그램이 캡처 후 큰 이미지를 붙잡고 있지 않도록,
    /// 작업이 끝나고 잠시 뒤 메모리를 정리하고 작업 집합(실제 점유 메모리)을 줄인다.
    /// </summary>
    internal static class MemoryTrim
    {
        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        static DispatcherTimer timer;

        public static void Schedule(int delayMs = 1500)
        {
            try
            {
                if (timer == null)
                {
                    timer = new DispatcherTimer(DispatcherPriority.Background);
                    timer.Tick += (s, e) => { timer.Stop(); RunNow(); };
                }
                timer.Stop();
                timer.Interval = TimeSpan.FromMilliseconds(delayMs);
                timer.Start();
            }
            catch { }
        }

        public static void RunNow()
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                using (var p = Process.GetCurrentProcess())
                    SetProcessWorkingSetSize(p.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }
    }
}
