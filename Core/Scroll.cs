using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SD = System.Drawing;

namespace Chalkak
{
    internal static class ScrollCapture
    {
        public static Action AddHandler;           // 수동 모드에서 단축키로 "현재 화면 추가"

        const int MaxHeightPx = 40000;

        // ------------------------------------------------------------ 수동
        public static async Task RunManual()
        {
            var rect = await CaptureService.PickRegion("스크롤할 영역을 드래그로 지정하세요");
            if (rect == null) return;
            var r = rect.Value;
            var frames = new List<SD.Bitmap>();
            var hud = new ScrollHud(false, ScreenGrab.MonitorAt(r.X + r.Width / 2, r.Y + r.Height / 2));
            var tcs = new TaskCompletionSource<int>();         // 1=완료, 0=취소
            hud.Show();
            hud.PlaceAtBottom();

            Action add = () =>
            {
                try
                {
                    var bmp = ScreenGrab.Grab(r, AppSettings.Current.IncludeCursor);
                    frames.Add(bmp);
                    hud.SetCount(frames.Count);
                    Sound.Play();
                }
                catch { }
            };
            hud.AddClicked += add;
            hud.DoneClicked += () => tcs.TrySetResult(1);
            hud.CancelClicked += () => tcs.TrySetResult(0);
            int tempId = App.Hotkeys.RegisterTemp("scrollAdd", "Ctrl+Alt+Space");
            AddHandler = add;
            hud.SetHotkeyHint(tempId >= 0 ? "Ctrl+Alt+Space" : null);

            add();   // 첫 화면은 자동으로 추가
            int res = await tcs.Task;

            AddHandler = null;
            App.Hotkeys.UnregisterTemp(tempId);
            hud.Finish();
            if (res == 1 && frames.Count > 0) await Finish(frames);
            else foreach (var f in frames) f.Dispose();
        }

        // ------------------------------------------------------------ 자동
        public static async Task RunAuto()
        {
            var rect = await CaptureService.PickRegion("자동으로 스크롤할 영역을 드래그로 지정하세요");
            if (rect == null) return;
            var r = rect.Value;
            var mon = ScreenGrab.MonitorAt(r.X + r.Width / 2, r.Y + r.Height / 2);
            if (!await CountdownWindow.Run(3, mon.Bounds)) return;       // 3초 동안 스크롤할 창을 준비
            await Task.Delay(150);

            var hud = new ScrollHud(true, mon);
            bool stop = false;
            hud.CancelClicked += () => stop = true;
            hud.Show();
            hud.PlaceAtBottom();
            await Task.Delay(250);

            var frames = new List<SD.Bitmap>();
            SD.Bitmap first = ScreenGrab.Grab(r, AppSettings.Current.IncludeCursor);
            var stitcher = new Stitcher(first);
            first.Dispose();
            int count = 1;
            int notches = 3;
            string reason = "맨 아래까지 도달했어요";
            Native.SetCursorPos(r.X + r.Width / 2, r.Y + r.Height / 2);

            for (int step = 0; step < 80 && !stop; step++)
            {
                if ((Native.GetAsyncKeyState(Native.VK_ESCAPE) & 0x8000) != 0) { reason = "중지했어요"; break; }
                Native.mouse_event(Native.MOUSEEVENTF_WHEEL, 0, 0, -120 * notches, UIntPtr.Zero);
                await Task.Delay(550);                                    // 스크롤 애니메이션과 지연 로딩 대기
                var bmp = ScreenGrab.Grab(r, AppSettings.Current.IncludeCursor);
                if (stitcher.IsSame(bmp)) { bmp.Dispose(); break; }       // 더 이상 움직이지 않음 = 끝

                var res = await Task.Run(() => { int sh; bool o = stitcher.Add(bmp, out sh); return new KeyValuePair<bool, int>(o, sh); });
                bool ok = res.Key;
                int shift = res.Value;
                bmp.Dispose();                                            // 자동 모드는 이어 붙인 결과만 보관 (메모리 절약)
                count++;
                hud.SetCount(count);

                // 한 번에 이동하는 양을 화면 높이의 약 40%로 맞춘다
                if (ok && shift > 0)
                {
                    double pxPerNotch = Math.Max(1.0, (double)shift / notches);
                    notches = Math.Max(1, Math.Min(30, (int)Math.Round(r.Height * 0.4 / pxPerNotch)));
                }
                else if (!ok) notches = Math.Max(1, notches / 2);

                if (stitcher.TotalHeight > MaxHeightPx) { reason = "최대 길이에 도달했어요"; break; }
            }
            hud.Finish();

            await FinishFromStitcher(stitcher, frames, reason);
        }

