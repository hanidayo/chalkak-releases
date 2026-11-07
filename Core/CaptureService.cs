using System;
using System.IO;
using System.Threading.Tasks;
using SD = System.Drawing;

namespace Chalkak
{
    internal class TargetSpec
    {
        public Func<SD.Rectangle?> Rect;
        public string Name;
        public MonitorInfo Monitor;
    }

    /// <summary>모든 캡처 모드의 진입점</summary>
    internal static class CaptureService
    {
        static bool busy;

        public static void Dispatch(string action)
        {
            if (action == "scrollAdd") { var h = ScrollCapture.AddHandler; if (h != null) h(); return; }
            if (action == "repeat" && RepeatController.Active) { RepeatController.RequestStop(); return; }
            if (busy) return;
            Run(action);
        }

        static async void Run(string action)
        {
            busy = true;
            try
            {
                switch (action)
                {
                    case "full": await DoFull(); break;
                    case "monitor": await DoMonitor(); break;
                    case "mouse": await DoMouse(); break;
                    case "window": await DoWindow(); break;
                    case "region": await DoRegion(); break;
                    case "delay": await DoDelay(); break;
                    case "repeat": await DoRepeat(); break;
                    case "scrollAuto": await ScrollCapture.RunAuto(); break;
                    case "scrollManual": await ScrollCapture.RunManual(); break;
                }
            }
            catch (Exception ex)
            {
                ErrorWindow.Show("캡처 중 문제가 생겼어요", ex);
            }
            finally { busy = false; MemoryTrim.Schedule(); }
        }

        static Task HideDelay() { return Task.Delay(140); }     // 트레이 메뉴 등이 사라질 시간

        static SD.Rectangle? Clip(SD.Rectangle r)
        {
            r.Intersect(ScreenGrab.VirtualScreen());
            if (r.Width < 2 || r.Height < 2) return null;
            return r;
        }

        // ------------------------------------------------------------------ 모드
        static async Task DoFull()
        {
            await HideDelay();
            var t = FullTarget();
            var time = DateTime.Now;
            Finish(ScreenGrab.Grab(t.Rect().Value, AppSettings.Current.IncludeCursor), t.Name, time);
        }

        static TargetSpec FullTarget()
        {
            var s = AppSettings.Current;
            var rect = s.FullScope == FullScope.AllMonitors ? ScreenGrab.VirtualScreen() : ScreenGrab.PrimaryMonitor().Bounds;
            return new TargetSpec { Rect = () => rect, Name = "전체화면", Monitor = ScreenGrab.PrimaryMonitor() };
        }

        static async Task DoMonitor()
        {
            await HideDelay();
            var virt = ScreenGrab.VirtualScreen();
            int idx;
            using (var frozen = ScreenGrab.Grab(virt, false))
                idx = await Overlays.PickMonitor(frozen, virt);
            if (idx < 0) return;
            await Task.Delay(160);
            var mon = ScreenGrab.Monitors()[Math.Min(idx, ScreenGrab.Monitors().Count - 1)];
            var time = DateTime.Now;
            Finish(ScreenGrab.Grab(mon.Bounds, AppSettings.Current.IncludeCursor), "모니터" + (mon.Index + 1), time);
        }

        static async Task DoMouse()
        {
            await HideDelay();
            var t = MouseTarget();
            var time = DateTime.Now;
            Finish(ScreenGrab.Grab(t.Rect().Value, AppSettings.Current.IncludeCursor), t.Name, time);
        }

        static TargetSpec MouseTarget()
        {
            var c = ScreenGrab.CursorPos();
            var m = ScreenGrab.MonitorAt(c.X, c.Y);
            return new TargetSpec { Rect = () => m.Bounds, Name = "마우스모니터" + (m.Index + 1), Monitor = m };
        }

        static async Task<TargetSpec> WindowTarget()
        {
            await HideDelay();
            var w = WindowFinder.GetActive();
            if (w == null) { Toast.Show("캡처할 창을 찾지 못했어요"); return null; }
            IntPtr h = w.Hwnd;
            var start = Clip(w.Bounds);
            if (start == null) { Toast.Show("이 창은 화면 밖에 있어요"); return null; }
            var c = start.Value;
            return new TargetSpec
            {
                Name = w.DisplayName(),
                Monitor = ScreenGrab.MonitorAt(c.X + c.Width / 2, c.Y + c.Height / 2),
                Rect = () => WindowFinder.IsCapturable(h) ? Clip(Native.GetFrameBounds(h)) : null
            };
        }

