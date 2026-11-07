using System;
using System.Collections.Generic;
using System.Linq;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace Chalkak
{
    /// <summary>
    /// 스크롤 장면 이어 붙이기. 이전 장면과 새 장면이 겹치는 만큼을 찾아 새로 보이는 줄만 덧붙인다.
    /// 고정 푸터는 감지해 한 번만 붙이고, 겹침을 못 찾으면 장면 전체를 이어 붙이고 Failed 로 기록한다.
    /// </summary>
    internal class Stitcher
    {
        class Strip { public byte[] Data; public int Rows; }

        public readonly int W, H;
        public int Failed;
        public int LastShift;
        readonly List<Strip> strips = new List<Strip>();
        byte[] prev;
        Strip tail;
        int total;
        int stickyFooter;
        int prevF;
        byte[] firstRaw;
        bool colsKnown;
        int staticL, staticR;      // 고정 영역: [0,staticL) 와 [staticR,W)

        public int TotalHeight => total + (tail == null ? 0 : tail.Rows);

        // ---- 진단용: 실행 파일 옆 debug 폴더에 처음 몇 장면과 이어 붙이기 기록을 남긴다 ----
        static int dbgRun;
        int dbgN;
        static string DbgDir
        {
            get
            {
                try
                {
                    string exe = Environment.ProcessPath;
                    string root = System.IO.Path.GetDirectoryName(exe) ?? ".";
                    if (!System.IO.File.Exists(System.IO.Path.Combine(root, "debug.txt"))) return null;   // 실행 파일 옆에 debug.txt 가 있을 때만 기록
                    string dir = System.IO.Path.Combine(root, "debug");
                    System.IO.Directory.CreateDirectory(dir);
                    return dir;
                }
                catch { return null; }
            }
        }
        void DbgFrame(SD.Bitmap b)
        {
            try
            {
                if (dbgN >= 6) return;
                string d = DbgDir; if (d == null) return;
                b.Save(System.IO.Path.Combine(d, "frame" + dbgN + ".png"), SDI.ImageFormat.Png);
                dbgN++;
            }
            catch { }
        }
        void DbgLog(string line)
        {
            try
            {
                string d = DbgDir; if (d == null) return;
                System.IO.File.AppendAllText(System.IO.Path.Combine(d, "scroll.log"), DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + "\r\n");
            }
            catch { }
        }
        System.Text.StringBuilder dbg = new System.Text.StringBuilder();

        public Stitcher(SD.Bitmap first)
        {
            W = first.Width; H = first.Height;
            try { string d = DbgDir; if (d != null) { foreach (var f in System.IO.Directory.GetFiles(d, "frame*.png")) System.IO.File.Delete(f); System.IO.File.WriteAllText(System.IO.Path.Combine(d, "scroll.log"), "새 스크롤 캡처 " + W + "x" + H + "\r\n"); } } catch { }
            DbgFrame(first);
            prev = Read(first);
            firstRaw = prev;
            staticR = W;
            strips.Add(new Strip { Data = prev, Rows = H });
            total = H;
        }

        byte[] Read(SD.Bitmap b)
        {
            var d = b.LockBits(new SD.Rectangle(0, 0, b.Width, b.Height), SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppRgb);
            try
            {
                var raw = new byte[W * 4 * H];
                for (int y = 0; y < H; y++)
                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), raw, y * W * 4, W * 4);
                return raw;
            }
            finally { b.UnlockBits(d); }
        }

        /// <summary>직전 장면과 사실상 같은 화면인가 (스크롤 끝 감지용)</summary>
        public bool IsSame(SD.Bitmap b)
        {
            if (b.Width != W || b.Height != H) return false;
            var cur = Read(b);
            long sum = 0; int n = 0;
            for (int y = 0; y < H; y += 3)
            {
                int o = y * W * 4;
                for (int x = 0; x < W; x += 5)
                {
                    int i = o + x * 4;
                    sum += Math.Abs(cur[i] - prev[i]) + Math.Abs(cur[i + 1] - prev[i + 1]) + Math.Abs(cur[i + 2] - prev[i + 2]);
                    n += 3;
                }
            }
            return n == 0 || (double)sum / n < 0.6;
        }

        /// <summary>새 장면을 이어 붙인다. 반환: 겹침을 찾았는지(Found)와 이동량(Shift)</summary>
        public bool Add(SD.Bitmap b, out int shift)
        {
            shift = 0;
            if (b.Width != W || b.Height != H) { Failed++; return false; }
            var cur = Read(b);
            int s;
            DbgFrame(b);
            dbg.Clear();
            bool found = FindShift(prev, cur, out s);
            DbgLog("found=" + found + " shift=" + s + "  " + dbg);
            if (found && s == 0) { prev = cur; return true; }     // 움직임 없음

            if (!found || s <= 0 || s >= H)
            {
                // 겹침을 못 찾음: 장면 전체를 그대로 붙여 내용이 빠지지 않게 한다
                Failed++;
                AddStrip(cur, 0, H);
                tail = null;
                prevF = 0;
                prev = cur;
                return false;
            }

            if (!colsKnown && s >= 20) { DetectStaticCols(prev, cur); colsKnown = true; }
            // 고정 푸터: 처음 한 번 찾은 값을 계속 쓰고, 그 위의 페이드/그림자 구간(margin)까지 푸터로 본다.
            // 장면마다 줄 수가 달라지면 이음새가 어긋나므로 항상 같은 값(Fnow)을 쓴다.
            int margin = Math.Max(24, H / 14);
            int fnow = 0;
            if (stickyFooter > 0)
            {
                if (FooterHolds(prev, cur, stickyFooter)) fnow = Math.Min(H / 2, stickyFooter + margin);
            }
            else
            {
                int f0 = FooterRows(prev, cur);
                if (f0 >= 6) { stickyFooter = f0; fnow = Math.Min(H / 2, f0 + margin); }
            }
            if (fnow + s >= H) fnow = Math.Max(0, H - s - 1);
            if (fnow > 0 && strips.Count == 1 && tail == null && prevF == 0)
            {
                TrimBottom(strips[0], fnow);      // 첫 장면에서 푸터 구간을 빼고(맨 끝에 한 번만 붙임) 이어 붙인다
                prevF = fnow;
            }
            int startRow = H - prevF - s;
            int rowsNew = s + prevF - fnow;
            if (startRow >= 0 && rowsNew > 0) AddStrip(cur, startRow, rowsNew);
            tail = fnow > 0 ? Slice(cur, H - fnow, fnow) : null;
            prevF = fnow;
            prev = cur;
            LastShift = s;
            shift = s;
            return true;
        }

        void AddStrip(byte[] src, int fromRow, int rows)
        {
            if (rows <= 0) return;
            strips.Add(Slice(src, fromRow, rows));
            total += rows;
        }

        Strip Slice(byte[] src, int fromRow, int rows)
        {
            var d = new byte[rows * W * 4];
            Buffer.BlockCopy(src, fromRow * W * 4, d, 0, d.Length);
            return new Strip { Data = d, Rows = rows };
        }

        void TrimBottom(Strip st, int rows)
        {
            int keep = st.Rows - rows;
            if (keep <= 0) return;
            var d = new byte[keep * W * 4];
            Buffer.BlockCopy(st.Data, 0, d, 0, d.Length);
            total -= rows;
            st.Data = d; st.Rows = keep;
        }

        public SD.Bitmap Build()
        {
            int h = TotalHeight;
            var bmp = new SD.Bitmap(W, h, SDI.PixelFormat.Format32bppRgb);
            var d = bmp.LockBits(new SD.Rectangle(0, 0, W, h), SDI.ImageLockMode.WriteOnly, SDI.PixelFormat.Format32bppRgb);
            try
            {
                int y = 0;
                var all = new List<Strip>(strips);
                if (tail != null) all.Add(tail);
                foreach (var st in all)
                    for (int r = 0; r < st.Rows; r++, y++)
                        System.Runtime.InteropServices.Marshal.Copy(st.Data, r * W * 4, IntPtr.Add(d.Scan0, y * d.Stride), W * 4);

                // 고정 영역(사이드바 등)은 첫 장면의 것을 한 번만 두고, 나머지 아래쪽은 단색으로 채운다
                int tailRows = tail == null ? 0 : tail.Rows;
                foreach (var band in new[] { new[] { 0, staticL }, new[] { staticR, W } })
                {
                    int a = band[0], b = band[1];
                    if (b <= a) continue;
                    int bw = (b - a) * 4;
                    int fillIdx = ((H - 1) * W + (a == 0 ? a : b - 1)) * 4;
                    byte[] fill = { firstRaw[fillIdx], firstRaw[fillIdx + 1], firstRaw[fillIdx + 2], 255 };
                    var line = new byte[bw];
                    for (int yy = 0; yy < h; yy++)
                    {
                        if (yy < H) System.Buffer.BlockCopy(firstRaw, (yy * W + a) * 4, line, 0, bw);
                        else if (tailRows > 0 && yy >= h - tailRows) System.Buffer.BlockCopy(tail.Data, ((yy - (h - tailRows)) * W + a) * 4, line, 0, bw);
                        else for (int k = 0; k < bw; k += 4) { line[k] = fill[0]; line[k + 1] = fill[1]; line[k + 2] = fill[2]; line[k + 3] = 255; }
                        System.Runtime.InteropServices.Marshal.Copy(line, 0, IntPtr.Add(d.Scan0, yy * d.Stride + a * 4), bw);
                    }
                }
            }
            finally { bmp.UnlockBits(d); }
            return bmp;
        }

        // ---------------- 겹침 찾기 ----------------

        const int RowStep = 3, ColStep = 6;

        double BandStd(byte[] b, int top, int rows)
        {
            double sum = 0, sum2 = 0; int n = 0;
            for (int y = top; y < top + rows; y += 2)
                for (int x = 0; x < W; x += 6)
                {
                    int i = (y * W + x) * 4;
                    double g = (b[i] * 11 + b[i + 1] * 59 + b[i + 2] * 30) / 100.0;
                    sum += g; sum2 += g * g; n++;
                }
            if (n == 0) return 0;
            double mean = sum / n;
            return Math.Sqrt(Math.Max(0, sum2 / n - mean * mean));
        }

        static bool Close(byte[] A, int ia, byte[] B, int ib, int tol)
        {
            return Math.Abs(A[ia] - B[ib]) <= tol && Math.Abs(A[ia + 1] - B[ib + 1]) <= tol && Math.Abs(A[ia + 2] - B[ib + 2]) <= tol;
        }

        // 무늬(글자·경계선)가 있는 픽셀인가: 오른쪽 이웃과의 차이가 큼
        bool Textured(byte[] b, int i)
        {
            if (i + 7 >= b.Length) return false;
            return Math.Abs(b[i] - b[i + 4]) + Math.Abs(b[i + 1] - b[i + 5]) + Math.Abs(b[i + 2] - b[i + 6]) > 45;
        }

        /// <summary>
        /// 새 장면(B)의 윗부분 조각이 이전 장면(P)의 어디(아래로 s줄)에서 왔는지 찾는다.
        /// 무늬가 있는 픽셀이 얼마나 일치하는지(일치율)로 판단하므로, 스크롤되지 않는 사이드바 같은 고정 영역이 있어도 흔들리지 않는다.
        /// </summary>
        bool FindShift(byte[] P, byte[] B, out int shift)
        {
            shift = 0;
            int bh = Math.Max(24, H / 8);
            double bestScore = 0; int bestShift = 0;
            foreach (double frac in new[] { 0.04, 0.14, 0.26, 0.40, 0.55 })
            {
                int t = (int)(H * frac);
                if (t + bh > H) continue;
                var samples = new List<int>();
                for (int y = t; y < t + bh; y += 2)
                    for (int x = 0; x < W - 2; x += 2)
                    {
                        int i = (y * W + x) * 4;
                        if (!Textured(B, i)) continue;
                        if (Close(P, i, B, i, 6)) continue;          // 같은 자리에서 그대로면 고정 영역이므로 제외
                        samples.Add(i);
                    }
                if (samples.Count < 60) { dbg.Append("[t=" + frac + " few=" + samples.Count + "] "); continue; }
                if (samples.Count > 2500)
                {
                    var cut = new List<int>(2500);
                    double stride = samples.Count / 2500.0;
                    for (double k = 0; k < samples.Count && cut.Count < 2500; k += stride) cut.Add(samples[(int)k]);
                    samples = cut;
                }
                int maxS = H - bh - t;
                var score = new int[maxS + 2];
                int rowBytes = W * 4;
                for (int sft = 1; sft <= maxS; sft++)
                {
                    int m = 0, off = sft * rowBytes;
                    for (int k = 0; k < samples.Count; k++)
                    {
                        int ib = samples[k];
                        if (Close(P, ib + off, B, ib, 10)) m++;
                    }
                    score[sft] = m;
                }
                int bs = 1;
                for (int sft = 2; sft <= maxS; sft++) if (score[sft] > score[bs]) bs = sft;
                int second = 0;
                for (int sft = 1; sft <= maxS; sft++) if (Math.Abs(sft - bs) > 4 && score[sft] > second) second = score[sft];
                double sc = (double)score[bs] / samples.Count, sc2 = (double)second / samples.Count;
                dbg.Append("[t=" + frac + " n=" + samples.Count + " s=" + bs + " sc=" + sc.ToString("0.00") + " 2nd=" + sc2.ToString("0.00") + "] ");
                if (sc >= 0.35 && sc - sc2 >= 0.12 && sc > bestScore) { bestScore = sc; bestShift = bs; }
            }
            if (bestShift <= 0) return false;
            shift = bestShift;
            return true;
        }

        /// <summary>스크롤되지 않는 좌우 가장자리(사이드바 등)를 찾는다. 결과에는 첫 장면의 것을 한 번만 남긴다.</summary>
        void DetectStaticCols(byte[] P, byte[] B)
        {
            var isStatic = new bool[W];
            for (int x = 0; x < W; x++)
            {
                int same = 0, n = 0;
                for (int y = 0; y < H; y += 4)
                {
                    n++;
                    int i = (y * W + x) * 4;
                    if (Close(P, i, B, i, 8)) same++;
                }
                isStatic[x] = n > 0 && same >= n * 0.985;
            }
            int l = 0; while (l < W && isStatic[l]) l++;
            int r = W; while (r > 0 && isStatic[r - 1]) r--;
            if (l < 24) l = 0;
            if (W - r < 24) r = W;
            if (r - l < 80) { l = 0; r = W; }
            if (l > 0 && !HasTexture(P, 0, l)) l = 0;
            if (r < W && !HasTexture(P, r, W)) r = W;
            staticL = l; staticR = r;
        }

        bool HasTexture(byte[] b, int x0, int x1)
        {
            int tex = 0, n = 0;
            for (int y = 0; y < H; y += 3)
                for (int x = x0; x < x1 - 1; x += 2)
                {
                    n++;
                    if (Textured(b, (y * W + x) * 4)) tex++;
                }
            return n > 0 && tex >= n * 0.01;
        }

        // 한 줄이 거의 같은가 (스크롤바·미세한 그라데이션 같은 작은 차이는 허용)
        bool RowsSame(byte[] P, byte[] B, int row, int tol)
        {
            int o = row * W * 4, diff = 0, n = 0;
            for (int x = 0; x < W; x += 3)
            {
                int i = o + x * 4;
                n++;
                if (Math.Abs(P[i] - B[i]) > tol || Math.Abs(P[i + 1] - B[i + 1]) > tol || Math.Abs(P[i + 2] - B[i + 2]) > tol) diff++;
            }
            return diff <= n * 0.03;
        }

        /// <summary>화면 아래쪽에 스크롤과 상관없이 고정된 줄 수 (채팅 입력창, 하단 바 등)</summary>
        int FooterRows(byte[] P, byte[] B)
        {
            int max = H / 3, last = 0, gap = 0;
            for (int k = 1; k <= max; k++)
            {
                if (RowsSame(P, B, H - k, 10)) { last = k; gap = 0; }
                else if (++gap > 3) break;                 // 경계의 그림자/페이드는 몇 줄까지 건너뜀
            }
            if (last < 6) return 0;
            return BandStd(B, H - last, Math.Max(2, last)) >= 4 ? last : 0;     // 단색 여백은 푸터로 보지 않음
        }

        /// <summary>이미 알고 있는 푸터가 이번 장면에서도 그대로인지 (느슨하게 확인)</summary>
        bool FooterHolds(byte[] P, byte[] B, int f)
        {
            int bad = 0;
            for (int k = 1; k <= f; k++) if (!RowsSame(P, B, H - k, 18)) bad++;
            return bad <= Math.Max(2, f / 10);
        }
    }
}
