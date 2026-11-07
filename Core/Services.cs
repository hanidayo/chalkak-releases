using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using Microsoft.Win32;

namespace Chalkak
{
    internal static class Sound
    {
        /// <summary>내장 셔터 소리 (Id, 표시 이름). "custom" 은 사용자가 고른 wav 파일.</summary>
        public static readonly KeyValuePair<string, string>[] Presets =
        {
            new KeyValuePair<string, string>("dslr", "DSLR (찰칵-철컥)"),
            new KeyValuePair<string, string>("film", "필름 카메라 (셔터 + 필름 감기)"),
            new KeyValuePair<string, string>("compact", "콤팩트 카메라"),
            new KeyValuePair<string, string>("mirrorless", "미러리스 (깔끔한 한 번)"),
            new KeyValuePair<string, string>("soft", "부드러운 클릭 (조용한 곳)"),
        };

        static readonly Dictionary<string, byte[]> cache = new Dictionary<string, byte[]>();
        static SoundPlayer player;

        /// <summary>캡처할 때: 설정에서 소리가 꺼져 있으면 재생하지 않는다.</summary>
        public static void Play()
        {
            if (!AppSettings.Current.PlaySound) return;
            Start();
        }

        /// <summary>설정 창의 미리듣기: 소리 켜짐 여부와 상관없이 재생.</summary>
        public static void Preview() { Start(); }

        static void Start()
        {
            try
            {
                var s = AppSettings.Current;
                byte[] data = null;
                if (s.SoundName == "custom" && !string.IsNullOrWhiteSpace(s.SoundCustomPath))
                {
                    try { data = File.ReadAllBytes(s.SoundCustomPath); } catch { data = null; }
                }
                if (data == null) data = Builtin(s.SoundName == "custom" ? "dslr" : s.SoundName);
                if (data == null) return;
                data = ApplyVolume(data, s.SoundVolume);
                var old = player;
                player = new SoundPlayer(new MemoryStream(data));
                player.Load();
                player.Play();
                if (old != null) old.Dispose();
            }
            catch { }
        }

        static byte[] Builtin(string id)
        {
            lock (cache)
            {
                byte[] d;
                if (cache.TryGetValue(id, out d)) return d;
                try
                {
                    var info = Application.GetResourceStream(new Uri("/Assets/Sounds/" + id + ".wav", UriKind.Relative));
                    using (var ms = new MemoryStream()) { info.Stream.CopyTo(ms); d = ms.ToArray(); }
                }
                catch { d = null; }
                cache[id] = d;
                return d;
            }
        }

        /// <summary>16비트 PCM wav 의 크기를 volume(0~100)%로 줄인 복사본. 다른 형식은 그대로 돌려준다.</summary>
        static byte[] ApplyVolume(byte[] wav, int volume)
        {
            if (volume >= 100) return wav;
            volume = Math.Max(0, volume);
            try
            {
                if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return wav;
                int pos = 12, fmtTag = 0, bits = 0, dataPos = -1, dataLen = 0;
                while (pos + 8 <= wav.Length)
                {
                    string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
                    int len = BitConverter.ToInt32(wav, pos + 4);
                    if (id == "fmt ") { fmtTag = BitConverter.ToInt16(wav, pos + 8); bits = BitConverter.ToInt16(wav, pos + 22); }
                    else if (id == "data") { dataPos = pos + 8; dataLen = Math.Min(len, wav.Length - dataPos); break; }
                    pos += 8 + len + (len & 1);
                }
                if (dataPos < 0 || fmtTag != 1 || bits != 16) return wav;
                var copy = (byte[])wav.Clone();
                double g = volume / 100.0; g = g * g;                       // 체감 음량에 맞게 곡선 적용
                for (int i = dataPos; i + 1 < dataPos + dataLen; i += 2)
                {
                    short v = BitConverter.ToInt16(copy, i);
                    short n = (short)Math.Round(v * g);
                    copy[i] = (byte)(n & 0xFF); copy[i + 1] = (byte)((n >> 8) & 0xFF);
                }
                return copy;
            }
            catch { return wav; }
        }
    }

    internal static class ExplorerUtil
    {
        public static void Reveal(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                else
                    OpenFolder(Path.GetDirectoryName(path));
            }
            catch { }
        }