        // ------------------------------------------------------------ 마무리
        static async Task Finish(List<SD.Bitmap> frames)
        {
            var time = DateTime.Now;
            SD.Bitmap result;
            int failedJoins = 0;
            try
            {
                result = await Task.Run(() =>
                {
                    var st = new Stitcher(frames[0]);
                    int sh;
                    for (int i = 1; i < frames.Count; i++) st.Add(frames[i], out sh);
                    failedJoins = st.Failed;
                    return st.Build();
                });
            }
            catch (Exception ex)
            {
                foreach (var f in frames) f.Dispose();
                Toast.Show("스크롤 캡처에 실패했어요", ex.Message);
                return;
            }
            await Deliver(result, frames, failedJoins, time, null);
        }

        static async Task FinishFromStitcher(Stitcher st, List<SD.Bitmap> frames, string reason)
        {
            var time = DateTime.Now;
            SD.Bitmap result;
            try { result = await Task.Run(() => st.Build()); }
            catch (Exception ex)
            {
                foreach (var f in frames) f.Dispose();
                Toast.Show("스크롤 캡처에 실패했어요", ex.Message);
                return;
            }
            await Deliver(result, frames, st.Failed, time, reason);
        }

        static async Task Deliver(SD.Bitmap result, List<SD.Bitmap> frames, int failed, DateTime time, string reason)
        {
            if (failed > 0 && frames.Count <= 1)
                Toast.Show("일부 구간은 정확히 이어 붙이지 못했어요", failed + "곳에서 겹침을 찾지 못했어요. 수동 스크롤 캡처로 다시 시도해 보세요.", null, null, 5500);
            if (failed > 0 && frames.Count > 1)
            {
                int r = DialogWindow.Ask("일부 구간은 정확히 이어 붙이지 못했어요",
                    failed + "곳에서 겹치는 부분을 찾지 못해 장면을 그대로 붙였어요. 고정된 메뉴나 움직이는 화면이 있으면 그럴 수 있어요. 장면을 하나씩 볼 수도 있어요.",
                    new[] { "장면 개별 보기", "이어 붙인 결과 사용" }, 1);
                if (r == 0)
                {
                    result.Dispose();
                    OpenScenes(frames);
                    return;
                }
            }
            foreach (var f in frames) f.Dispose();
            CaptureService.Finish(result, "스크롤", time);
            if (!string.IsNullOrEmpty(reason) && failed == 0) Toast.Show("스크롤 캡처 완료", reason);
            await Task.CompletedTask;
        }

        static void OpenScenes(List<SD.Bitmap> frames)
        {
            var s = RepeatSession.Create("스크롤장면");
            int i = 0;
            foreach (var f in frames)
            {
                try
                {
                    var t = DateTime.Now.AddMilliseconds(i);
                    string file = System.IO.Path.Combine(s.Dir, (++i).ToString("0000") + "_" + t.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                    f.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                    s.Frames.Add(FrameFactory.Make(f, file, i, t));
                }
                catch { }
                finally { f.Dispose(); }
            }
            s.Captured = s.Frames.Count;
            RepeatSession.OpenDirs.Add(s.Dir);
            new GalleryWindow(s).Show();
        }
    }
}
