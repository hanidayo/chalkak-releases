using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Chalkak
{
    public partial class SettingsWindow : ChromeWindow
    {
        static SettingsWindow instance;
        readonly AppSettings S = AppSettings.Current;
        StackPanel card;
        readonly Dictionary<string, TextBlock> hotkeyStatus = new Dictionary<string, TextBlock>();
        readonly Dictionary<string, TextBox> hotkeyBoxes = new Dictionary<string, TextBox>();
        TextBlock folderText;

        public static void ShowSingleton()
        {
            if (instance == null) { instance = new SettingsWindow(); instance.Show(); }
            if (instance.WindowState == WindowState.Minimized) instance.WindowState = WindowState.Normal;
            instance.Activate();
        }

        SettingsWindow()
        {
            InitializeComponent();
            Build();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            instance = null;
            S.Save();
            if (!S.CloseToTray) Dispatcher.BeginInvoke(new Action(() => App.Quit()));
        }

        // =============================================================== 구성
        static Brush Res(string k) { return (Brush)Application.Current.FindResource(k); }

        static int lastTab;
        static readonly string[] TabNames = { "일반", "캡처", "소리", "단축키", "정보" };

        void Build()
        {
            string group = "settingsTabs";
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < TabNames.Length; i++)
            {
                int idx = i;
                var rb = new RadioButton { Content = TabNames[i], GroupName = group, Style = (Style)Application.Current.FindResource("SegButton"), Padding = new Thickness(16, 5, 16, 5) };
                rb.Checked += (s, e) => ShowTab(idx);
                if (i == lastTab) rb.IsChecked = true;
                sp.Children.Add(rb);
            }
            TabHost.CornerRadius = new CornerRadius(8);
            TabHost.Background = Res("FillSoft");
            TabHost.Padding = new Thickness(1);
            TabHost.Child = sp;
        }

        void ShowTab(int i)
        {
            lastTab = i;
            Root.Children.Clear();
            hotkeyBoxes.Clear(); hotkeyStatus.Clear();
            switch (i)
            {
                case 0: BuildGeneral(); break;
                case 1: BuildCapture(); break;
                case 2: BuildSound(); break;
                case 3: BuildHotkeys(); break;
                default: BuildAbout(); break;
            }
        }

        void BuildGeneral()
        {
            Section("저장");
            folderText = new TextBlock { Text = S.SaveFolder, MaxWidth = 230, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Foreground = Res("TextSecondary"), ToolTip = S.SaveFolder };
            var folderBtn = SmallBtn("변경…"); folderBtn.Click += (s, e) => ChooseFolder();
            var openBtn = SmallBtn("열기"); openBtn.Margin = new Thickness(6, 0, 0, 0); openBtn.Click += (s, e) => ExplorerUtil.OpenFolder(S.SaveFolder);
            Row("저장 폴더", Horiz(folderText, folderBtn, openBtn), "캡처하면 이 폴더에 PNG로 자동 저장돼요.");
            Row("캡처 후 바로 편집 창 열기", Switch(S.ShowPreview, v => { S.ShowPreview = v; S.Save(); }), "끄면 편집 창 없이 저장만 하고 알림으로 알려줘요.");
            Row("마우스 커서 포함", Switch(S.IncludeCursor, v => { S.IncludeCursor = v; S.Save(); }), "영역 캡처는 정지된 화면에서 고르기 때문에 커서가 포함되지 않아요.");
            Row("파일 이름", Segmented(S.NameMode, new[] { Tuple.Create("프로그램명", NameMode.Program), Tuple.Create("창 제목", NameMode.Title) }, v => { S.NameMode = v; S.Save(); }),
                "창 캡처 이름: 이름_20261020_143025.png  (창 제목에 개인 정보가 있다면 ‘프로그램명’을 추천해요)");
            Row("전체 화면 범위", Segmented(S.FullScope, new[] { Tuple.Create("모든 모니터", FullScope.AllMonitors), Tuple.Create("주 모니터만", FullScope.PrimaryOnly) }, v => { S.FullScope = v; S.Save(); }));

            Section("시스템");
            Row("Windows 시작 시 자동 실행", Switch(S.AutoStart, v => { S.AutoStart = v; S.Save(); Autostart.Apply(v); }), "트레이에서 조용히 시작해요.");
            Row("설정 창을 닫아도 계속 실행", Switch(S.CloseToTray, v => { S.CloseToTray = v; S.Save(); }), "끄면 설정 창을 닫을 때 찰칵이 완전히 종료돼요.");
        }

        void BuildCapture()
        {
            Section("지연 캡처");
            Row("대기 시간", Stepper(S.DelaySeconds, 1, 60, 1, "0'초'", v => { S.DelaySeconds = (int)v; S.Save(); }));
            Row("캡처 대상", TargetSeg(S.DelayTarget, v => { S.DelayTarget = v; S.Save(); }), "시작할 때 대상이 정해지고, 카운트다운 중 Esc나 클릭으로 취소할 수 있어요.");

            Section("반복 캡처");
            Row("촬영 간격", Stepper(S.RepeatIntervalSec, 0.1, 10, 0.1, "0.0'초'", v => { S.RepeatIntervalSec = Math.Round(v, 2); S.Save(); }), "기본 0.5초");
            Row("총 촬영 시간", Stepper(S.RepeatDurationSec, 1, 600, 1, "0'초'", v => { S.RepeatDurationSec = v; S.Save(); }), "기본 30초. 끝나면 갤러리에서 필요한 사진만 골라 저장해요.");
            Row("캡처 대상", TargetSeg(S.RepeatTarget, v => { S.RepeatTarget = v; S.Save(); }));
            Row("비슷한 사진 묶기", Switch(S.GroupSimilar, v => { S.GroupSimilar = v; S.Save(); }), "묶어도 원본은 그대로예요. 펼치면 모든 사진을 볼 수 있어요.");
            Row("묶는 기준", Segmented(S.SimilarSensitivity, new[] { Tuple.Create("엄격", Sensitivity.Strict), Tuple.Create("보통", Sensitivity.Normal), Tuple.Create("느슨", Sensitivity.Loose) }, v => { S.SimilarSensitivity = v; S.Save(); }),
                "엄격: 작은 글자 변화도 따로 둬요 · 느슨: 더 많이 묶어요");
            Row("최대 촬영 수", Stepper(S.MaxRepeatFrames, 50, 2000, 50, "0'장'", v => { S.MaxRepeatFrames = (int)v; S.Save(); }), "메모리·디스크 보호를 위한 상한이에요.");
        }

        void BuildSound()
        {
            Section("셔터 소리");
            Row("셔터 소리 사용", Switch(S.PlaySound, v => { S.PlaySound = v; S.Save(); }), "끄면 캡처할 때 소리가 나지 않아요.");

            var volLabel = new TextBlock { Width = 42, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Text = S.SoundVolume + "%" };
            var vol = new Slider { Minimum = 0, Maximum = 100, Width = 170, Value = S.SoundVolume, Style = (Style)Application.Current.FindResource("AppleSlider"), VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
            vol.ValueChanged += (s, e) => { S.SoundVolume = (int)Math.Round(vol.Value); volLabel.Text = S.SoundVolume + "%"; };
            vol.PreviewMouseLeftButtonUp += (s, e) => { S.Save(); Sound.Preview(); };
            vol.KeyUp += (s, e) => { S.Save(); Sound.Preview(); };
            Row("음량", Horiz(vol, volLabel), "찰칵 소리의 크기만 조절해요. (윈도우 전체 음량과는 별개예요)");

            Section("소리 종류");
            string group = "shutterSound";
            foreach (var p in Sound.Presets)
            {
                string id = p.Key;
                var rb = new RadioButton { Content = p.Value, GroupName = group, Style = (Style)Application.Current.FindResource("RadioRow"), IsChecked = S.SoundName == id };
                rb.Checked += (s, e) => { S.SoundName = id; S.Save(); Sound.Preview(); };
                var play = SmallBtn("▶ 듣기");
                play.Click += (s, e) => { string prev = S.SoundName; S.SoundName = id; Sound.Preview(); S.SoundName = prev; };
                Row(null, play, null, rb);
            }

            // 내 파일
            var customName = new TextBlock { Text = string.IsNullOrWhiteSpace(S.SoundCustomPath) ? "선택한 파일 없음" : System.IO.Path.GetFileName(S.SoundCustomPath), Foreground = Res("TextSecondary"), MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ToolTip = S.SoundCustomPath };
            var customRb = new RadioButton { Content = "내 소리 파일 (.wav)", GroupName = group, Style = (Style)Application.Current.FindResource("RadioRow"), IsChecked = S.SoundName == "custom" };
            var pick = SmallBtn("파일 선택…");
            Action pickFile = () =>
            {
                var d = new Microsoft.Win32.OpenFileDialog { Title = "셔터 소리로 쓸 wav 파일", Filter = "WAV 소리 파일 (*.wav)|*.wav" };
                if (d.ShowDialog(this) != true) { if (string.IsNullOrWhiteSpace(S.SoundCustomPath)) RestoreSoundChoice(); return; }
                S.SoundCustomPath = d.FileName; S.SoundName = "custom"; S.Save();
                customName.Text = System.IO.Path.GetFileName(d.FileName); customName.ToolTip = d.FileName;
                customRb.IsChecked = true;
                Sound.Preview();
            };
            pick.Click += (s, e) => pickFile();
            customRb.Checked += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(S.SoundCustomPath)) { Dispatcher.BeginInvoke(pickFile); return; }
                S.SoundName = "custom"; S.Save(); Sound.Preview();
            };
            Row(null, Horiz(customName, pick), "wav 파일만 지원해요. 짧은 소리(1초 이내)가 좋아요.", customRb);

            var test = SmallBtn("▶ 지금 설정으로 듣기");
            test.Click += (s, e) => Sound.Preview();
            Row("", test);
        }

        void RestoreSoundChoice() { ShowTab(2); }

        void BuildHotkeys()
        {
            Section("캡처 단축키");
            AddHotkeyRow("full", "전체 화면");
            AddHotkeyRow("region", "영역 선택");
            AddHotkeyRow("window", "활성 창");
            AddHotkeyRow("mouse", "마우스가 있는 모니터");
            AddHotkeyRow("monitor", "모니터 선택");
            AddHotkeyRow("delay", "지연 캡처");
            AddHotkeyRow("repeat", "반복 캡처 시작/중지");
            AddHotkeyRow("scrollAuto", "스크롤 캡처 (자동)");
            AddHotkeyRow("scrollManual", "스크롤 캡처 (수동)");
            var reset = SmallBtn("기본값으로 되돌리기");
            reset.Click += (s, e) => ResetHotkeys();
            Row("", reset, "칸을 클릭하고 원하는 키 조합을 누르세요. Backspace는 지정 해제, Esc는 취소예요.\n다른 프로그램이 이미 쓰는 조합은 ⚠ 로 표시돼요. 그래도 트레이 메뉴에서 실행할 수 있어요.");
        }

        void BuildAbout()
        {
            Section("찰칵");
            var ver = new TextBlock { Text = "버전 " + Updater.Current.ToString(3), VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextSecondary") };
            Row("현재 버전", ver);
            Row("업데이트 자동 확인", Switch(S.AutoUpdate, v => { S.AutoUpdate = v; S.Save(); }), "켜 두면 새 버전이 나왔을 때 알려줘요. 알림에서 ‘업데이트’를 누르면 받아서 다시 시작해요.");

            var status = new TextBlock { Text = string.IsNullOrEmpty(S.LastUpdateCheck) ? "" : "마지막 확인  " + S.LastUpdateCheck, Foreground = Res("TextSecondary"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var check = SmallBtn("지금 확인");
            check.Click += async (s, e) =>
            {
                check.IsEnabled = false; status.Text = "확인하는 중…";
                var info = await Updater.CheckAsync(true);
                status.Text = info != null ? "새 버전 " + info.Version.ToString(3) + " 이 있어요" : "마지막 확인  " + S.LastUpdateCheck;
                check.IsEnabled = true;
            };
            Row("새 버전 확인", Horiz(status, check));

            var data = SmallBtn("열기");
            data.Click += (s, e) => ExplorerUtil.OpenFolder(AppSettings.DataDir);
            Row("설정·기록 폴더", data, "설정 파일과 오류 기록이 들어 있어요.");

            Root.Children.Add(new TextBlock
            {
                Text = "찰칵 · 가볍고 예쁜 화면 캡처", Foreground = Res("TextTertiary"), FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0)
            });
        }

        // =============================================================== 빌더
        void Section(string title)
        {
            Root.Children.Add(new TextBlock
            {
                Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Res("TextSecondary"),
                Margin = new Thickness(6, Root.Children.Count == 0 ? 10 : 20, 0, 7)
            });
            card = new StackPanel();
            var b = new Border { CornerRadius = new CornerRadius(12), Background = Res("CardBg"), Child = card, BorderBrush = Res("Separator"), BorderThickness = new Thickness(1) };
            Root.Children.Add(b);
        }

        void Row(string label, UIElement control, string desc = null, UIElement leftControl = null)
        {
            if (card.Children.Count > 0)
                card.Children.Add(new Border { Height = 1, Background = Res("Separator"), Margin = new Thickness(16, 0, 0, 0) });
            var g = new Grid { Margin = new Thickness(16, 11, 16, 11) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            if (leftControl != null) left.Children.Add(leftControl);
            if (!string.IsNullOrEmpty(label)) left.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(desc))
                left.Children.Add(new TextBlock { Text = desc, FontSize = 11.5, Foreground = Res("TextSecondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            g.Children.Add(left);
            var right = new ContentControl { Content = control, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Focusable = false, IsTabStop = false };
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
            card.Children.Add(g);
        }

        static Button SmallBtn(string text)
        {
            return new Button { Content = text, Style = (Style)Application.Current.FindResource("PillButton"), Padding = new Thickness(12, 5, 12, 5) };
        }

        static StackPanel Horiz(params UIElement[] items)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var i in items) sp.Children.Add(i);
            return sp;
        }

        static ToggleButton Switch(bool value, Action<bool> set)
        {
            var t = new ToggleButton { Style = (Style)Application.Current.FindResource("AppleSwitch"), IsChecked = value };
            t.Click += (s, e) => set(t.IsChecked == true);
            return t;
        }

        static FrameworkElement Segmented<T>(T current, Tuple<string, T>[] items, Action<T> set)
        {
            string group = Guid.NewGuid().ToString();
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var it in items)
            {
                var v = it.Item2;
                var rb = new RadioButton { Content = it.Item1, GroupName = group, Style = (Style)Application.Current.FindResource("SegButton"), IsChecked = EqualityComparer<T>.Default.Equals(v, current) };
                rb.Checked += (s, e) => set(v);
                sp.Children.Add(rb);
            }
            return new Border { CornerRadius = new CornerRadius(8), Background = (Brush)Application.Current.FindResource("FillSoft"), Padding = new Thickness(1), Child = sp };
        }

        static FrameworkElement TargetSeg(CaptureTarget cur, Action<CaptureTarget> set)
        {
            return Segmented(cur, new[]
            {
                Tuple.Create("전체 화면", CaptureTarget.Full), Tuple.Create("마우스 모니터", CaptureTarget.MouseMonitor),
                Tuple.Create("활성 창", CaptureTarget.ActiveWindow), Tuple.Create("영역", CaptureTarget.Region)
            }, set);
        }

        static FrameworkElement Stepper(double value, double min, double max, double step, string fmt, Action<double> set)
        {
            double cur = value;
            var label = new TextBlock { Width = 70, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
            Action refresh = () => label.Text = cur.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
            var minus = SmallBtn("−"); var plus = SmallBtn("＋");
            minus.Padding = new Thickness(12, 4, 12, 4); plus.Padding = new Thickness(12, 4, 12, 4);
            minus.Click += (s, e) => { cur = Math.Max(min, Math.Round(cur - step, 3)); refresh(); set(cur); };
            plus.Click += (s, e) => { cur = Math.Min(max, Math.Round(cur + step, 3)); refresh(); set(cur); };
            refresh();
            return Horiz(minus, label, plus);
        }

        void ChooseFolder()
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "저장할 폴더를 선택하세요", InitialDirectory = S.SaveFolder };
            if (d.ShowDialog(this) == true)
            {
                S.SaveFolder = d.FolderName;
                S.Save();
                folderText.Text = S.SaveFolder;
                folderText.ToolTip = S.SaveFolder;
            }
        }

        // =============================================================== 단축키
        void AddHotkeyRow(string id, string label)
        {
            var box = new TextBox { Style = (Style)Application.Current.FindResource("FlatTextBox"), IsReadOnly = true, Width = 190, Cursor = Cursors.Hand, TextAlignment = TextAlignment.Center, IsReadOnlyCaretVisible = false };
            var status = new TextBlock { Text = "", Width = 22, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("Danger"), FontWeight = FontWeights.Bold };
            hotkeyBoxes[id] = box; hotkeyStatus[id] = status;
            ShowHotkey(id);
            box.GotKeyboardFocus += (s, e) => { box.Text = "키 조합을 누르세요…"; };
            box.LostKeyboardFocus += (s, e) => ShowHotkey(id);
            box.PreviewKeyDown += (s, e) => OnHotkeyKey(id, e);
            Row(label, Horiz(box, status));
        }

        void ShowHotkey(string id)
        {
            string t;
            S.Hotkeys.TryGetValue(id, out t);
            hotkeyBoxes[id].Text = string.IsNullOrWhiteSpace(t) ? "지정 안 함" : t;
            bool ok = !App.Hotkeys.Status.ContainsKey(id) || App.Hotkeys.Status[id];
            hotkeyStatus[id].Text = ok ? "" : "⚠";
            hotkeyStatus[id].ToolTip = ok ? null : "다른 프로그램이 이미 쓰고 있어서 등록하지 못했어요. 다른 조합을 지정하거나 트레이 메뉴를 사용하세요.";
        }

        void OnHotkeyKey(string id, KeyEventArgs e)
        {
            e.Handled = true;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (k == Key.LeftCtrl || k == Key.RightCtrl || k == Key.LeftAlt || k == Key.RightAlt || k == Key.LeftShift || k == Key.RightShift ||
                k == Key.LWin || k == Key.RWin || k == Key.None || k == Key.ImeProcessed) return;
            var mods = Keyboard.Modifiers;
            if (k == Key.Escape && mods == ModifierKeys.None) { Keyboard.ClearFocus(); this.Focus(); ShowHotkey(id); return; }
            if ((k == Key.Back || k == Key.Delete) && mods == ModifierKeys.None) { Apply(id, ""); return; }
            bool isFn = k >= Key.F1 && k <= Key.F24;
            if (mods == ModifierKeys.None && !isFn && k != Key.Snapshot) { hotkeyBoxes[id].Text = "Ctrl · Alt · Shift 와 함께 눌러 주세요"; return; }
            string text = HotkeyText.ToText(mods, k);
            foreach (var kv in S.Hotkeys)
                if (kv.Key != id && string.Equals(kv.Value, text, StringComparison.OrdinalIgnoreCase))
                { hotkeyBoxes[id].Text = "이미 다른 동작에서 사용 중이에요"; return; }
            Apply(id, text);
        }

        void Apply(string id, string text)
        {
            S.Hotkeys[id] = text;
            S.Save();
            App.Hotkeys.ReloadAll();
            Keyboard.ClearFocus();
            this.Focus();
            foreach (var key in hotkeyBoxes.Keys.ToList()) ShowHotkey(key);
        }

        void ResetHotkeys()
        {
            S.Hotkeys = AppSettings.DefaultHotkeys();
            S.Save();
            App.Hotkeys.ReloadAll();
            foreach (var key in hotkeyBoxes.Keys.ToList()) ShowHotkey(key);
        }
    }
}