        public static void OpenFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
            }
            catch { }
        }
    }

    internal static class Autostart
    {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "Chalkak";

        public static void Apply(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(ValueName, "\"" + Environment.ProcessPath + "\" --tray");
                    else k.DeleteValue(ValueName, false);
                }
            }
            catch { }
        }
    }

    internal static class ThemeManager
    {
        public static bool IsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k?.GetValue("AppsUseLightTheme");
                    return v is int i && i == 0;
                }
            }
            catch { return false; }
        }

        public static void Apply()
        {
            try
            {
                var d = new ResourceDictionary
                {
                    Source = new Uri(IsDark() ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
                };
                Application.Current.Resources.MergedDictionaries[0] = d;
            }
            catch { }
        }

        public static void Watch()
        {
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General)
                    Application.Current.Dispatcher.BeginInvoke(new Action(Apply));
            };
        }
    }

    /// <summary>"Ctrl+Alt+A" ↔ (modifiers, Key)</summary>
    internal static class HotkeyText
    {
        public static string ToText(ModifierKeys m, Key k)
        {
            var parts = new List<string>();
            if ((m & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((m & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((m & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((m & ModifierKeys.Windows) != 0) parts.Add("Win");
            parts.Add(KeyName(k));
            return string.Join("+", parts);
        }

        public static string KeyName(Key k)
        {
            if (k == Key.Snapshot || k == Key.PrintScreen) return "PrtSc";
            if (k >= Key.D0 && k <= Key.D9) return ((int)k - (int)Key.D0).ToString();
            return k.ToString();
        }

        public static bool TryParse(string text, out ModifierKeys mods, out Key key)
        {
            mods = ModifierKeys.None; key = Key.None;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var raw in text.Split('+'))
            {
                string t = raw.Trim();
                switch (t.ToLowerInvariant())
                {
                    case "ctrl": mods |= ModifierKeys.Control; break;
                    case "alt": mods |= ModifierKeys.Alt; break;
                    case "shift": mods |= ModifierKeys.Shift; break;
                    case "win": mods |= ModifierKeys.Windows; break;
                    case "prtsc": key = Key.Snapshot; break;
                    default:
                        if (t.Length == 1 && char.IsDigit(t[0])) { key = (Key)((int)Key.D0 + (t[0] - '0')); break; }
                        Key parsed;
                        if (Enum.TryParse(t, true, out parsed)) key = parsed; else return false;
                        break;
                }
            }
            return key != Key.None;
        }
    }

    /// <summary>전역 단축키 (RegisterHotKey)</summary>
    internal class HotkeyManager : IDisposable
    {
        readonly HwndSource source;
        readonly Dictionary<int, string> idToAction = new Dictionary<int, string>();
        int nextId = 100;

        public Dictionary<string, bool> Status { get; } = new Dictionary<string, bool>();
        public event Action<string> Pressed;

        public HotkeyManager()
        {
            var p = new HwndSourceParameters("ChalkakHotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 };
            source = new HwndSource(p);
            source.AddHook(Hook);
        }

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY)
            {
                string action;
                if (idToAction.TryGetValue(wParam.ToInt32(), out action))
                {
                    handled = true;
                    var h = Pressed;
                    if (h != null) h(action);
                }
            }
            return IntPtr.Zero;
        }

        public void ReloadAll()
        {
            foreach (var id in idToAction.Keys) Native.UnregisterHotKey(source.Handle, id);
            idToAction.Clear();
            Status.Clear();
            foreach (var kv in AppSettings.Current.Hotkeys) Register(kv.Key, kv.Value);
        }

        public bool Register(string action, string text)
        {
            Status[action] = true;
            ModifierKeys mods; Key key;
            if (string.IsNullOrWhiteSpace(text)) return true;      // 지정 안 함
            if (!HotkeyText.TryParse(text, out mods, out key)) { Status[action] = false; return false; }
            uint fs = 0x4000;                                       // MOD_NOREPEAT
            if ((mods & ModifierKeys.Alt) != 0) fs |= 1;
            if ((mods & ModifierKeys.Control) != 0) fs |= 2;
            if ((mods & ModifierKeys.Shift) != 0) fs |= 4;
            if ((mods & ModifierKeys.Windows) != 0) fs |= 8;
            int id = nextId++;
            bool ok = Native.RegisterHotKey(source.Handle, id, fs, (uint)KeyInterop.VirtualKeyFromKey(key));
            if (ok) idToAction[id] = action;
            Status[action] = ok;
            return ok;
        }

        /// <summary>세션 동안만 쓰는 임시 단축키. 반환값은 해제용 id (-1이면 실패)</summary>
        public int RegisterTemp(string action, string text)
        {
            ModifierKeys mods; Key key;
            if (!HotkeyText.TryParse(text, out mods, out key)) return -1;
            uint fs = 0x4000;
            if ((mods & ModifierKeys.Alt) != 0) fs |= 1;
            if ((mods & ModifierKeys.Control) != 0) fs |= 2;
            if ((mods & ModifierKeys.Shift) != 0) fs |= 4;
            int id = nextId++;
            if (!Native.RegisterHotKey(source.Handle, id, fs, (uint)KeyInterop.VirtualKeyFromKey(key))) return -1;
            idToAction[id] = action;
            return id;
        }

        public void UnregisterTemp(int id)
        {
            if (id < 0) return;
            Native.UnregisterHotKey(source.Handle, id);
            idToAction.Remove(id);
        }

        public void Dispose()
        {
            foreach (var id in idToAction.Keys) Native.UnregisterHotKey(source.Handle, id);
            idToAction.Clear();
            source.Dispose();
        }
    }
}
