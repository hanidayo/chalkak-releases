using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Chalkak
{
    /// <summary>
    /// GitHub 릴리스에서 새 버전을 찾아 내려받고, 실행 파일을 교체한 뒤 다시 시작한다.
    /// 보내는 정보는 없고(익명 GET 요청), 내려받은 파일은 GitHub 가 알려주는 SHA-256 과 비교해서 확인한다.
    /// </summary>
    internal static class Updater
    {
        const string Owner = "hanidayo", Repo = "chalkak-releases", AssetName = "Chalkak.exe";

        public class Info
        {
            public Version Version;
            public string Tag, Url, Sha256, Notes;
            public long Size;
        }

        public static Version Current
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
            }
        }

        static string UpdateDir => Path.Combine(AppSettings.DataDir, "Update");
        static readonly HttpClient http = Make();
        static bool busy;
        static DispatcherTimer timer;

        static HttpClient Make()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Chalkak-Updater/1.0");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        /// <summary>시작 직후 한 번, 이후 6시간마다 새 버전을 확인한다 (설정에서 끌 수 있음).</summary>
        public static void StartBackground()
        {
            CleanOld();
            timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            timer.Tick += async (s, e) => { if (AppSettings.Current.AutoUpdate) await CheckAsync(false); };
            timer.Start();
            var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            first.Tick += async (s, e) => { first.Stop(); if (AppSettings.Current.AutoUpdate) await CheckAsync(false); };
            first.Start();
        }

        /// <summary>manual=true 면 결과(최신 버전이에요 / 실패)를 알림으로 알려 준다.</summary>
        public static async Task<Info> CheckAsync(bool manual)
        {
            if (busy) return null;
            busy = true;
            try
            {
                Info info = await FetchLatest();
                AppSettings.Current.LastUpdateCheck = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                AppSettings.Current.Save();
                if (info == null || info.Version <= Current)
                {
                    if (manual) Toast.Show("최신 버전이에요", "찰칵 " + Current.ToString(3));
                    return null;
                }
                Offer(info);
                return info;
            }
            catch (Exception ex)
            {
                if (manual) Toast.Show("업데이트를 확인하지 못했어요", ex.Message, null, null, 5000);
                return null;
            }
            finally { busy = false; }
        }

        static async Task<Info> FetchLatest()
        {
            string json = await http.GetStringAsync("https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest");
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                string tag = root.GetProperty("tag_name").GetString() ?? "";
                Version v;
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out v)) return null;
                var info = new Info { Tag = tag, Version = new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build)) };
                if (root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String) info.Notes = body.GetString();
                foreach (var a in root.GetProperty("assets").EnumerateArray())
                {
                    if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                    info.Url = a.GetProperty("browser_download_url").GetString();
                    info.Size = a.GetProperty("size").GetInt64();
                    if (a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String)
                    {
                        string d = dg.GetString() ?? "";
                        if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) info.Sha256 = d.Substring(7);
                    }
                }
                // 공식 저장소의 릴리스 파일만 받는다
                if (info.Url == null || !info.Url.StartsWith("https://github.com/" + Owner + "/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase)) return null;
                return info;
            }
        }

        static void Offer(Info info)
        {
            Toast.Show("새 버전 " + info.Version.ToString(3) + "이 있어요", "지금 받아서 다시 시작할 수 있어요.", "업데이트", () => { var _ = InstallAsync(info); }, 15000);
        }

        /// <summary>내려받아 검증한 뒤, 현재 실행 파일을 교체하고 새 버전을 시작한다.</summary>
        public static async Task InstallAsync(Info info)
        {
            if (busy) return;
            busy = true;
            string tmp = null;
            try
            {
                Toast.Show("업데이트를 내려받는 중이에요", "찰칵 " + info.Version.ToString(3), null, null, 20000);
                Directory.CreateDirectory(UpdateDir);
                tmp = Path.Combine(UpdateDir, "Chalkak-" + info.Version.ToString(3) + ".exe.download");
                using (var resp = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    using (var src = await resp.Content.ReadAsStreamAsync())
                    using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                        await src.CopyToAsync(dst);
                }
                var fi = new FileInfo(tmp);
                if (fi.Length < 5 * 1024 * 1024 || (info.Size > 0 && fi.Length != info.Size)) throw new IOException("내려받은 파일이 올바르지 않아요.");
                using (var fs = File.OpenRead(tmp))
                {
                    int b0 = fs.ReadByte(), b1 = fs.ReadByte();
                    if (b0 != 'M' || b1 != 'Z') throw new IOException("실행 파일 형식이 아니에요.");
                }
                if (!string.IsNullOrEmpty(info.Sha256))
                {
                    string got;
                    using (var fs = File.OpenRead(tmp)) got = BitConverter.ToString(SHA256.Create().ComputeHash(fs)).Replace("-", "");
                    if (!string.Equals(got, info.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("파일 검증(SHA-256)에 실패했어요.");
                }

                string exe = Environment.ProcessPath;
                string old = exe + ".old";
                if (File.Exists(old)) { try { File.Delete(old); } catch { } }
                File.Move(exe, old);                                 // 실행 중인 파일도 이름은 바꿀 수 있다
                try { File.Move(tmp, exe); }
                catch { try { File.Move(old, exe); } catch { } throw; }

                Process.Start(new ProcessStartInfo(exe, "--tray --wait-pid " + Environment.ProcessId) { UseShellExecute = false });
                busy = false;
                App.Quit(true);
            }
            catch (Exception ex)
            {
                busy = false;
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
                Toast.Show("업데이트하지 못했어요", ex.Message);
            }
        }

        /// <summary>이전 업데이트가 남긴 파일 정리</summary>
        public static void CleanOld()
        {
            try
            {
                string old = Environment.ProcessPath + ".old";
                if (File.Exists(old)) File.Delete(old);
                if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, true);
            }
            catch { }
        }
    }
}
