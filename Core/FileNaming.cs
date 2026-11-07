using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Chalkak
{
    /// <summary>파일명 규칙: 프로그램명 또는 창 제목_YYYYMMDD_HHMMSS.png</summary>
    internal static class FileNaming
    {
        static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
        };

        public static string Stamp(DateTime t) => t.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

        public static string Sanitize(string s, string fallback = "창", int max = 60)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            var bad = new HashSet<char>(Path.GetInvalidFileNameChars());
            foreach (char c in "\\/:*?\"<>|") bad.Add(c);
            var sb = new StringBuilder();
            foreach (char c in s.Trim())
                sb.Append(char.IsControl(c) || bad.Contains(c) ? '_' : c);
            string r = sb.ToString().Trim().TrimEnd('.', ' ');
            if (r.Length > max) r = r.Substring(0, max).TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(r)) return fallback;
            if (Reserved.Contains(r)) r = "_" + r;
            return r;
        }

        /// <summary>기존 파일을 덮어쓰지 않는 새 경로. 같은 이름이 있으면 _002, _003 …</summary>
        public static string UniquePath(string folder, string baseName, string ext = ".png")
        {
            string p = Path.Combine(folder, baseName + ext);
            if (!File.Exists(p)) return p;
            for (int i = 2; i < 10000; i++)
            {
                p = Path.Combine(folder, baseName + "_" + i.ToString("000", CultureInfo.InvariantCulture) + ext);
                if (!File.Exists(p)) return p;
            }
            return Path.Combine(folder, baseName + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ext);
        }
    }
}