        static async Task DoWindow()
        {
            var t = await WindowTarget();
            if (t == null) return;
            var r = t.Rect();
            if (r == null) { Toast.Show("창을 캡처할 수 없어요", "창이 닫혔거나 최소화되었어요"); return; }
            var time = DateTime.Now;
            Finish(ScreenGrab.Grab(r.Value, AppSettings.Current.IncludeCursor), t.Name, time);
        }

        public static async Task<SD.Rectangle?> PickRegion(string hint = null)
        {
            await HideDelay();
            var virt = ScreenGrab.VirtualScreen();
            using (var frozen = ScreenGrab.Grab(virt, false))
                return await Overlays.SelectRegion(frozen, virt, hint);
        }

        static async Task DoRegion()
        {
            await HideDelay();
            var virt = ScreenGrab.VirtualScreen();
            var time = DateTime.Now;
            SD.Bitmap result = null;
            using (var frozen = ScreenGrab.Grab(virt, false))
            {
                var r = await Overlays.SelectRegion(frozen, virt);
                if (r == null) return;                             // 취소: 파일을 만들지 않는다
                var rel = r.Value;
                rel.Offset(-virt.X, -virt.Y);
                result = ScreenGrab.Crop(frozen, rel);
            }
            Finish(result, "영역", time);
        }

        public static async Task<TargetSpec> ResolveTarget(CaptureTarget t)
        {
            switch (t)
            {
                case CaptureTarget.MouseMonitor: await HideDelay(); return MouseTarget();
                case CaptureTarget.ActiveWindow: return await WindowTarget();
                case CaptureTarget.Region:
                    {
                        var r = await PickRegion();
                        if (r == null) return null;
                        var rr = r.Value;
                        return new TargetSpec
                        {
                            Rect = () => rr, Name = "영역",
                            Monitor = ScreenGrab.MonitorAt(rr.X + rr.Width / 2, rr.Y + rr.Height / 2)
                        };
                    }
                default: await HideDelay(); return FullTarget();
            }
        }

        static async Task DoDelay()
        {
            var s = AppSettings.Current;
            var target = await ResolveTarget(s.DelayTarget);
            if (target == null) return;
            if (!await CountdownWindow.Run(s.DelaySeconds, target.Monitor.Bounds)) return;
            await Task.Delay(200);
            var r = target.Rect();
            if (r == null) { Toast.Show("대상을 찾을 수 없어 캡처하지 못했어요"); return; }
            var time = DateTime.Now;
            Finish(ScreenGrab.Grab(r.Value, s.IncludeCursor), target.Name, time);
        }

        static async Task DoRepeat()
        {
            var target = await ResolveTarget(AppSettings.Current.RepeatTarget);
            if (target == null) return;
            await RepeatController.RunAsync(target);
        }

        // ------------------------------------------------------------------ 저장 + 미리보기
        /// <summary>찍은 이미지를 자동 저장하고, 설정에 따라 미리보기를 연다.</summary>
        public static void Finish(SD.Bitmap bmp, string name, DateTime time)
        {
            Sound.Play();
            string baseName = FileNaming.Sanitize(name) + "_" + FileNaming.Stamp(time);
            string path = SaveWithRetry(bmp, baseName);
            if (path == null)
            {
                // 저장을 포기해도 이미지를 잃지 않도록 클립보드에 복사
                bool ok = ImageUtil.CopyToClipboard(ImageUtil.ToSource(bmp));
                bmp.Dispose();
                Toast.Show("저장하지 못했어요", ok ? "대신 이미지를 클립보드에 복사했어요" : "이미지가 저장되지 않았어요", null, null, 5000);
                return;
            }
            bmp.Dispose();
            if (AppSettings.Current.ShowPreview)
                EditorWindow.Open(path);
            else
            {
                string p = path;
                Toast.Show("저장했어요", Path.GetFileName(path), "폴더 열기", () => ExplorerUtil.Reveal(p));
            }
        }

        static string SaveWithRetry(SD.Bitmap bmp, string baseName)
        {
            while (true)
            {
                try { return SaveService.SaveBitmap(bmp, AppSettings.Current.SaveFolder, baseName); }
                catch (Exception ex)
                {
                    int r = DialogWindow.Ask("저장하지 못했어요", ex.Message + "\n\n저장 폴더: " + AppSettings.Current.SaveFolder,
                        new[] { "취소", "다른 폴더 선택", "다시 시도" }, 2);
                    if (r == 2) continue;
                    if (r == 1)
                    {
                        var d = new Microsoft.Win32.OpenFolderDialog { Title = "저장할 폴더를 선택하세요" };
                        if (d.ShowDialog() == true)
                        {
                            AppSettings.Current.SaveFolder = d.FolderName;
                            AppSettings.Current.Save();
                        }
                        continue;
                    }
                    return null;
                }
            }
        }
    }
}
