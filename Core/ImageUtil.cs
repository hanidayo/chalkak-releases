using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace Chalkak
{
    internal static class ImageUtil
    {
        /// <summary>GDI+ Bitmap → WPF BitmapSource (복사본, Frozen)</summary>
        public static BitmapSource ToSource(SD.Bitmap bmp)
        {
            var rect = new SD.Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppRgb);
            try
            {
                var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgr32, null,
                    data.Scan0, data.Stride * data.Height, data.Stride);
                src.Freeze();
                return src;
            }
            finally { bmp.UnlockBits(data); }
        }

        /// <summary>파일 → Bgra32 BitmapSource (파일 잠금 없음)</summary>
        public static BitmapSource Load(string path, int decodeWidth = 0)
        {
            var bi = new BitmapImage();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bi.BeginInit();
                bi.StreamSource = fs;                      // 경로에 # 나 % 가 있어도 안전하고 파일을 잠그지 않는다
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
                bi.EndInit();
            }
            bi.Freeze();
            // 디코더 결과와 변환본을 둘 다 붙잡지 않도록 픽셀을 한 번만 복사해 새 비트맵으로 만든다 (메모리 절반)
            BitmapSource conv = bi.Format == PixelFormats.Bgra32 ? (BitmapSource)bi : new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var buf = new byte[stride * h];
            conv.CopyPixels(buf, stride, 0);
            var res = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, stride);
            res.Freeze();
            return res;
        }

        public static bool CopyToClipboard(BitmapSource src)
        {
            BitmapSource opaque = src;
            try
            {
                var fc = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
                fc.Freeze();
                opaque = fc;
            }
            catch { }
            for (int i = 0; i < 10; i++)
            {
                try { Clipboard.SetImage(opaque); return true; }
                catch { Thread.Sleep(40); }
            }
            return false;
        }

        public static void WritePng(BitmapSource src, string path)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                enc.Save(fs);
        }

        // ---------------- 모자이크 / 블러 ----------------

        public static BitmapSource Mosaic(BitmapSource src, Int32Rect r, int block)
        {
            int w = r.Width, h = r.Height, stride = w * 4;
            var buf = new byte[stride * h];
            src.CopyPixels(r, buf, stride, 0);
            block = Math.Max(2, block);
            for (int by = 0; by < h; by += block)
            {
                int bh = Math.Min(block, h - by);
                for (int bx = 0; bx < w; bx += block)
                {
                    int bw = Math.Min(block, w - bx);
                    long sb = 0, sg = 0, sr = 0, sa = 0;
                    for (int y = 0; y < bh; y++)
                    {
                        int o = ((by + y) * w + bx) * 4;
                        for (int x = 0; x < bw; x++, o += 4)
                        { sb += buf[o]; sg += buf[o + 1]; sr += buf[o + 2]; sa += buf[o + 3]; }
                    }
                    int n = bw * bh;
                    byte ab = (byte)(sb / n), ag = (byte)(sg / n), ar = (byte)(sr / n), aa = (byte)(sa / n);
                    for (int y = 0; y < bh; y++)
                    {
                        int o = ((by + y) * w + bx) * 4;
                        for (int x = 0; x < bw; x++, o += 4)
                        { buf[o] = ab; buf[o + 1] = ag; buf[o + 2] = ar; buf[o + 3] = aa; }
                    }
                }
            }
            var res = BitmapSource.Create(w, h, 96, 96, src.Format, null, buf, stride);
            res.Freeze();
            return res;
        }

        public static BitmapSource Blur(BitmapSource src, Int32Rect r, int radius)
        {
            int w = r.Width, h = r.Height, stride = w * 4;
            var a = new byte[stride * h];
            src.CopyPixels(r, a, stride, 0);
            var b = new byte[a.Length];
            radius = Math.Max(1, radius);
            for (int pass = 0; pass < 3; pass++)
            {
                BoxPass(a, b, w, h, Math.Min(radius, Math.Max(1, w / 2 - 1)), true);
                BoxPass(b, a, w, h, Math.Min(radius, Math.Max(1, h / 2 - 1)), false);
            }
            var res = BitmapSource.Create(w, h, 96, 96, src.Format, null, a, stride);
            res.Freeze();
            return res;
        }

        static void BoxPass(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
        {
            int len = horizontal ? w : h;
            int lines = horizontal ? h : w;
            int div = 2 * r + 1;
            for (int line = 0; line < lines; line++)
            {
                for (int ch = 0; ch < 4; ch++)
                {
                    long sum = 0;
                    for (int i = -r; i <= r; i++)
                    {
                        int ii = i < 0 ? 0 : (i > len - 1 ? len - 1 : i);
                        sum += src[Idx(horizontal, line, ii, w, ch)];
                    }
                    for (int i = 0; i < len; i++)
                    {
                        dst[Idx(horizontal, line, i, w, ch)] = (byte)(sum / div);
                        int add = Math.Min(len - 1, i + r + 1);
                        int sub = Math.Max(0, i - r);
                        sum += src[Idx(horizontal, line, add, w, ch)] - src[Idx(horizontal, line, sub, w, ch)];
                    }
                }
            }
        }

        static int Idx(bool horizontal, int line, int i, int w, int ch)
            => horizontal ? (line * w + i) * 4 + ch : (i * w + line) * 4 + ch;
    }

    /// <summary>임시 파일로 쓴 뒤 완성되면 이름을 바꾸는 안전한 저장 (기존 파일 덮어쓰기 금지)</summary>
    internal static class SaveService
    {
        public static string SaveBitmap(SD.Bitmap bmp, string folder, string baseName)
            => Commit(folder, baseName, tmp => bmp.Save(tmp, SDI.ImageFormat.Png));

        public static string SaveSource(BitmapSource src, string folder, string baseName)
            => Commit(folder, baseName, tmp => ImageUtil.WritePng(src, tmp));

        public static string CopyFile(string srcPath, string folder, string baseName)
            => Commit(folder, baseName, tmp => File.Copy(srcPath, tmp, true));

        static string Commit(string folder, string baseName, Action<string> write)
        {
            Directory.CreateDirectory(folder);
            // 경로가 너무 길면 이름을 줄인다
            int room = 240 - folder.Length - 8;
            if (room < 20) room = 20;
            if (baseName.Length > room) baseName = baseName.Substring(baseName.Length - room);

            string tmp = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                write(tmp);
                for (int i = 0; i < 50; i++)
                {
                    string path = FileNaming.UniquePath(folder, baseName);
                    try { File.Move(tmp, path); return path; }
                    catch (IOException) when (File.Exists(path)) { /* 다음 이름으로 */ }
                }
                throw new IOException("사용 가능한 파일 이름을 찾지 못했어요.");
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }
}
