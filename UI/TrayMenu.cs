using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace Chalkak
{
    internal class MenuItemDef
    {
        public string Text;
        public string Hotkey;
        public Action Click;
        public bool Separator;
        public bool Header;
        public static MenuItemDef Sep() => new MenuItemDef { Separator = true };
    }

    internal class TrayMenuWindow : FloatWindow
    {
        static TrayMenuWindow current;
        bool closing;

        public static void ShowMenu(List<MenuItemDef> items)
        {
            if (current != null) { try { current.Close(); } catch { } current = null; return; }
            var w = new TrayMenuWindow(items);
            current = w;
            w.Show();
            var cur = ScreenGrab.CursorPos();
            var mon = ScreenGrab.MonitorAt(cur.X, cur.Y);
            w.PlaceAnchored(mon.Bounds, (m, size) =>
            {
                var wa = WorkArea(m);
                int x = Math.Min(Math.Max(cur.X - size.Width / 2, wa.Left), wa.Right - size.Width + 12);
                int y = cur.Y - size.Height - 4;
                if (y < wa.Top) y = Math.Min(cur.Y + 4, wa.Bottom - size.Height);
                return new SD.Point(x, y);
            });
        }

        TrayMenuWindow(List<MenuItemDef> items) : base(true, true)
        {
            ShowActivated = true;
            var stack = new StackPanel { Margin = new Thickness(6), MinWidth = 270 };
            foreach (var it in items)
            {
                if (it.Separator)
                {
                    stack.Children.Add(new Border { Height = 1, Margin = new Thickness(10, 5, 10, 5), Background = (Brush)Application.Current.FindResource("Separator") });
                    continue;
                }
                if (it.Header)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = it.Text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 6, 10, 4),
                        Foreground = (Brush)Application.Current.FindResource("TextSecondary"), FontSize = 12
                    });
                    continue;
                }
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new TextBlock { Text = it.Text, Margin = new Thickness(0, 0, 24, 0) };
                grid.Children.Add(left);
                if (!string.IsNullOrEmpty(it.Hotkey))
                {
                    var right = new TextBlock { Text = it.Hotkey, FontSize = 11.5, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(right, 1);
                    grid.Children.Add(right);
                }
                var btn = new Button { Content = grid, Style = (Style)Application.Current.FindResource("MenuRow") };
                var click = it.Click;
                btn.Click += (s, e) =>
                {
                    CloseMenu();
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => { if (click != null) click(); }), DispatcherPriority.Background);
                };
                stack.Children.Add(btn);
            }
            Content = stack;
            Deactivated += (s, e) => CloseMenu();
            PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) CloseMenu(); };
            Loaded += (s, e) => Activate();
            // 포그라운드 잠금 등으로 비활성 이벤트를 못 받는 경우를 대비해 활성 상태를 주기적으로 확인
            var born = DateTime.UtcNow;
            var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            watch.Tick += (s, e) =>
            {
                if ((DateTime.UtcNow - born).TotalMilliseconds > 700 && !IsActive) { watch.Stop(); CloseMenu(); }
            };
            watch.Start();
            Closed += (s, e) => { watch.Stop(); if (current == this) current = null; };
        }

        void CloseMenu()
        {
            if (closing) return;
            closing = true;
            try { Close(); } catch { }
        }
    }

    internal class TrayIcon : IDisposable
    {
        readonly WF.NotifyIcon ni;

        public TrayIcon()
        {
            var res = Application.GetResourceStream(new Uri("/Assets/chalkak.ico", UriKind.Relative));
            ni = new WF.NotifyIcon { Icon = new SD.Icon(res.Stream), Text = "찰칵", Visible = true };
            ni.MouseUp += (s, e) =>
            {
                if (e.Button == WF.MouseButtons.Left || e.Button == WF.MouseButtons.Right) ShowMenu();
            };
        }

        public void SetStatus(string text) { ni.Text = string.IsNullOrEmpty(text) ? "찰칵" : ("찰칵 — " + text); }

        static string Hk(string id)
        {
            string t;
            return AppSettings.Current.Hotkeys.TryGetValue(id, out t) ? t : "";
        }

        public static void ShowMenu()
        {
            var s = AppSettings.Current;
            var items = new List<MenuItemDef>
            {
                new MenuItemDef { Header = true, Text = "캡처" },
                new MenuItemDef { Text = "전체 화면", Hotkey = Hk("full"), Click = () => CaptureService.Dispatch("full") },
                new MenuItemDef { Text = "영역 선택", Hotkey = Hk("region"), Click = () => CaptureService.Dispatch("region") },
                new MenuItemDef { Text = "활성 창", Hotkey = Hk("window"), Click = () => CaptureService.Dispatch("window") },
                new MenuItemDef { Text = "마우스가 있는 모니터", Hotkey = Hk("mouse"), Click = () => CaptureService.Dispatch("mouse") },
                new MenuItemDef { Text = "모니터 선택…", Hotkey = Hk("monitor"), Click = () => CaptureService.Dispatch("monitor") },
                MenuItemDef.Sep(),
                new MenuItemDef { Text = "지연 캡처 (" + s.DelaySeconds + "초)", Hotkey = Hk("delay"), Click = () => CaptureService.Dispatch("delay") },
                new MenuItemDef
                {
                    Text = RepeatController.Active ? "반복 캡처 중지" : "반복 캡처 시작",
                    Hotkey = Hk("repeat"), Click = () => CaptureService.Dispatch("repeat")
                },
                new MenuItemDef { Text = "스크롤 캡처 (자동)", Hotkey = Hk("scrollAuto"), Click = () => CaptureService.Dispatch("scrollAuto") },
                new MenuItemDef { Text = "스크롤 캡처 (수동)", Hotkey = Hk("scrollManual"), Click = () => CaptureService.Dispatch("scrollManual") },
                MenuItemDef.Sep(),
                new MenuItemDef { Text = "저장 폴더 열기", Click = () => ExplorerUtil.OpenFolder(AppSettings.Current.SaveFolder) },
                new MenuItemDef { Text = "보관된 반복 촬영 열기…", Click = () => Recovery.OpenPending() },
                new MenuItemDef { Text = "설정…", Click = () => SettingsWindow.ShowSingleton() },
                new MenuItemDef { Text = "업데이트 확인…", Click = () => { var _ = Updater.CheckAsync(true); } },
                MenuItemDef.Sep(),
                new MenuItemDef { Text = "찰칵 종료", Click = () => App.Quit() },
            };
            TrayMenuWindow.ShowMenu(items);
        }

        public void Dispose()
        {
            ni.Visible = false;
            ni.Dispose();
        }
    }
}
