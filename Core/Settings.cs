using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chalkak
{
    public enum NameMode { Program, Title }
    public enum FullScope { AllMonitors, PrimaryOnly }
    public enum CaptureTarget { Full, MouseMonitor, ActiveWindow, Region }
    public enum Sensitivity { Strict, Normal, Loose }

    public class AppSettings
    {
        public string SaveFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "찰칵");
        public bool ShowPreview { get; set; } = true;
        public bool PlaySound { get; set; } = true;
        public string SoundName { get; set; } = "dslr";          // dslr / film / compact / mirrorless / soft / custom
        public int SoundVolume { get; set; } = 80;               // 0~100
        public string SoundCustomPath { get; set; } = "";
        public bool IncludeCursor { get; set; } = false;
        public NameMode NameMode { get; set; } = NameMode.Program;
        public FullScope FullScope { get; set; } = FullScope.AllMonitors;

        public int DelaySeconds { get; set; } = 3;
        public CaptureTarget DelayTarget { get; set; } = CaptureTarget.Full;

        public double RepeatIntervalSec { get; set; } = 0.5;
        public double RepeatDurationSec { get; set; } = 30;
        public CaptureTarget RepeatTarget { get; set; } = CaptureTarget.Full;
        public bool GroupSimilar { get; set; } = true;
        public Sensitivity SimilarSensitivity { get; set; } = Sensitivity.Strict;
        public int MaxRepeatFrames { get; set; } = 600;

        public bool AutoStart { get; set; } = false;
        public bool CloseToTray { get; set; } = true;
        public bool FirstRunDone { get; set; } = false;
        public bool AutoUpdate { get; set; } = true;
        public string LastUpdateCheck { get; set; } = "";

        public Dictionary<string, string> Hotkeys { get; set; } = DefaultHotkeys();

        public static Dictionary<string, string> DefaultHotkeys() => new Dictionary<string, string>
        {
            { "full", "Ctrl+Alt+PrtSc" },
            { "monitor", "Ctrl+Alt+M" },
            { "mouse", "Ctrl+Alt+P" },
            { "window", "Ctrl+Alt+W" },
            { "region", "Ctrl+Alt+A" },
            { "scrollAuto", "Ctrl+Alt+S" },
            { "scrollManual", "Ctrl+Alt+Shift+S" },
            { "delay", "Ctrl+Alt+D" },
            { "repeat", "Ctrl+Alt+R" },
        };

        // ---------- 저장/불러오기 ----------
        [JsonIgnore] public static AppSettings Current { get; private set; } = new AppSettings();

        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "찰칵");
        public static string TempRoot => Path.Combine(DataDir, "Temp");
        static string FilePath => Path.Combine(DataDir, "settings.json");

        static JsonSerializerOptions Opt()
        {
            var o = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            o.Converters.Add(new JsonStringEnumConverter());
            return o;
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opt());
                    if (s != null) Current = s;
                }
            }
            catch { Current = new AppSettings(); }   // 깨진 설정은 기본값으로 복구
            Current.Validate();
        }

        public void Save()
        {
            try
            {
                Validate();
                Directory.CreateDirectory(DataDir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opt()));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch { }
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(SaveFolder)) SaveFolder = new AppSettings().SaveFolder;
            if (DelaySeconds < 1 || DelaySeconds > 60) DelaySeconds = 3;
            if (!(RepeatIntervalSec >= 0.05) || RepeatIntervalSec > 60) RepeatIntervalSec = 0.5;
            if (!(RepeatDurationSec >= 1) || RepeatDurationSec > 3600) RepeatDurationSec = 30;
            if (MaxRepeatFrames < 10 || MaxRepeatFrames > 5000) MaxRepeatFrames = 600;
            if (SoundVolume < 0 || SoundVolume > 100) SoundVolume = 80;
            if (string.IsNullOrWhiteSpace(SoundName)) SoundName = "dslr";
            if (Hotkeys == null) Hotkeys = DefaultHotkeys();
            foreach (var kv in DefaultHotkeys())
                if (!Hotkeys.ContainsKey(kv.Key)) Hotkeys[kv.Key] = kv.Value;
        }
    }
}
