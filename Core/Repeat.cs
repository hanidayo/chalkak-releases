using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;
using SDD = System.Drawing.Drawing2D;

namespace Chalkak
{
    internal class CapturedFrame
    {
        public int Index;
        public DateTime Time;
        public string Path;
        public BitmapSource Thumb;
        public byte[] Hash;
        public int Width, Height;
    }

    internal static class FrameFactory
    {
        public const int HashW = 128, HashH = 72;

        /// <summary>썸네일(240px)과 유사도 비교용 축소본(128×72 회색조)을 만든다. 원본 bmp는 그대로 둔다.</summary>
        public static CapturedFrame Make(SD.Bitmap bmp, string path, int index, DateTime time)
        {
            var f = new CapturedFrame { Index = index, Time = time, Path = path, Width = bmp.Width, Height = bmp.Height };
            int tw = 240, th = Math.Max(1, (int)((long)bmp.Height * tw / Math.Max(1, bmp.Width)));
            using (var t = new SD.Bitmap(tw, th, SDI.PixelFormat.Format32bppRgb))
            {
                using (var g = SD.Graphics.FromImage(t))
                {
                    g.InterpolationMode = SDD.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, new SD.Rectangle(0, 0, tw, th));
                }
                f.Thumb = ImageUtil.ToSource(t);
            }
            using (var h = new SD.Bitmap(HashW, HashH, SDI.PixelFormat.Format32bppRgb))
            {
                using (var g = SD.Graphics.FromImage(h))
                {
                    g.InterpolationMode = SDD.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, new SD.Rectangle(0, 0, HashW, HashH));
                }
                f.Hash = Gray(h);
            }
            return f;
        }

        static byte[] Gray(SD.Bitmap b)
        {
            var data = b.LockBits(new SD.Rectangle(0, 0, b.Width, b.Height), SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppRgb);
            try
            {
                int stride = data.Stride;
                var raw = new byte[stride * b.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, raw, 0, raw.Length);
                var res = new byte[b.Width * b.Height];
                for (int y = 0; y < b.Height; y++)
                    for (int x = 0; x < b.Width; x++)
                    {
                        int o = y * stride + x * 4;
                        res[y * b.Width + x] = (byte)((raw[o] * 11 + raw[o + 1] * 59 + raw[o + 2] * 30) / 100);
                    }
                return res;
            }
            finally { b.UnlockBits(data); }
        }
    }

    internal static class Similarity
    {
        /// <summary>대표 사진과 달라 보이는 칸(점)의 허용 개수. 값이 작을수록 엄격.</summary>
        public static int Limit(Sensitivity s)
        {
            switch (s)
            {
                case Sensitivity.Loose: return 150;
                case Sensitivity.Normal: return 30;
                default: return 6;
            }
        }

        static int Diff(byte[] a, byte[] b, int stopAt)
        {
            int c = 0;
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > 14 && ++c > stopAt) return c;
            return c;
        }

        /// <summary>시간상 이웃한 사진 중 대표(묶음의 첫 사진)와 거의 같은 것만 묶는다. 원본은 건드리지 않는다.</summary>
        public static List<List<CapturedFrame>> Group(List<CapturedFrame> frames, Sensitivity s)
        {
            var groups = new List<List<CapturedFrame>>();
            int limit = Limit(s);
            List<CapturedFrame> cur = null;
            foreach (var f in frames)
            {
                bool join = false;
                if (cur != null)
                {
                    var rep = cur[0];
                    join = rep.Width == f.Width && rep.Height == f.Height && rep.Hash.Length == f.Hash.Length
                           && Diff(rep.Hash, f.Hash, limit) <= limit;
                }
                if (join) cur.Add(f);
                else { cur = new List<CapturedFrame> { f }; groups.Add(cur); }
            }
            return groups;
        }
    }

    /// <summary>반복 캡처 한 번의 촬영 세션 (임시 폴더에 원본 PNG 보관)</summary>
    internal class RepeatSession
    {
        class Job { public SD.Bitmap Bmp; public DateTime Time; public int Index; }

        public static readonly HashSet<string> OpenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string Dir;
        public string NameBase = "반복";
        public DateTime Started = DateTime.Now;
        public readonly List<CapturedFrame> Frames = new List<CapturedFrame>();
        public int Skipped;
        public int Captured;
        public string EndReason = "";
        public double Elapsed;
        public event Action<double, int, int> Progress;

        CancellationTokenSource cts;

        public static string SessionsRoot => System.IO.Path.Combine(AppSettings.TempRoot, "sessions");

        public static RepeatSession Create(string nameBase)
        {
            var s = new RepeatSession { NameBase = FileNaming.Sanitize(nameBase, "반복") };
            s.Dir = System.IO.Path.Combine(SessionsRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 4));
            Directory.CreateDirectory(s.Dir);
            try { File.WriteAllText(System.IO.Path.Combine(s.Dir, "session.json"), JsonSerializer.Serialize(new { name = s.NameBase, started = s.Started })); } catch { }
            return s;
        }

        /// <summary>남아 있는 임시 폴더에서 세션 복구 (썸네일을 다시 만든다)</summary>
        public static RepeatSession FromDirectory(string dir)
        {
            var s = new RepeatSession { Dir = dir };
            try
            {
                string meta = System.IO.Path.Combine(dir, "session.json");
                if (File.Exists(meta))
                {
                    using (var doc = JsonDocument.Parse(File.ReadAllText(meta)))
                        if (doc.RootElement.TryGetProperty("name", out var n)) s.NameBase = n.GetString() ?? "반복";
                }
            }
            catch { }
            int idx = 0;
            foreach (var file in Directory.GetFiles(dir, "*.png").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    DateTime t = ParseTime(file);
                    using (var bmp = new SD.Bitmap(file))
                        s.Frames.Add(FrameFactory.Make(bmp, file, ++idx, t));
                }
                catch { }
            }
            s.Captured = s.Frames.Count;
            s.EndReason = "복구됨";
            return s;
        }

        static DateTime ParseTime(string file)
        {
            try
            {
                var parts = System.IO.Path.GetFileNameWithoutExtension(file).Split('_');
                if (parts.Length >= 4)
                {
                    DateTime d;
                    if (DateTime.TryParseExact(parts[1] + parts[2], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                    {
                        int ms; int.TryParse(parts[3], out ms);
                        return d.AddMilliseconds(ms);
                    }
                }
            }
            catch { }
            return File.GetCreationTime(file);
        }

        public void Stop(string reason = "중지됨")
        {
            if (string.IsNullOrEmpty(EndReason)) EndReason = reason;
            var c = cts; if (c != null) c.Cancel();
        }

        public async Task RunAsync(Func<SD.Rectangle?> rectProvider, double interval, double duration, int maxFrames, bool cursor)
        {
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            var queue = new BlockingCollection<Job>(6);
            var writer = Task.Run(() => WriterLoop(queue));

            await Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                int k = 0;
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        double due = k * interval;
                        if (due >= duration) { if (string.IsNullOrEmpty(EndReason)) EndReason = "완료"; break; }
                        int wait = (int)((due - sw.Elapsed.TotalSeconds) * 1000);
                        if (wait > 1) await Task.Delay(wait, ct);

                        var r = rectProvider();
                        if (r == null) { if (string.IsNullOrEmpty(EndReason)) EndReason = "대상 창을 찾을 수 없어 멈췄어요"; break; }

                        var time = DateTime.Now;
                        SD.Bitmap bmp = null;
                        try { bmp = ScreenGrab.Grab(r.Value, cursor); }
                        catch { if (string.IsNullOrEmpty(EndReason)) EndReason = "화면을 가져오지 못해 멈췄어요"; break; }

                        if (Captured >= maxFrames)
                        { bmp.Dispose(); if (string.IsNullOrEmpty(EndReason)) EndReason = "최대 촬영 수(" + maxFrames + "장)에 도달했어요"; break; }

                        if (queue.TryAdd(new Job { Bmp = bmp, Time = time, Index = Captured + 1 }, 0)) Captured++;
                        else { bmp.Dispose(); Skipped++; }          // 저장이 밀리면 쌓지 않고 건너뛴다

                        // 늦어졌다면 지나간 시각은 건너뛴다
                        int next = k + 1;
                        double now = sw.Elapsed.TotalSeconds;
                        if (next * interval < now)
                        {
                            int jump = (int)Math.Ceiling(now / interval);
                            Skipped += Math.Max(0, jump - next);
                            next = Math.Max(next, jump);
                        }
                        k = next;
                        Elapsed = sw.Elapsed.TotalSeconds;
                        Report();
                    }
                }
                catch (OperationCanceledException) { }
                Elapsed = sw.Elapsed.TotalSeconds;
            });

            queue.CompleteAdding();
            await writer;
            lock (Frames) Frames.Sort((a, b) => a.Index.CompareTo(b.Index));
            if (string.IsNullOrEmpty(EndReason)) EndReason = "중지됨";
            Report();
        }

        void Report()
        {
            var h = Progress;
            if (h == null) return;
            double e = Elapsed; int c = Captured, s = Skipped;
            Application.Current.Dispatcher.BeginInvoke(new Action(() => h(e, c, s)));
        }

        void WriterLoop(BlockingCollection<Job> queue)
        {
            foreach (var job in queue.GetConsumingEnumerable())
            {
                try
                {
                    string file = System.IO.Path.Combine(Dir, job.Index.ToString("0000") + "_" + job.Time.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".png");
                    job.Bmp.Save(file, SDI.ImageFormat.Png);
                    var f = FrameFactory.Make(job.Bmp, file, job.Index, job.Time);
                    lock (Frames) Frames.Add(f);
                }
                catch (IOException)
                {
                    if (string.IsNullOrEmpty(EndReason)) EndReason = "저장 공간이 부족하거나 쓸 수 없어 멈췄어요";
                    var c = cts; if (c != null) c.Cancel();
                }
                catch { }
                finally { job.Bmp.Dispose(); }
            }
        }

        public void DeleteFiles()
        {
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
        }
    }

    internal static class RepeatController
    {
        static RepeatSession session;
        public static bool Active => session != null;

        public static void RequestStop()
        {
            var s = session; if (s != null) s.Stop("중지됨");
        }

        public static async Task RunAsync(TargetSpec target)
        {
            var st = AppSettings.Current;
            var s = RepeatSession.Create(target.Name);
            session = s;
            App.Tray?.SetStatus("반복 촬영 중");
            var hud = new RepeatHud(st.RepeatDurationSec, target.Monitor);
            hud.StopRequested += () => s.Stop("중지됨");
            s.Progress += (el, c, sk) => hud.Update(el, c, sk);
            hud.Show();
            hud.PlaceAtTop();
            try
            {
                await Task.Delay(250);                      // HUD가 자리 잡을 시간
                await s.RunAsync(target.Rect, st.RepeatIntervalSec, st.RepeatDurationSec, st.MaxRepeatFrames, st.IncludeCursor);
            }
            finally
            {
                hud.Finish();
                session = null;
                App.Tray?.SetStatus(null);
            }

            if (s.Frames.Count == 0)
            {
                s.DeleteFiles();
                Toast.Show("촬영된 사진이 없어요", s.EndReason);
                return;
            }
            Sound.Play();
            RepeatSession.OpenDirs.Add(s.Dir);
            new GalleryWindow(s).Show();
        }
    }

    internal static class Recovery
    {
        static List<string> Pending()
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(RepeatSession.SessionsRoot)) return list;
                foreach (var d in Directory.GetDirectories(RepeatSession.SessionsRoot))
                {
                    if (RepeatSession.OpenDirs.Contains(d)) continue;
                    if (Directory.GetFiles(d, "*.png").Length > 0) list.Add(d);
                    else { try { Directory.Delete(d, true); } catch { } }
                }
            }
            catch { }
            return list;
        }

        public static void CheckAndPrompt()
        {
            var p = Pending();
            if (p.Count == 0) return;
            int r = DialogWindow.Ask("저장하지 않은 반복 촬영이 " + p.Count + "개 있어요",
                "지난번에 보관했거나 비정상 종료로 남은 촬영본이에요. 다시 열어서 필요한 사진만 저장할 수 있어요.",
                new[] { "삭제", "나중에", "열어 보기" }, 2, 0);
            if (r == 2) OpenPending();
            else if (r == 0) foreach (var d in p) { try { Directory.Delete(d, true); } catch { } }
        }

        public static async void OpenPending()
        {
            var p = Pending();
            if (p.Count == 0) { Toast.Show("보관된 반복 촬영이 없어요"); return; }
            foreach (var d in p)
            {
                RepeatSession.OpenDirs.Add(d);
                var s = await Task.Run(() => RepeatSession.FromDirectory(d));
                if (s.Frames.Count == 0) { RepeatSession.OpenDirs.Remove(d); continue; }
                new GalleryWindow(s).Show();
            }
        }
    }
}
