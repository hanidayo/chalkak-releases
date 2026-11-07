using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Chalkak
{
    public partial class App : Application
    {
        internal static HotkeyManager Hotkeys;
        internal static TrayIcon Tray;
        static bool quitting;
        EventWaitHandle showEvent;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 업데이트 직후 다시 시작된 경우: 이전 프로세스가 끝날 때까지 기다린다
            for (int i = 0; i + 1 < e.Args.Length; i++)
            {
                int oldPid;
                if (string.Equals(e.Args[i], "--wait-pid", StringComparison.OrdinalIgnoreCase) && int.TryParse(e.Args[i + 1], out oldPid))
                {
                    try { using (var op = System.Diagnostics.Process.GetProcessById(oldPid)) op.WaitForExit(20000); } catch { }
                }
            }

            // 이미 실행 중이면 그쪽에 설정 창을 열라고 알리고 종료
            bool created;
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Chalkak.ShowSettings.v1", out created);
            if (!created) { showEvent.Set(); Shutdown(); return; }

            DispatcherUnhandledException += (s, ev) =>
            {
                Log(ev.Exception);
                try { Toast.Show("예상치 못한 문제가 생겼어요", ev.Exception.Message, null, null, 6000); } catch { }
                ev.Handled = true;
            };

            AppSettings.Load();
            ThemeManager.Apply();
            ThemeManager.Watch();

            Hotkeys = new HotkeyManager();
            Hotkeys.Pressed += action => Dispatcher.BeginInvoke(new Action(() => CaptureService.Dispatch(action)));
            Hotkeys.ReloadAll();

            Tray = new TrayIcon();
            Updater.StartBackground();
            if (AppSettings.Current.AutoStart) Autostart.Apply(true);

            var waiter = new Thread(() =>
            {
                while (true)
                {
                    try { showEvent.WaitOne(); } catch { return; }
                    Dispatcher.BeginInvoke(new Action(() => SettingsWindow.ShowSingleton()));
                }
            }) { IsBackground = true };
            waiter.Start();

            bool trayStart = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
            var st = AppSettings.Current;
            bool conflicts = Hotkeys.Status.Values.Any(v => !v);

            if (!trayStart || !st.FirstRunDone) SettingsWindow.ShowSingleton();
            if (!st.FirstRunDone) { st.FirstRunDone = true; st.Save(); }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (conflicts) Toast.Show("일부 단축키를 등록하지 못했어요", "다른 프로그램이 쓰고 있어요. 설정에서 바꿀 수 있어요.", "설정", () => SettingsWindow.ShowSingleton(), 7000);
                else if (trayStart) Toast.Show("찰칵이 실행 중이에요", "영역 캡처  " + st.Hotkeys["region"], null, null, 2800);
            }), DispatcherPriority.ApplicationIdle);

            CleanShareTemp();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (s, ev) => { t.Stop(); try { Recovery.CheckAndPrompt(); } catch { } };
            t.Start();
        }

        static void CleanShareTemp()
        {
            try
            {
                string dir = Path.Combine(AppSettings.TempRoot, "share");
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir))
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1)) { try { File.Delete(f); } catch { } }
            }
            catch { }
        }

        static void Log(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                File.AppendAllText(Path.Combine(AppSettings.DataDir, "error.log"), DateTime.Now.ToString("s") + "\n" + ex + "\n\n");
            }
            catch { }
        }

        /// <summary>완전 종료. 저장하지 않은 갤러리·편집 내용이 있으면 먼저 확인한다.</summary>
        internal static void Quit(bool afterUpdate = false)
        {
            if (quitting) return;
            if (RepeatController.Active)
            {
                int r = DialogWindow.Ask("반복 촬영이 진행 중이에요", "지금 종료하면 촬영이 멈춰요.", new[] { "계속 촬영", "중지하고 종료" }, 0, 1);
                if (r != 1) return;
                RepeatController.RequestStop();
            }
            if (!GalleryWindow.CloseAll()) return;
            foreach (var w in Current.Windows.OfType<EditorWindow>().ToList())
            {
                w.Close();
                if (w.IsLoaded) return;                     // 사용자가 취소함
            }
            quitting = true;
            try { if (Tray != null) Tray.Dispose(); } catch { }
            try { if (Hotkeys != null) Hotkeys.Dispose(); } catch { }
            Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { if (Tray != null) Tray.Dispose(); } catch { }
            try { if (Hotkeys != null) Hotkeys.Dispose(); } catch { }
            base.OnExit(e);
        }
    }
}
